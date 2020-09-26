using System.Collections.Generic;
using System.Xml.Linq;
using Jellyfin.Plugin.DlnaPlayTo.Main;
using Jellyfin.Plugin.Ssdp.Model;
using MediaBrowser.Model.Dlna;

namespace Jellyfin.Plugin.DlnaPlayTo.Model
{
    /// <summary>
    /// Defines the <see cref="PlayToDeviceInfo" />.
    /// </summary>
    internal class PlayToDeviceInfo
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PlayToDeviceInfo"/> class.
        /// </summary>
        /// <param name="name">Name of the device.</param>
        /// <param name="baseUrl">Base url of the device.</param>
        /// <param name="uuid">Uuid of the device.</param>
        public PlayToDeviceInfo(string name, string baseUrl, string uuid)
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Generic Device" : name;
            BaseUrl = baseUrl;
            Uuid = uuid;
        }

        /// <summary>
        /// Gets or sets the Uuid.
        /// </summary>
        public string Uuid { get; set; }

        /// <summary>
        /// Gets or sets the Name.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets the Model Name.
        /// </summary>
        public string? ModelName { get; set; }

        /// <summary>
        /// Gets or sets the Model Number.
        /// </summary>
        public string? ModelNumber { get; set; }

        /// <summary>
        /// Gets or sets the Model Description.
        /// </summary>
        public string? ModelDescription { get; set; }

        /// <summary>
        /// Gets or sets the Model Url.
        /// </summary>
        public string? ModelUrl { get; set; }

        /// <summary>
        /// Gets or sets the Manufacturer.
        /// </summary>
        public string? Manufacturer { get; set; }

        /// <summary>
        /// Gets or sets the Serial Number.
        /// </summary>
        public string? SerialNumber { get; set; }

        /// <summary>
        /// Gets or sets the Manufacturer Url.
        /// </summary>
        public string? ManufacturerUrl { get; set; }

        // <summary>
        // Gets or sets the Presentation Url.
        // </summary>
        // public string PresentationUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Base Url.
        /// </summary>
        public string BaseUrl { get; set; }

        /// <summary>
        /// Gets the services the device supports.
        /// </summary>
        public DeviceService?[] Services { get; } = { null, null, null };

        /// <summary>
        /// Gets the Render Control service.
        /// </summary>
        public DeviceService? RenderControl => Services[(int)ServiceType.RenderControl];

        /// <summary>
        /// Gets the Connection Manager service.
        /// </summary>
        public DeviceService? ConnectionManager => Services[(int)ServiceType.ConnectionManager];

        /// <summary>
        /// Gets the AVTransport service.
        /// </summary>
        public DeviceService? AVTransport => Services[(int)ServiceType.AVTransport];

        /// <summary>
        /// Gets or sets the capabilities of the device.
        /// </summary>
        public string? Capabilities { get; set; }

        /// <summary>
        /// Converts this item to a <see cref="DeviceIdentification"/>.
        /// </summary>
        /// <returns>The <see cref="DeviceIdentification"/>.</returns>
        public DeviceIdentification ToDeviceIdentification()
        {
            return new DeviceIdentification
            {
                Manufacturer = Manufacturer ?? string.Empty,
                ModelName = ModelName ?? string.Empty,
                ModelNumber = ModelNumber ?? string.Empty,
                FriendlyName = Name,
                ManufacturerUrl = ManufacturerUrl ?? string.Empty,
                ModelUrl = ModelUrl ?? string.Empty,
                ModelDescription = ModelDescription ?? string.Empty,
                SerialNumber = SerialNumber ?? string.Empty
            };
        }
    }
}
