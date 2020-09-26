using System;
using MediaBrowser.Model.Dlna;

namespace Jellyfin.Plugin.DlnaPlayTo.Model
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
        public PlaylistItem(StreamInfo? streamInfo, DeviceProfile profile)
        {
            if (streamInfo == null)
            {
                throw new ArgumentNullException(nameof(streamInfo));
            }

            StreamInfo = streamInfo;
            Profile = profile;
            Didl = string.Empty;
        }

        /// <summary>
        /// Gets or sets the stream's Url.
        /// </summary>
        public Uri? StreamUrl { get; set; }

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
    }
}
