using System.Linq;
using System.Xml.Linq;

namespace Jellyfin.Plugin.Dlna.PlayTo.Model
{
    /// <summary>
    /// Defines the <see cref="XElementExtensions" />.
    /// </summary>
    public static class XElementExtensions
    {
        /// <summary>
        /// The GetValue.
        /// </summary>
        /// <param name="container">The <see cref="XElement"/>.</param>
        /// <param name="name">The <see cref="XName"/>.</param>
        /// <returns>The <see cref="string"/>.</returns>
        public static string GetValue(this XElement container, XName name)
        {
            var node = container?.Element(name);

            return node?.Value ?? string.Empty;
        }

        /// <summary>
        /// Retrieves the descendants value.
        /// </summary>
        /// <param name="container">The <see cref="XElement"/>.</param>
        /// <param name="name">The <see cref="XName"/>.</param>
        /// <returns>The <see cref="string"/>.</returns>
        public static string GetDescendantValue(this XElement container, XName name)
            => container?.Descendants(name).FirstOrDefault()?.Value ?? string.Empty;
    }
}
