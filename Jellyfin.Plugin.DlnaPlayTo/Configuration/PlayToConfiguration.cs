using System.Text.Json.Serialization;
using Jellyfin.Plugin.Ssdp.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DlnaPlayTo.Configuration
{
    /// <summary>
    /// Defines the <see cref="PlayToConfiguration" />.
    /// </summary>
    public class PlayToConfiguration : BasePluginConfiguration
    {
        private IConfigurationManager? _config;

        /// <summary>
        /// Gets or sets the maximum wait time for http responses from devices (ms).
        /// </summary>
        public static int CtsTimeout { get; set; } = 10000;

        /// <summary>
        /// Gets or sets a value indicating whether detailed playTo debug logs are sent to the console/log.
        /// If the setting "Emby.Dlna.PlayTo": "Debug" must be set in logging.default.json for this property to work.
        /// </summary>
        public bool EnablePlayToDebug { get; set; }

        /// <summary>
        /// Gets or sets the initial ssdp client discovery interval time (in seconds).
        /// Once a device has been detected, this discovery interval will drop to <seealso cref="ClientNotificationInterval"/> seconds.
        /// </summary>
        public int ClientDiscoveryIntervalSeconds { get; set; } = 15;

        /// <summary>
        /// Gets or sets the continuous ssdp client discovery interval time (in seconds).
        /// </summary>
        public int ClientNotificationInterval { get; set; } = 1800;

        /// <summary>
        /// Gets or sets a value indicating whether disk profiles should be created for devices that are unknown.
        /// </summary>
        public bool AutoCreatePlayToProfiles { get; set; }

        /// <summary>
        /// Gets or sets the amount of time given for the device to respond in ms.
        /// Setting this too low will result in JF not knowing the device is streaming.
        /// Note: Some devices don't respond until the device starts streaming.
        /// </summary>
        public int CommunicationTimeout { get; set; } = 8000;

        /// <summary>
        /// Gets or sets the USERAGENT that is sent to devices.
        /// </summary>
        public string UserAgent { get; set; } = "Microsoft-Windows/6.2 UPnP/1.0 Microsoft-DLNA DLNADOC/1.50";

        /// <summary>
        /// Gets or sets the frequency of the device polling (ms).
        /// </summary>
        public int TimerInterval { get; set; } = 30000;

        /// <summary>
        /// Gets or sets a value indicating the command queue processing frequency (ms).
        /// </summary>
        public int QueueInterval { get; set; } = 1000;

        /// <summary>
        /// Gets or sets the friendly name that is used.
        /// </summary>
        public string FriendlyName { get; set; } = "Jellyfin";

        /// <summary>
        /// Gets the SSDP Configuration settings.
        /// </summary>
        [JsonIgnore]
        public SsdpConfiguration? Configuration => _config?.GetConfiguration<SsdpConfiguration>("ssdp");

        /// <summary>
        /// Gets or sets a value indicating whether detailed SSDP logs are sent to the console/log.
        /// "Emby.Dlna": "Debug" must be set in logging.default.json for this property to have any effect.
        /// </summary>
        [JsonIgnore]
        public bool EnableSsdpTracing
        {
            get
            {
                return Configuration?.EnableSsdpTracing ?? false;
            }

            set
            {
                if (Configuration != null)
                {
                    Configuration.EnableSsdpTracing = value;
                }
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether an IP address is to be used to filter the detailed ssdp logs that are being sent to the console/log.
        /// If the setting "Emby.Dlna": "Debug" must be set in logging.default.json for this property to work.
        /// </summary>
        [JsonIgnore]
        public string SsdpTracingFilter
        {
            get
            {
                return Configuration?.SsdpTracingFilter ?? string.Empty;
            }

            set
            {
                if (Configuration != null)
                {
                    Configuration.SsdpTracingFilter = value;
                }
            }
        }

        /// <summary>
        /// Gets or sets the range of UDP ports to use in communications as well as 1900.
        /// </summary>
        [JsonIgnore]
        public string UdpPortRange
        {
            get
            {
                return Configuration?.UdpPortRange ?? string.Empty;
            }

            set
            {
                if (Configuration != null)
                {
                    Configuration.UdpPortRange = value;
                }
            }
        }

        /// <summary>
        /// Defines the configuration manager to use.
        /// </summary>
        /// <param name="config">The <see cref="IConfigurationManager"/> instance.</param>
        internal void SetConfigurationManager(IConfigurationManager config)
        {
            _config = config;
        }
    }
}
