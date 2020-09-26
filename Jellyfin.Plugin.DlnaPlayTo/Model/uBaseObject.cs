using System;

namespace Jellyfin.Plugin.DlnaPlayTo.Model
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

        /// <summary>
        /// The Equals Method.
        /// </summary>
        /// <param name="other">The <see cref="UBaseObject"/>.</param>
        /// <returns>True if this object matches <paramref name="other"/>.</returns>
        public bool Equals(UBaseObject other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            return string.Equals(Id, other.Id, StringComparison.Ordinal);
        }
    }
}
