using Jellyfin.Plugin.DlnaPlayTo.Main;
using Jellyfin.Plugin.DlnaPlayTo.Profile;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DlnaPlayTo.Registrator
{
    /// <summary>
    /// Defines the <see cref="PlayToRegistrator" />.
    /// </summary>
    public class PlayToRegistrator : IPluginServiceRegistrator
    {
        /// <summary>
        /// Register the classes with DI.
        /// </summary>
        /// <param name="serviceCollection">The <see cref="IServiceCollection"/>.</param>
        public void RegisterServices(IServiceCollection serviceCollection)
        {
            serviceCollection.AddSingleton<ProfileManager>();
            serviceCollection.AddSingleton<ISessionController, PlayToController>();
        }
    }
}
