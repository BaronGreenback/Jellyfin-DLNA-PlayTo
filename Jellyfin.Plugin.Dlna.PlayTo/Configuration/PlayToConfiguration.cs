using System;
using System.Xml.Serialization;
using Jellyfin.Plugin.Dlna.Configuration;
using Jellyfin.Plugin.Dlna.Model;
using Jellyfin.Plugin.Dlna.Ssdp;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Dlna.PlayTo.Configuration
{
    /// <summary>
    /// Defines the <see cref="PlayToConfiguration" />.
    /// </summary>
    public class PlayToConfiguration : BasePluginConfiguration
    {
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
        /// Gets or sets the amount of time given for the device to respond in ms.
        /// Setting this too low will result in JF not knowing the device is streaming.
        /// Note: Some devices don't respond until the device starts streaming.
        /// </summary>
        public int CommunicationTimeout { get; set; } = 8000;

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
        /// Gets or sets a value indicating whether network discovery should be used to detect devices.
        /// </summary>
        public bool UseNetworkDiscovery { get; set; }

        /// <summary>
        /// Gets or sets the list of devices which are static.
        /// </summary>
        public string[] StaticDevices { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Gets or sets a value indicating whether detailed SSDP logs are sent to the console/log.
        /// "Emby.Dlna": "Debug" must be set in logging.default.json for this property to have any effect.
        /// </summary>
        [XmlIgnoreAttribute]
        public bool EnableSsdpTracing
        {
            get => SsdpConfig.EnableSsdpTracing;
            set => SsdpConfig.EnableSsdpTracing = value;
        }

        /// <summary>
        /// Gets or sets a value indicating whether an IP address is to be used to filter the detailed ssdp logs that are being sent to the console/log.
        /// If the setting "Emby.Dlna": "Debug" must be set in logging.default.json for this property to work.
        /// </summary>
        [XmlIgnoreAttribute]
        public string SsdpTracingFilter
        {
            get => SsdpConfig.SsdpTracingFilter;
            set => SsdpConfig.SsdpTracingFilter = value;
        }

        /// <summary>
        /// Gets or sets the range of UDP ports to use in communications as well as 1900.
        /// </summary>
        [XmlIgnoreAttribute]
        public string UdpPortRange
        {
            get => SsdpConfig.UdpPortRange;
            set => SsdpConfig.UdpPortRange = value;
        }

        /// <summary>
        /// Gets or sets the USERAGENT that is sent to devices.
        /// </summary>
        [XmlIgnoreAttribute]
        public string UserAgent
        {
            get => SsdpConfig.UserAgent;
            set => SsdpConfig.UserAgent = value;
        }

        /// <summary>
        /// Gets or sets the Dlna version that the SSDP server supports.
        /// </summary>
        [XmlIgnore]
        public DlnaVersion DlnaVersion
        {
            get => SsdpConfig.DlnaVersion;
            set => SsdpConfig.DlnaVersion = value;
        }

        private static SsdpConfiguration SsdpConfig => SsdpServer.GetInstance().Configuration;
    }
}
