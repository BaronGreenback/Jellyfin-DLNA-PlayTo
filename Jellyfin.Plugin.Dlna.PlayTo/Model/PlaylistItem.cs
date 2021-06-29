using MediaBrowser.Model.Dlna;

namespace Jellyfin.Plugin.Dlna.PlayTo.Model
{
    /// <summary>
    /// Defines the <see cref="PlaylistItem" />.
    /// </summary>
    public class PlaylistItem
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PlaylistItem"/> class.
        /// </summary>
        /// <param name="streamInfo">The <see cref="StreamInfo"/>.</param>
        /// <param name="profile">The <see cref="DeviceProfile"/>.</param>
        /// <param name="mediaType">The <see cref="DlnaProfileType"/>.</param>
        public PlaylistItem(StreamInfo streamInfo, DeviceProfile profile, DlnaProfileType mediaType)
        {
            StreamInfo = streamInfo;
            Profile = profile;
            Didl = string.Empty;
            MediaType = mediaType;
        }

        /// <summary>
        /// Gets or sets the stream's Url.
        /// </summary>
        public string? StreamUrl { get; set; }

        /// <summary>
        /// Gets or sets the Didl xml.
        /// </summary>
        public string Didl { get; set; }

        /// <summary>
        /// Gets the stream information.
        /// </summary>
        public StreamInfo StreamInfo { get; }

        /// <summary>
        /// Gets the device profile.
        /// </summary>
        public DeviceProfile Profile { get; }

        /// <summary>
        /// Gets the media type of this item.
        /// </summary>
        public DlnaProfileType MediaType { get; }
    }
}
