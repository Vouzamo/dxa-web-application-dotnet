﻿using Sdl.Web.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Web.Script.Serialization;
using Sdl.Web.Common;
using Sdl.Web.Common.Interfaces;


namespace Sdl.Web.Mvc
{
    /// <summary>
    /// Abstract Base Content Provider
    /// </summary>
    public abstract class BaseContentProvider : IContentProvider
    {
        public IContentResolver ContentResolver { get; set; }
        public BaseContentProvider()
        {
        }

        //These need to be implemented by the specific content provider
        public abstract string GetPageContent(string url);
        public abstract object GetEntityModel(string id);
        public abstract string GetEntityContent(string url);

        protected abstract object GetPageModelFromUrl(string url);
        
        public object GetPageModel(string url)
        {
            //We can have a couple of tries to get the page model if there is no file extension on the url request, but it does not end in a slash:
            //1. Try adding the default extension, so /news becomes /news.html
            var model = GetPageModelFromUrl(ParseUrl(url));
            if (model == null && !url.EndsWith("/") && url.LastIndexOf(".", StringComparison.Ordinal) <= url.LastIndexOf("/", StringComparison.Ordinal))
            {
                //2. Try adding the default page, so /news becomes /news/index.html
                model = GetPageModelFromUrl(ParseUrl(url + "/"));
            }
            return model;
        }

        private static Dictionary<Type, IModelBuilder> _modelBuilders = null;
        public static Dictionary<Type, IModelBuilder> ModelBuilders
        {
            get
            {
                if (_modelBuilders == null)
                {
                    //TODO hardcoded and empty for now
                    _modelBuilders = new Dictionary<Type, IModelBuilder>();
                }
                return _modelBuilders;
            }
            set
            {
                _modelBuilders = value;
            }
        }
        public IModelBuilder DefaultModelBuilder { get; set; }
        
        public virtual string ParseUrl(string url)
        {
            var defaultPageFileName = ContentResolver.DefaultPageName;
            return String.IsNullOrEmpty(url) ? defaultPageFileName : (url.EndsWith("/") ? url + defaultPageFileName : url += ContentResolver.DefaultExtension);
        }
        
        public virtual object MapModel(object data, ModelType modelType, Type viewModeltype = null)
        {
            List<object> includes = GetIncludesFromModel(data, modelType);
            MvcData viewData = null;
            viewData = ContentResolver.ResolveMvcData(data);
            if (viewModeltype == null)
            {
                var key = String.Format("{0}:{1}", viewData.AreaName, viewData.ViewName);
                viewModeltype = Configuration.ViewModelRegistry.ContainsKey(key) ? Configuration.ViewModelRegistry[key] : null;
            }
            if (viewModeltype!=null)
            {
                IModelBuilder builder = DefaultModelBuilder;
                if (ModelBuilders.ContainsKey(viewModeltype))
                {
                    builder = ModelBuilders[viewModeltype];
                }
                return builder.Create(data, viewModeltype, includes);
            }
            else
            {
                var ex = new Exception(String.Format("Cannot find view model for entity in ViewModelRegistry. Check the view is strongly typed using the @model statement"));
                Log.Error(ex);
                throw ex;
            }
        }

        protected abstract List<object> GetIncludesFromModel(object data, ModelType modelType);

        
        public abstract void PopulateDynamicList(ContentList<Teaser> list);



        public virtual object GetNavigationModel(string url)
        {
            string key = "navigation-" + url;
            //This is a temporary measure to cache the navigationModel per request to not retrieve and serialize 3 times per request. Comprehensive caching strategy pending
            if (HttpContext.Current.Items[key] == null)
            {
                string navigationJsonString = GetPageContent(url);
                var navigationModel = new JavaScriptSerializer().Deserialize<SitemapItem>(navigationJsonString);
                HttpContext.Current.Items[key] = navigationModel;
            }
            return HttpContext.Current.Items[key] as SitemapItem;
        }
    }
}
