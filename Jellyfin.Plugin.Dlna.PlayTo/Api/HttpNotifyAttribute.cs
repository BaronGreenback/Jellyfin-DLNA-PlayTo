using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Jellyfin.Plugin.Dlna.PlayTo.Api
{
    /// <summary>
    /// Identifies an action that supports the HTTP GET method.
    /// </summary>
    public class HttpNotifyAttribute : HttpMethodAttribute
    {
        private static readonly IEnumerable<string> _supportedMethods = new[] { "NOTIFY" };

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpNotifyAttribute"/> class.
        /// </summary>
        /// <param name="template">The route template. May not be null.</param>
        public HttpNotifyAttribute(string template)
            : base(_supportedMethods, template)
        {
            if (template == null)
            {
                throw new ArgumentNullException(nameof(template));
            }
        }
    }
}
