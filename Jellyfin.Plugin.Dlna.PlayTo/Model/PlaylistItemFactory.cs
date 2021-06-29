using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.Dlna.PlayTo.Model
{
    /// <summary>
    /// Defines the <see cref="PlaylistItemFactory" />.
    /// </summary>
    public static class PlaylistItemFactory
    {
        /// <summary>
        /// The Create.
        /// </summary>
        /// <param name="item">The item<see cref="Photo"/>.</param>
        /// <param name="profile">The profile<see cref="DeviceProfile"/>.</param>
        /// <returns>The <see cref="PlaylistItem"/>.</returns>
        public static PlaylistItem Create(Photo item, DeviceProfile profile)
        {
            if (item == null)
            {
                throw new System.ArgumentNullException(nameof(item));
            }

            if (profile == null)
            {
                throw new System.ArgumentNullException(nameof(profile));
            }

            var playlistItem = new PlaylistItem(
                new StreamInfo(item.Id, DlnaProfileType.Photo, profile),
                profile,
                DlnaProfileType.Photo);

            var directPlay = profile.DirectPlayProfiles?
                .FirstOrDefault(i => i.Type == DlnaProfileType.Photo && IsSupported(i, item));

            if (directPlay != null)
            {
                playlistItem.StreamInfo.PlayMethod = PlayMethod.DirectStream;
                playlistItem.StreamInfo.Container = Path.GetExtension(item.Path);

                return playlistItem;
            }

            var transcodingProfile = profile.TranscodingProfiles?
                .FirstOrDefault(i => i.Type == DlnaProfileType.Photo);

            if (transcodingProfile == null)
            {
                return playlistItem;
            }

            playlistItem.StreamInfo.PlayMethod = PlayMethod.Transcode;
            playlistItem.StreamInfo.Container = "." + transcodingProfile.Container.TrimStart('.');

            return playlistItem;
        }

        private static bool IsSupported(DirectPlayProfile profile, BaseItem item)
        {
            var mediaPath = item.Path;

            if (profile.Container.Length <= 0)
            {
                return true;
            }

            // Check container type
            var mediaContainer = Path.GetExtension(mediaPath).TrimStart('.');

            return profile.SupportsContainer(mediaContainer);
        }
    }
}
