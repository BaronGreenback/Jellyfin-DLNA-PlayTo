namespace Jellyfin.Plugin.Dlna.PlayTo.Model
{
    /// <summary>
    /// Defines the <see cref="UBaseObject" />.
    /// </summary>
    public class UBaseObject
    {
        /// <summary>
        /// Gets or sets the Id.
        /// </summary>
        public string? Id { get; set; }

        /// <summary>
        /// Gets or sets the Url.
        /// </summary>
        public string Url { get; set; } = string.Empty;
    }
}
