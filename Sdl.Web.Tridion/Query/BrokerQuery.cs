﻿using Sdl.Web.Mvc.Common;
using Sdl.Web.Mvc.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Tridion.ContentDelivery.DynamicContent;
using Tridion.ContentDelivery.DynamicContent.Query;
using Tridion.ContentDelivery.Meta;
using Tridion.ContentDelivery.Taxonomies;

namespace Sdl.Web.Tridion
{
    public class BrokerQuery
    {
        public int SchemaId { get; set; }
        public int PublicationId { get; set; }
        public int MaxResults { get; set; }
        public string Sort { get; set; }
        public int Start { get; set; }
        public int PageSize { get; set; }
        public Dictionary<string, List<string>> KeywordFilters { get; set; }
        public bool HasMore { get; set; }
        public List<Teaser> ExecuteQuery()
        {
            Criteria criteria = BuildCriteria();
            Query query = new Query(criteria);
            Sort = Sort ?? "dateCreated";
            query.AddSorting(GetSortParameter());
            if (MaxResults > 0)
            {
                query.SetResultFilter(new LimitFilter(MaxResults));
            }
            if (PageSize > 0)
            {
                //We set the page size to one more than what we need, to see if there are more pages to come...
                query.SetResultFilter(new PagingFilter(Start, PageSize + 1));
            }
            try
            {
                ComponentMetaFactory cmf = new ComponentMetaFactory(this.PublicationId);
                var items = query.ExecuteQuery();
                var results = new List<Teaser>();
                var ids = query.ExecuteQuery();
                HasMore = ids.Length>PageSize;
                int count = 0;
                foreach (string compId in ids)
                {
                    if (count < PageSize)
                    {
                        var compMeta = cmf.GetMeta(compId);
                        if (compMeta != null)
                        {
                            results.Add(GetTeaserFromMeta(compMeta));
                        }
                        count++;
                    }
                    else
                    {
                        break;
                    }
                }
                return results;
            }
            catch (Exception ex)
            {
                throw new Exception(String.Format("Error running broker query: {0}.", ex.Message ), ex);
            }
        }

        private Teaser GetTeaserFromMeta(IComponentMeta compMeta)
        {
            Teaser result = new Teaser();
            result.Link = new Link { Url = String.Format("tcm:{0}-{1}", compMeta.PublicationId, compMeta.Id) };
            result.Date = GetDateFromCustomMeta(compMeta.CustomMeta, "dateCreated") ?? compMeta.LastPublicationDate;
            result.Headline = GetTextFromCustomMeta(compMeta.CustomMeta, "name") ?? compMeta.Title;
            result.Text = GetTextFromCustomMeta(compMeta.CustomMeta, "introText");
            return result;
        }

        private string GetTextFromCustomMeta(CustomMeta meta, string fieldname)
        {
            if (meta.NameValues.Contains(fieldname))
            {
                return meta.GetValue(fieldname).ToString();
            }
            return null;
        }

        private DateTime? GetDateFromCustomMeta(CustomMeta meta, string fieldname)
        {
            if (meta.NameValues.Contains(fieldname))
            {
                return meta.GetValue(fieldname) as DateTime?;
            }
            return null;
        }

        /// <summary>
        /// Sets the keyword filters using a list of keyword uri strings
        /// </summary>
        /// <param name="encodedFilters"></param>
        public void SetKeywordFilters(List<String> keywordUris)
        {
            var taxonomyFactory = new TaxonomyFactory();
            List<Keyword> keywords = new List<Keyword>();
            foreach (var kwUri in keywordUris)
            {
                var kw = taxonomyFactory.GetTaxonomyKeyword(kwUri);
                if (kw != null)
                {
                    keywords.Add(kw);
                }
            }
            SetKeywordFilters(keywords);
        }

        /// <summary>
        /// Sets the keyword filters using a list of keyword objects
        /// </summary>
        /// <param name="encodedFilters"></param>
        public void SetKeywordFilters(List<Keyword> keywords)
        {
            if (KeywordFilters == null)
            {
                KeywordFilters = new Dictionary<string, List<string>>();
            }
            foreach (var kw in keywords)
            {
                var taxonomy = kw.TaxonomyUri;
                if (!KeywordFilters.ContainsKey(taxonomy))
                {
                    KeywordFilters.Add(taxonomy, new List<string>());
                }
                KeywordFilters[taxonomy].Add(kw.KeywordUri);
            }
        }

        public static Keyword LoadKeyword(string keywordUri)
        {
            var taxonomyFactory = new TaxonomyFactory();
            return taxonomyFactory.GetTaxonomyKeyword(keywordUri);
        }

        /// <summary>
        /// Gets a list of keyword objects based on their URIs
        /// </summary>
        /// <param name="keywordUris"></param>
        /// <returns></returns>
        public static List<Keyword> LoadKeywords(List<string> keywordUris)
        {
            var res = new List<Keyword>();
            var taxonomyFactory = new TaxonomyFactory();
            foreach (var uri in keywordUris)
            {
                var kw = taxonomyFactory.GetTaxonomyKeyword(uri);
                if (kw != null)
                {
                    res.Add(kw);
                }
            }
            return res;
        }

        private Criteria BuildCriteria()
        {
            var children = new List<Criteria>();
            children.Add(new ItemTypeCriteria(16));
            if (SchemaId > 0)
            {
                children.Add(new ItemSchemaCriteria(SchemaId));
            }
            if (PublicationId > 0)
            {
                children.Add(new PublicationCriteria(PublicationId));
            }
            if (KeywordFilters != null)
            {
                foreach (var taxonomy in KeywordFilters.Keys)
                {
                    foreach (var keyword in KeywordFilters[taxonomy])
                    {
                        children.Add(new TaxonomyKeywordCriteria(taxonomy, keyword, true));
                    }
                }
            }
            return new AndCriteria(children.ToArray());
        }

        private SortParameter GetSortParameter()
        {
            var dir = Sort.ToLower().EndsWith("asc") ? SortParameter.Ascending : SortParameter.Descending;
            return new SortParameter(GetSortColumn(), dir);
        }

        private SortColumn GetSortColumn()
        {
            //TODO add more options if required
            var sort = Sort.ToLower().Trim();
            var pos = Sort.Trim().IndexOf(" ");
            sort = pos > 0 ? Sort.Trim().Substring(0, pos) : Sort.Trim();
            switch (sort.ToLower())
            {
                case "title":
                    return SortParameter.ItemTitle;
                case "pubdate":
                    return SortParameter.ItemLastPublishedDate;
                default:
                    //Default is to assume that its a custom metadata date field;
                    return new CustomMetaKeyColumn(Sort, MetadataType.DATE);
            }
        }
    }
}