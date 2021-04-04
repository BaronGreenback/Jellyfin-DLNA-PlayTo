using Jellyfin.Plugin.Dlna.PlayTo.Model;

namespace Jellyfin.Plugin.Dlna.PlayTo.EventArgs
{
    /// <summary>
    /// Defines the <see cref="MediaChangedEventArgs" />.
    /// </summary>
    public class MediaChangedEventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MediaChangedEventArgs"/> class.
        /// </summary>
        /// <param name="previousMediaInfo">The <see cref="UBaseObject"/> containing the previous media.</param>
        /// <param name="mediaInfo">The <see cref="UBaseObject"/> containing the current media.</param>
        public MediaChangedEventArgs(UBaseObject previousMediaInfo, UBaseObject mediaInfo)
        {
            OldMediaInfo = previousMediaInfo;
            NewMediaInfo = mediaInfo;
        }

        /// <summary>
        /// Gets the previous media.
        /// </summary>
        public UBaseObject? OldMediaInfo { get; }

        /// <summary>
        /// Gets the current media.
        /// </summary>
        public UBaseObject NewMediaInfo { get; }
    }
}
