using Jellyfin.Plugin.Dlna.PlayTo.Model;

namespace Jellyfin.Plugin.Dlna.PlayTo.EventArgs
{
    /// <summary>
    /// Defines the <see cref="PlaybackEventArgs" />.
    /// </summary>
    public class PlaybackEventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PlaybackEventArgs"/> class.
        /// </summary>
        /// <param name="mediaInfo">The mediaInfo<see cref="UBaseObject"/>.</param>
        public PlaybackEventArgs(UBaseObject mediaInfo) => MediaInfo = mediaInfo;

        /// <summary>
        /// Gets the MediaInfo.
        /// </summary>
        public UBaseObject MediaInfo { get; }
    }
}
