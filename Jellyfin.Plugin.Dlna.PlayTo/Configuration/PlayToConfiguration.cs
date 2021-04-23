using System;
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
        /// Gets or sets a value indicating whether detailed SSDP logs are sent to the console/log.
        /// "Emby.Dlna": "Debug" must be set in logging.default.json for this property to have any effect.
        /// </summary>
        public bool EnableSsdpTracing { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether an IP address is to be used to filter the detailed ssdp logs that are being sent to the console/log.
        /// If the setting "Emby.Dlna": "Debug" must be set in logging.default.json for this property to work.
        /// </summary>
        public string SsdpTracingFilter { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the range of UDP ports to use in communications as well as 1900.
        /// </summary>
        public string UdpPortRange { get; set; } = "49152-65535";

        /// <summary>
        /// Gets or sets a value indicating whether network discovery should be used to detect devices.
        /// </summary>
        public bool UseNetworkDiscovery { get; set; }

        /// <summary>
        /// Gets or sets the list of devices which are not permitted to connect.
        /// </summary>
        public string[] StaticDevices { get; set; } = Array.Empty<string>();
    }
}
