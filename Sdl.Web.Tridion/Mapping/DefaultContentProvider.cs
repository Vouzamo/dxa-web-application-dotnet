﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;
using DD4T.ContentModel;
using DD4T.ContentModel.Factories;
using Sdl.Web.Common;
using Sdl.Web.Common.Configuration;
using Sdl.Web.Common.Interfaces;
using Sdl.Web.Common.Logging;
using Sdl.Web.Common.Models;
using Sdl.Web.Tridion.Statics;
using Sdl.Web.Tridion.ContentManager;
using Sdl.Web.Tridion.Query;
using Tridion.ContentDelivery.Meta;
using IComponentMeta = Tridion.ContentDelivery.Meta.IComponentMeta;
using IPage = DD4T.ContentModel.IPage;

namespace Sdl.Web.Tridion.Mapping
{
    /// <summary>
    /// Default Content Provider implementation (DD4T-based).
    /// </summary>
    public class DefaultContentProvider : IContentProvider, IRawDataProvider
    {
        #region IContentProvider members
        /// <summary>
        /// Gets a Page Model for a given URL.
        /// </summary>
        /// <param name="urlPath">The URL path (unescaped).</param>
        /// <param name="localization">The context Localization.</param>
        /// <param name="addIncludes">Indicates whether include Pages should be expanded.</param>
        /// <returns>The Page Model.</returns>
        /// <exception cref="DxaItemNotFoundException">If no Page Model exists for the given URL.</exception>
        public virtual PageModel GetPageModel(string urlPath, Localization localization, bool addIncludes)
        {
            using (new Tracer(urlPath, localization, addIncludes))
            {
                if (urlPath == null)
                {
                    urlPath = "/";
                }
                else if (!urlPath.StartsWith("/"))
                {
                    urlPath = "/" + urlPath;
                }

                IPage page = GetPage(urlPath, localization);
                if (page == null && !urlPath.EndsWith("/"))
                {
                    // This may be a SG URL path; try if the index page exists.
                    urlPath += Constants.IndexPageUrlSuffix;
                    page = GetPage(urlPath, localization);
                }
                else if (urlPath.EndsWith("/"))
                {
                    urlPath += Constants.DefaultExtensionLessPageName;
                }

                if (page == null)
                {
                    throw new DxaItemNotFoundException(urlPath, localization.Id);
                }

                IPage[] includes = addIncludes ? GetIncludesFromModel(page, localization).ToArray() : new IPage[0];

                List<string> dependencies = new List<string>() { page.Id };
                dependencies.AddRange(includes.Select(p => p.Id));

                PageModel result = null;
                if (CacheRegions.IsViewModelCachingEnabled)
                {
                    PageModel cachedPageModel = SiteConfiguration.CacheProvider.GetOrAdd(
                        string.Format("{0}:{1}", page.Id, addIncludes), // Cache Page Models with and without includes separately
                        CacheRegions.PageModel,
                        () =>
                        {
                            PageModel pageModel = ModelBuilderPipeline.CreatePageModel(page, includes, localization);
                            pageModel.Url = urlPath;
                            if (pageModel.NoCache)
                            {
                                result = pageModel;
                                return null;
                            }
                            return pageModel;
                        },
                        dependencies
                        );

                    if (cachedPageModel != null)
                    {
                        // Don't return the cached Page Model itself, because we don't want dynamic logic to modify the cached state.
                        result = (PageModel) cachedPageModel.DeepCopy();
                    }
                }
                else
                {
                    result = ModelBuilderPipeline.CreatePageModel(page, includes, localization);
                    result.Url = urlPath;
                }

                if (SiteConfiguration.ConditionalEntityEvaluator != null)
                {
                    result.FilterConditionalEntities(localization);
                }

                return result;
            }
        }

        /// <summary>
        /// Gets an Entity Model for a given Entity Identifier.
        /// </summary>
        /// <param name="id">The Entity Identifier in format ComponentID-TemplateID.</param>
        /// <param name="localization">The context Localization.</param>
        /// <returns>The Entity Model.</returns>
        /// <exception cref="DxaItemNotFoundException">If no Entity Model exists for the given URL.</exception>
        /// <remarks>
        /// Since we can't obtain CT metadata for DCPs, we obtain the View Name from the CT Title.
        /// </remarks>
        public virtual EntityModel GetEntityModel(string id, Localization localization)
        {
            using (new Tracer(id, localization))
            {
                string[] idParts = id.Split('-');
                if (idParts.Length != 2)
                {
                    throw new DxaException(String.Format("Invalid Entity Identifier '{0}'. Must be in format ComponentID-TemplateID.", id));
                }

                string componentUri = localization.GetCmUri(idParts[0]);
                string templateUri = localization.GetCmUri(idParts[1], (int) ItemType.ComponentTemplate);

                IComponentPresentationFactory componentPresentationFactory = DD4TFactoryCache.GetComponentPresentationFactory(localization);
                IComponentPresentation dcp;
                if (!componentPresentationFactory.TryGetComponentPresentation(out dcp, componentUri, templateUri))
                {
                    throw new DxaItemNotFoundException(id, localization.Id);
                }

                EntityModel result;
                if (CacheRegions.IsViewModelCachingEnabled)
                {
                    EntityModel cachedEntityModel = SiteConfiguration.CacheProvider.GetOrAdd(
                        string.Format("{0}-{1}", id, localization.Id), // key
                        CacheRegions.EntityModel,
                        () => ModelBuilderPipeline.CreateEntityModel(dcp, localization),
                        dependencies: new[] {componentUri}
                        );

                    // Don't return the cached Entity Model itself, because we don't want dynamic logic to modify the cached state.
                    result = (EntityModel) cachedEntityModel.DeepCopy();
                }
                else
                {
                    result = ModelBuilderPipeline.CreateEntityModel(dcp, localization);
                }

                if (result.XpmMetadata != null)
                {
                    // Entity Models requested through this method are per definition "query based" in XPM terminology.
                    result.XpmMetadata["IsQueryBased"] = true;
                }
                return result;
            }
        }

        /// <summary>
        /// Gets a Static Content Item for a given URL path.
        /// </summary>
        /// <param name="urlPath">The URL path (unescaped).</param>
        /// <param name="localization">The context Localization.</param>
        /// <returns>The Static Content Item.</returns>
        public StaticContentItem GetStaticContentItem(string urlPath, Localization localization)
        {
            using (new Tracer(urlPath, localization))
            {
                string localFilePath = BinaryFileManager.Instance.GetCachedFile(urlPath, localization);
 
                return new StaticContentItem(
                    new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan),
                    MimeMapping.GetMimeMapping(localFilePath),
                    File.GetLastWriteTime(localFilePath), 
                    Encoding.UTF8
                    );
            }
        }

        /// <summary>
        /// Populates a Dynamic List by executing the query it specifies.
        /// </summary>
        /// <param name="dynamicList">The Dynamic List which specifies the query and is to be populated.</param>
        /// <param name="localization">The context Localization.</param>
        public virtual void PopulateDynamicList(DynamicList dynamicList, Localization localization)
        {
            using (new Tracer(dynamicList, localization))
            {
                SimpleBrokerQuery simpleBrokerQuery = dynamicList.GetQuery(localization) as SimpleBrokerQuery;
                if (simpleBrokerQuery == null)
                {
                    throw new DxaException($"Unexpected result from {dynamicList.GetType().Name}.GetQuery: {dynamicList.GetQuery(localization)}");
                }

                BrokerQuery brokerQuery = new BrokerQuery(simpleBrokerQuery);
                string[] componentUris = brokerQuery.ExecuteQuery().ToArray();
                Log.Debug($"Broker Query returned {componentUris.Length} results. HasMore={brokerQuery.HasMore}");

                if (componentUris.Length > 0)
                {
                    Type resultType = dynamicList.ResultType;
                    ComponentMetaFactory componentMetaFactory = new ComponentMetaFactory(localization.GetCmUri());
                    dynamicList.QueryResults = componentUris
                        .Select(c => ModelBuilderPipeline.CreateEntityModel(CreateComponent(componentMetaFactory.GetMeta(c)), resultType, localization))
                        .ToList();
                }

                dynamicList.HasMore = brokerQuery.HasMore;
            }
        }

        #endregion

        #region IRawDataProvider members
        public virtual string GetPageContent(string urlPath, Localization localization)
        {
            string cmUrl = GetCmUrl(urlPath);

            using (new Tracer(urlPath, cmUrl))
            {
                IPageFactory pageFactory = DD4TFactoryCache.GetPageFactory(localization);
                string result;
                pageFactory.TryFindPageContent(GetCmUrl(urlPath), out result);
                return result;
            }
        }
        #endregion

        /// <summary>
        /// Creates a lightweight DD4T Component that contains enough information such that the semantic model builder can cope and build a strongly typed model from it.
        /// </summary>
        /// <param name="componentMeta">A <see cref="DD4T.ContentModel.IComponentMeta"/> instance obtained from CD API.</param>
        /// <returns>A DD4T Component.</returns>
        private static IComponent CreateComponent(IComponentMeta componentMeta)
        {
            Component component = new Component
            {
                Id = $"tcm:{componentMeta.PublicationId}-{componentMeta.Id}",
                LastPublishedDate = componentMeta.LastPublicationDate,
                RevisionDate = componentMeta.ModificationDate,
                Schema = new Schema
                {
                    PublicationId = componentMeta.PublicationId.ToString(),
                    Id = $"tcm:{componentMeta.PublicationId}-{componentMeta.SchemaId}"
                },
                MetadataFields = new FieldSet()
            };

            FieldSet metadataFields = new FieldSet();
            component.MetadataFields.Add("standardMeta", new Field { EmbeddedValues = new List<FieldSet> { metadataFields } });
            foreach (DictionaryEntry de in componentMeta.CustomMeta.NameValues)
            {
                object v = ((NameValuePair) de.Value).Value;
                if (v == null)
                {
                    continue;
                }
                string k = de.Key.ToString();
                metadataFields.Add(k, new Field { Name = k, Values = new List<string> { v.ToString() }});
            }

            // The semantic mapping requires that some metadata fields exist. This may not be the case so we map some component meta properties onto them
            // if they don't exist.
            if (!metadataFields.ContainsKey("dateCreated"))
            {
                metadataFields.Add("dateCreated", new Field { Name = "dateCreated", DateTimeValues = new List<DateTime> { componentMeta.LastPublicationDate } });
            }

            if (!metadataFields.ContainsKey("name"))
            {
                metadataFields.Add("name", new Field { Name = "name", Values = new List<string> { componentMeta.Title } });
            }

            return component;
        }


        /// <summary>
        /// Converts a request URL path into a CMS URL (for example adding default page name and file extension)
        /// </summary>
        /// <param name="urlPath">The request URL path (unescaped)</param>
        /// <returns>A CMS URL (UTF-8 URL escaped)</returns>
        protected virtual string GetCmUrl(string urlPath)
        {
            string cmUrl;
            if (String.IsNullOrEmpty(urlPath))
            {
                cmUrl = Constants.DefaultPageName;
            }
            else
            {
                cmUrl = Uri.EscapeUriString(urlPath);
            }

            if (cmUrl.EndsWith("/"))
            {
                cmUrl = cmUrl + Constants.DefaultPageName;
            }
            if (!Path.HasExtension(cmUrl))
            {
                cmUrl = cmUrl + Constants.DefaultExtension;
            }
            if (!cmUrl.StartsWith("/"))
            {
                cmUrl = "/" + cmUrl;
            }
            return cmUrl;
        }

        protected virtual IPage GetPage(string urlPath, Localization localization)
        {
            string cmUrl = GetCmUrl(urlPath);

            using (new Tracer(urlPath, localization, cmUrl))
            {
                IPageFactory pageFactory = DD4TFactoryCache.GetPageFactory(localization);
                IPage result;
                pageFactory.TryFindPage(cmUrl, out result);
                return result;
            }
        }

        protected virtual IEnumerable<IPage> GetIncludesFromModel(IPage page, Localization localization)
        {
            using (new Tracer(page.Id, localization))
            {
                List<IPage> result = new List<IPage>();
                string[] pageTemplateTcmUriParts = page.PageTemplate.Id.Split('-');
                IEnumerable<string> includePageUrls = localization.GetIncludePageUrls(pageTemplateTcmUriParts[1]);
                foreach (string includePageUrl in includePageUrls)
                {
                    IPage includePage = GetPage(localization.GetAbsoluteUrlPath(includePageUrl), localization);
                    if (includePage == null)
                    {
                        Log.Error("Include Page '{0}' not found.", includePageUrl);
                        continue;
                    }
                    result.Add(includePage);
                }
                return result;
            }
        }
    }
}
