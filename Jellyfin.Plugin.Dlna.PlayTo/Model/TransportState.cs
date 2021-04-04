namespace Jellyfin.Plugin.Dlna.PlayTo.Model
{
    /// <summary>
    /// Lists the transport states of the dlna player as defined by the standard.
    ///
    /// DO NOT CHANGE THESE VALUES OR REMOVE THE UNDERSCORES AS NAMES MUST MATCH THE STANDARD.
    /// </summary>
    public enum TransportState
    {
        /// <summary>
        /// Transport state is stopped.
        /// </summary>
        Stopped,

        /// <summary>
        /// Transport state is playing.
        /// </summary>
        Playing,

        /// <summary>
        /// Transport state is transitioning.
        /// </summary>
        Transitioning,

        /// <summary>
        /// Transport state is paused playback.
        /// </summary>
        Paused_Playback,

        /// <summary>
        /// Transport state is paused recording - Not used.
        /// </summary>
        Paused_Recording,

        /// <summary>
        /// Transport state is recoding -  Not used.
        /// </summary>
        Recording,

        /// <summary>
        /// Transport state is none. No media present.
        /// </summary>
        No_Media_Present,

        /// <summary>
        /// Transport state is stopped.
        /// </summary>
        Paused,

        /// <summary>
        /// Transport state is error.
        /// </summary>
        Error
    }
}
