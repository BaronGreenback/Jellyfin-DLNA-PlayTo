using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Entities;
using Jellyfin.Plugin.Dlna.Culture;
using Jellyfin.Plugin.Dlna.Didl;
using Jellyfin.Plugin.Dlna.Model;
using Jellyfin.Plugin.Dlna.PlayTo.EventArgs;
using Jellyfin.Plugin.Dlna.PlayTo.Model;
using Jellyfin.Plugin.Dlna.Ssdp;
using MediaBrowser.Common.Profiles;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Photo = MediaBrowser.Controller.Entities.Photo;

namespace Jellyfin.Plugin.Dlna.PlayTo.Main
{
    /// <summary>
    /// Defines the <see cref="PlayToController" />.
    /// </summary>
    internal class PlayToController : ISessionController, IDisposable
    {
        private readonly SessionInfo _session;
        private readonly ISessionManager _sessionManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly IUserManager _userManager;
        private readonly IImageProcessor _imageProcessor;
        private readonly IUserDataManager _userDataManager;
        private readonly ILocalizationManager _localization;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IServerConfigurationManager _config;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly SsdpLocator _deviceDiscovery;
        private readonly string _serverAddress;
        private readonly string? _accessToken;
        private readonly List<PlaylistItem> _playlist = new();
        private readonly IProfileManager _profileManager;
        private readonly Timer _photoTransitionTimer;
        private int _currentPlaylistIndex = -1;
        private PlaystateCommand? _photoSlideshow;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="PlayToController"/> class.
        /// </summary>
        /// <param name="session">The <see cref="SessionInfo"/>.</param>
        /// <param name="sessionManager">The <see cref="ISessionManager"/>.</param>
        /// <param name="libraryManager">The <see cref="ILibraryManager"/>.</param>
        /// <param name="logger">The <see cref="ILogger"/>.</param>
        /// <param name="userManager">The <see cref="IUserManager"/>.</param>
        /// <param name="imageProcessor">The <see cref="IImageProcessor"/>.</param>
        /// <param name="serverAddress">The server address.</param>
        /// <param name="accessToken">The access token.</param>
        /// <param name="deviceDiscovery">The <see cref="SsdpLocator"/>.</param>
        /// <param name="userDataManager">The <see cref="IUserDataManager"/>.</param>
        /// <param name="localization">The <see cref="ILocalizationManager"/>.</param>
        /// <param name="mediaSourceManager">The <see cref="IMediaSourceManager"/>.</param>
        /// <param name="config">The <see cref="IServerConfigurationManager"/>.</param>
        /// <param name="mediaEncoder">The mediaEncoder<see cref="IMediaEncoder"/>.</param>
        /// <param name="device">The <see cref="PlayToDevice"/>.</param>
        /// <param name="profileManager">The <see cref="IProfileManager"/>.</param>
        public PlayToController(
            SessionInfo session,
            ISessionManager sessionManager,
            ILibraryManager libraryManager,
            ILogger logger,
            IUserManager userManager,
            IImageProcessor imageProcessor,
            string serverAddress,
            string? accessToken,
            SsdpLocator deviceDiscovery,
            IUserDataManager userDataManager,
            ILocalizationManager localization,
            IMediaSourceManager mediaSourceManager,
            IServerConfigurationManager config,
            IMediaEncoder mediaEncoder,
            PlayToDevice device,
            IProfileManager profileManager)
        {
            _session = session;
            _sessionManager = sessionManager;
            _libraryManager = libraryManager;
            _logger = logger;
            _userManager = userManager;
            _imageProcessor = imageProcessor;
            _serverAddress = serverAddress;
            _accessToken = accessToken;
            _deviceDiscovery = deviceDiscovery;
            _userDataManager = userDataManager;
            _localization = localization;
            _mediaSourceManager = mediaSourceManager;
            _config = config;
            _mediaEncoder = mediaEncoder;
            _photoTransitionTimer = new Timer(
                (object? state) =>
                {
                    _logger.LogDebug("Transitioning image...");
                    SetPlaylistIndex(_currentPlaylistIndex + 1);
                },
                null,
                Timeout.Infinite,
                Timeout.Infinite);

            Device = device;
            _profileManager = profileManager;

            Device.OnDeviceUnavailable = OnDeviceUnavailable;
            Device.PlaybackStart += OnDevicePlaybackStart;
            Device.PlaybackProgress += OnDevicePlaybackProgress;
            Device.PlaybackStopped += OnDevicePlaybackStopped;
            Device.MediaChanged += OnDeviceMediaChanged;
            _deviceDiscovery.DeviceLeft += OnDeviceDiscoveryDeviceLeft;
        }

        /// <summary>
        /// Gets a value indicating whether the session is active.
        /// </summary>
        public bool IsSessionActive => !_disposed;

        /// <summary>
        /// Gets a value indicating the playTo device of this controller.
        /// </summary>
        public PlayToDevice Device { get; }

        /// <summary>
        /// Gets a value indicating whether the session supports media control.
        /// </summary>
        public bool SupportsMediaControl => !_disposed;

        /// <inheritdoc />
        public Task SendMessage<T>(SessionMessageType name, Guid messageId, T data, CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            // Ensure the device is initialised.
            Device.DeviceInitialise().ConfigureAwait(false);

            if (_disposed)
            {
                return Task.CompletedTask;
            }

            switch (name)
            {
                case SessionMessageType.Play:
                    SendPlayCommand(data as PlayRequest);
                    return Task.CompletedTask;

                case SessionMessageType.Playstate:
                    SendPlaystateCommand(data as PlaystateRequest);
                    return Task.CompletedTask;

                case SessionMessageType.GeneralCommand:
                    SendGeneralCommand(data as GeneralCommand);
                    return Task.CompletedTask;
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private static int? GetIntValue(IReadOnlyDictionary<string, string> values, string name)
        {
            var value = values.GetValueOrDefault(name);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
        }

        private static long GetLongValue(IReadOnlyDictionary<string, string> values, string name)
        {
            var value = values.GetValueOrDefault(name);
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
        }

        private static string GetDlnaHeaders(PlaylistItem item)
        {
            var profile = item.Profile;
            var streamInfo = item.StreamInfo;

            if (string.IsNullOrEmpty(streamInfo.Container))
            {
                return string.Empty;
            }

            switch (streamInfo.MediaType)
            {
                case DlnaProfileType.Audio:
                    return ContentFeatureBuilder.BuildAudioHeader(
                        profile,
                        streamInfo.Container,
                        streamInfo.TargetAudioCodec.FirstOrDefault(),
                        streamInfo.TargetAudioBitrate,
                        streamInfo.TargetAudioSampleRate,
                        streamInfo.TargetAudioChannels,
                        streamInfo.TargetAudioBitDepth,
                        streamInfo.IsDirectStream,
                        streamInfo.RunTimeTicks ?? 0,
                        streamInfo.TranscodeSeekInfo);

                case DlnaProfileType.Video:
                    {
                        var list = ContentFeatureBuilder.BuildVideoHeader(
                            profile,
                            streamInfo.Container,
                            streamInfo.TargetVideoCodec.FirstOrDefault(),
                            streamInfo.TargetAudioCodec.FirstOrDefault(),
                            streamInfo.TargetWidth,
                            streamInfo.TargetHeight,
                            streamInfo.TargetVideoBitDepth,
                            streamInfo.TargetVideoBitrate,
                            streamInfo.TargetTimestamp,
                            streamInfo.IsDirectStream,
                            streamInfo.RunTimeTicks ?? 0,
                            streamInfo.TargetVideoProfile,
                            streamInfo.TargetVideoLevel,
                            streamInfo.TargetFramerate ?? 0,
                            streamInfo.TargetPacketLength,
                            streamInfo.TranscodeSeekInfo,
                            streamInfo.IsTargetAnamorphic,
                            streamInfo.IsTargetInterlaced,
                            streamInfo.TargetRefFrames,
                            streamInfo.TargetVideoStreamCount,
                            streamInfo.TargetAudioStreamCount,
                            streamInfo.TargetVideoCodecTag,
                            streamInfo.IsTargetAVC);

                        return list.Count == 0 ? string.Empty : list[0];
                    }

                default:
                    return string.Empty;
            }
        }

        private void OnDeviceUnavailable()
        {
            try
            {
                _sessionManager.ReportSessionEnded(_session.Id);
                if (!string.IsNullOrEmpty(Device.Profile.Id))
                {
                    _profileManager.DeleteProfile(Device.Profile.Id);
                }

                _ = Device.DeviceUnavailable();
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                // Could throw if the session is already gone
                _logger.LogError(ex, "Error reporting the end of session {Id}", _session.Id);
            }
        }

        private void OnDeviceDiscoveryDeviceLeft(object? sender, DiscoveredSsdpDevice e)
        {
            if (_disposed)
            {
                return;
            }

            e.Headers.TryGetValue("USN", out string? usn);
            e.Headers.TryGetValue("NT", out string? nt);

            usn ??= string.Empty;
            nt ??= string.Empty;

            if (usn.Contains(Device.Uuid, StringComparison.OrdinalIgnoreCase)
                && (usn.Contains("MediaRenderer:", StringComparison.OrdinalIgnoreCase) || nt.Contains("MediaRenderer:", StringComparison.OrdinalIgnoreCase)))
            {
                OnDeviceUnavailable();
            }
        }

        private async void OnDeviceMediaChanged(object? sender, MediaChangedEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var streamInfo = StreamParams.ParseFromUrl(e.OldMediaInfo?.Url, _libraryManager, _mediaSourceManager);
                if (streamInfo.Item != null)
                {
                    var positionTicks = GetProgressPositionTicks(streamInfo);
                    await ReportPlaybackStopped(streamInfo, positionTicks).ConfigureAwait(false);
                }

                streamInfo = StreamParams.ParseFromUrl(e.NewMediaInfo.Url, _libraryManager, _mediaSourceManager);
                if (streamInfo.Item == null)
                {
                    return;
                }

                var newItemProgress = GetProgressInfo(streamInfo);

                await _sessionManager.OnPlaybackStart(newItemProgress).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                _logger.LogError(ex, "Error reporting progress");
            }
        }

        private async void OnDevicePlaybackStopped(object? sender, PlaybackEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var streamInfo = StreamParams.ParseFromUrl(e.MediaInfo.Url, _libraryManager, _mediaSourceManager);
                if (streamInfo.Item == null)
                {
                    return;
                }

                if (streamInfo.Item is Photo)
                {
                    await ReportPlaybackStopped(streamInfo, 1).ConfigureAwait(false);
                    return;
                }

                var positionTicks = GetProgressPositionTicks(streamInfo);
                await ReportPlaybackStopped(streamInfo, positionTicks).ConfigureAwait(false);
                var mediaSource = await streamInfo.GetMediaSource(CancellationToken.None).ConfigureAwait(false);

                var duration = mediaSource == null ?
                    Device.Duration?.Ticks :
                    mediaSource.RunTimeTicks;

                var playedToCompletion = positionTicks == 0;
                if (!playedToCompletion && duration.HasValue && positionTicks.HasValue)
                {
                    double percent = positionTicks.Value;
                    percent /= duration.Value;

                    playedToCompletion = Math.Abs(1 - percent) * 100 <= _config.Configuration.MaxResumePct;
                }

                if (playedToCompletion)
                {
                    SetPlaylistIndex(_currentPlaylistIndex + 1);
                }
                else
                {
                    _playlist.Clear();
                    _currentPlaylistIndex = -1;
                }
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                _logger.LogError(ex, "Error reporting playback stopped");
            }
        }

        private async Task ReportPlaybackStopped(StreamParams streamInfo, long? positionTicks)
        {
            try
            {
                await _sessionManager.OnPlaybackStopped(new PlaybackStopInfo
                {
                    ItemId = streamInfo.ItemId,
                    SessionId = _session.Id,
                    PositionTicks = positionTicks,
                    MediaSourceId = streamInfo.MediaSourceId
                }).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                _logger.LogError(ex, "Error reporting progress");
            }
        }

        private async void OnDevicePlaybackStart(object? sender, PlaybackEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var info = StreamParams.ParseFromUrl(e.MediaInfo.Url, _libraryManager, _mediaSourceManager);

                if (info.Item == null)
                {
                    return;
                }

                var progress = GetProgressInfo(info);

                await _sessionManager.OnPlaybackStart(progress).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                _logger.LogError(ex, "Error reporting progress");
            }
        }

        private async void OnDevicePlaybackProgress(object? sender, PlaybackEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var mediaUrl = e.MediaInfo.Url;

                if (string.IsNullOrWhiteSpace(mediaUrl))
                {
                    return;
                }

                _logger.LogDebug("Reporting playback progress..");

                var info = StreamParams.ParseFromUrl(mediaUrl, _libraryManager, _mediaSourceManager);

                if (info.Item == null)
                {
                    return;
                }

                var progress = GetProgressInfo(info);

                await _sessionManager.OnPlaybackProgress(progress).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                _logger.LogError(ex, "Error reporting progress");
            }
        }

        private long? GetProgressPositionTicks(StreamParams info)
        {
            var ticks = Device.Position.Ticks;

            if (!info.IsDirectStream)
            {
                ticks += info.StartPositionTicks;
            }

            return ticks;
        }

        private PlaybackStartInfo GetProgressInfo(StreamParams info)
        {
            return new()
            {
                ItemId = info.ItemId,
                SessionId = _session.Id,
                PositionTicks = GetProgressPositionTicks(info),
                IsMuted = Device.IsMuted,
                IsPaused = Device.IsPaused,
                MediaSourceId = info.MediaSourceId,
                AudioStreamIndex = info.AudioStreamIndex,
                SubtitleStreamIndex = info.SubtitleStreamIndex,
                VolumeLevel = Device.Volume,
                CanSeek = true, // TODO
                PlayMethod = info.IsDirectStream ? PlayMethod.DirectStream : PlayMethod.Transcode
            };
        }

        /// <summary>
        /// Shuffles a list.
        /// https://stackoverflow.com/questions/273313/randomize-a-listt.
        /// </summary>
        private void ShufflePlaylist()
        {
            using var provider = new RNGCryptoServiceProvider();
            int n = _playlist.Count;
            while (n > 1)
            {
                byte[] box = new byte[1];
                do
                {
                    provider.GetBytes(box);
                }
                while (!(box[0] < n * (byte.MaxValue / n)));

                int k = box[0] % n;
                n--;
                PlaylistItem value = _playlist[k];
                _playlist[k] = _playlist[n];
                _playlist[n] = value;
            }
        }

        private void SendPlayCommand(PlayRequest? command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            _logger.LogDebug("{Name} - Received PlayRequest: {Command}", _session.DeviceName, command.PlayCommand);

            var user = command.ControllingUserId.Equals(Guid.Empty) ? null : _userManager.GetUserById(command.ControllingUserId);

            var items = new List<BaseItem>();
            foreach (var id in command.ItemIds)
            {
                AddItemFromId(id, items);
            }

            var startIndex = command.StartIndex ?? 0;
            int len = items.Count - startIndex;
            if (startIndex > 0)
            {
                items = items.GetRange(startIndex, len);
            }

            var playlist = new List<PlaylistItem>(len);
            var didl = new DidlBuilder(
                Device.Profile,
                user,
                _imageProcessor,
                _serverAddress,
                _accessToken,
                _userDataManager,
                _localization,
                _mediaSourceManager,
                _logger,
                _mediaEncoder,
                _libraryManager);

            for (int i = 0; i < len; i++)
            {
                PlaylistItem? item;
                if (playlist.Count == 0)
                {
                    item = CreatePlaylistItem(items[0], user, command.StartPositionTicks ?? 0, command.MediaSourceId, command.AudioStreamIndex, command.SubtitleStreamIndex, didl);
                    if (item == null)
                    {
                        // skip through any without stream urls.
                        continue;
                    }
                }
                else
                {
                    item = CreatePlaylistItem(items[i], user, 0, null, null, null, didl);
                    if (item == null)
                    {
                        continue;
                    }
                }

                playlist.Add(item);
            }

            _logger.LogDebug("{Name} - Playlist created", _session.DeviceName);

            switch (command.PlayCommand)
            {
                // track offset./
                case PlayCommand.PlayNow:
                    // Reset playlist and re-add tracks.
                    _playlist.Clear();
                    _playlist.AddRange(playlist);
                    _currentPlaylistIndex = playlist.Count > 0 ? 0 : -1;
                    break;

                case PlayCommand.PlayInstantMix:
                case PlayCommand.PlayShuffle:
                    _logger.LogDebug("{Name} - Shuffling playlist.", _session.DeviceName);

                    // Will restart playback on a random item.
                    ShufflePlaylist();
                    break;

                case PlayCommand.PlayLast:
                    {
                        // Add to the end of the list.
                        _playlist.AddRange(playlist);

                        _logger.LogDebug("{Name} - Adding {Count} items to the end of the playlist.", _session.DeviceName, _playlist.Count);
                        if (Device.IsPlaying)
                        {
                            return;
                        }

                        break;
                    }

                case PlayCommand.PlayNext:
                    {
                        // Insert into the next up.
                        _logger.LogDebug("{Name} - Inserting {Count} items next in the playlist.", _session.DeviceName, _playlist.Count);
                        if (_currentPlaylistIndex >= 0)
                        {
                            _playlist.InsertRange(_currentPlaylistIndex, playlist);
                        }
                        else
                        {
                            _playlist.AddRange(playlist);
                        }

                        if (Device.IsPlaying)
                        {
                            return;
                        }

                        break;
                    }
            }

            if (!command.ControllingUserId.Equals(Guid.Empty))
            {
                _sessionManager.LogSessionActivity(
                    _session.Client,
                    _session.ApplicationVersion,
                    _session.DeviceId,
                    _session.DeviceName,
                    _session.RemoteEndPoint,
                    user);
            }

            PlayItems();
        }

        private void SendPlaystateCommand(PlaystateRequest? command)
        {
            if (command == null)
            {
                return;
            }

            if (_photoSlideshow != null)
            {
                var timeout = DlnaPlayTo.GetInstance().Configuration.PhotoTransitionalTimeout * 1000;
                switch (command.Command)
                {
                    case PlaystateCommand.Stop:
                        _playlist.Clear();
                        _photoTransitionTimer.Change(Timeout.Infinite, Timeout.Infinite);
                        _photoSlideshow = null;
                        break;
                    case PlaystateCommand.Pause:
                        _photoTransitionTimer.Change(Timeout.Infinite, Timeout.Infinite);
                        _photoSlideshow = PlaystateCommand.Pause;
                        break;
                    case PlaystateCommand.Unpause:
                        _photoTransitionTimer.Change(timeout, Timeout.Infinite);
                        _photoSlideshow = PlaystateCommand.PlayPause;
                        break;
                    case PlaystateCommand.PlayPause:
                        _photoTransitionTimer.Change(timeout, Timeout.Infinite);
                        break;
                    case PlaystateCommand.NextTrack:
                        _photoTransitionTimer.Change(timeout, Timeout.Infinite);
                        SetPlaylistIndex(_currentPlaylistIndex + 1);
                        return;
                    case PlaystateCommand.PreviousTrack:
                        _photoTransitionTimer.Change(timeout, Timeout.Infinite);
                        SetPlaylistIndex(_currentPlaylistIndex - 1);
                        return;
                }

                return;
            }

            switch (command.Command)
            {
                case PlaystateCommand.Stop:
                    _playlist.Clear();
                    Device.Stop();
                    return;

                case PlaystateCommand.Pause:
                    Device.Pause();
                    return;

                case PlaystateCommand.Unpause:
                    Device.Play();
                    return;

                case PlaystateCommand.PlayPause:
                    if (Device.IsPaused)
                    {
                        Device.Play();
                        return;
                    }

                    Device.Pause();
                    return;

                case PlaystateCommand.Seek:
                    Seek(command.SeekPositionTicks ?? 0);
                    return;

                case PlaystateCommand.NextTrack:
                    SetPlaylistIndex(_currentPlaylistIndex + 1);
                    return;

                case PlaystateCommand.PreviousTrack:
                    SetPlaylistIndex(_currentPlaylistIndex - 1);
                    return;
            }
        }

        private void Seek(long newPosition)
        {
            var media = Device.CurrentMediaInfo;

            if (media != null)
            {
                var info = StreamParams.ParseFromUrl(media.Url, _libraryManager, _mediaSourceManager);

                if (info.Item != null && !info.IsDirectStream)
                {
                    var user = !_session.UserId.Equals(Guid.Empty) ? _userManager.GetUserById(_session.UserId) : null;
                    var newItem = CreatePlaylistItem(info.Item, user, newPosition, info.MediaSourceId, info.AudioStreamIndex, info.SubtitleStreamIndex);
                    if (newItem == null)
                    {
                        _logger.LogError("Unable to seek on null stream.");
                        return;
                    }

                    Device.SetAvTransport(newItem.StreamInfo.MediaType, true, newItem.StreamUrl!, GetDlnaHeaders(newItem), newItem.Didl, true, newPosition);
                    SendNextTrackMessage();
                }
            }
        }

        /// <summary>
        /// Send a message to the DLNA device to notify what is the next track in the playlist.
        /// </summary>
        private void SendNextTrackMessage()
        {
            if (_currentPlaylistIndex < 0 || _currentPlaylistIndex >= _playlist.Count - 1)
            {
                return;
            }

            // The current playing item is indeed in the play list and we are not yet at the end of the playlist.
            var nextItemIndex = _currentPlaylistIndex + 1;
            var nextItem = _playlist[nextItemIndex];
            // Send the SetNextAvTransport message.
            Device.SetNextAvTransport(nextItem.StreamUrl!, GetDlnaHeaders(nextItem), nextItem.Didl);
        }

        private void AddItemFromId(Guid id, ICollection<BaseItem> list)
        {
            var item = _libraryManager.GetItemById(id);

            // Ensure the device can be play the media.
            foreach (var type in Device.Profile.GetSupportedMediaTypes())
            {
                if (string.Equals(item.MediaType, type, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(item);
                    return;
                }
            }
        }

        private PlaylistItem? CreatePlaylistItem(
        BaseItem item,
        User? user,
        long startPostionTicks,
        string? mediaSourceId,
        int? audioStreamIndex,
        int? subtitleStreamIndex,
        DidlBuilder? didl = null)
        {
            didl ??= new DidlBuilder(
                Device.Profile,
                user,
                _imageProcessor,
                _serverAddress,
                _accessToken,
                _userDataManager,
                _localization,
                _mediaSourceManager,
                _logger,
                _mediaEncoder,
                _libraryManager);

            var mediaSources = item is IHasMediaSources
                ? _mediaSourceManager.GetStaticMediaSources(item, true, user).ToArray()
                : Array.Empty<MediaSourceInfo>();

            var playlistItem = GetPlaylistItem(item, mediaSources, Device.Profile, _session.DeviceId, mediaSourceId, audioStreamIndex, subtitleStreamIndex);
            string itemDidl;
            if (playlistItem.StreamInfo.MediaType != DlnaProfileType.Photo)
            {
                playlistItem.StreamUrl = playlistItem.StreamInfo.ToUrl(_serverAddress, _accessToken, "&dlna=true");
                itemDidl = didl.GetItemDidl(item, user, null, _session.DeviceId, FilterHelper.Filter(), playlistItem.StreamInfo);
            }
            else
            {
                playlistItem.StreamUrl = didl.GetImageUrl(item);
                itemDidl = didl.GetItemDidl(item, user, null, _session.DeviceId, FilterHelper.Filter(), playlistItem.StreamInfo);
            }

            if (playlistItem.StreamUrl == null)
            {
                return null;
            }

            playlistItem.StreamInfo.StartPositionTicks = startPostionTicks;
            playlistItem.Didl = itemDidl;

            return playlistItem;
        }

        private PlaylistItem GetPlaylistItem(BaseItem item, MediaSourceInfo[] mediaSources, DeviceProfile profile, string deviceId, string? mediaSourceId, int? audioStreamIndex, int? subtitleStreamIndex)
        {
            if (string.Equals(item.MediaType, MediaType.Video, StringComparison.OrdinalIgnoreCase))
            {
                return new PlaylistItem(
                    new StreamBuilder(_mediaEncoder, _logger).BuildVideoItem(new VideoOptions
                    {
                        ItemId = item.Id,
                        MediaSources = mediaSources,
                        Profile = profile,
                        DeviceId = deviceId,
                        MaxBitrate = profile.MaxStreamingBitrate,
                        MediaSourceId = mediaSourceId,
                        AudioStreamIndex = audioStreamIndex,
                        SubtitleStreamIndex = subtitleStreamIndex
                    }),
                    profile);
            }

            if (string.Equals(item.MediaType, MediaType.Audio, StringComparison.OrdinalIgnoreCase))
            {
                return new PlaylistItem(
                    new StreamBuilder(_mediaEncoder, _logger).BuildAudioItem(
                        new AudioOptions
                        {
                            ItemId = item.Id,
                            MediaSources = mediaSources,
                            Profile = profile,
                            DeviceId = deviceId,
                            MaxBitrate = profile.MaxStreamingBitrate,
                            MediaSourceId = mediaSourceId
                        }),
                    profile);
            }

            if (string.Equals(item.MediaType, MediaType.Photo, StringComparison.OrdinalIgnoreCase))
            {
                return PlaylistItemFactory.Create((Photo)item, profile);
            }

            throw new ArgumentException("Unrecognized item type.");
        }

        private void PlayItems()
        {
            _logger.LogDebug("{Name} - Playing {Count} items", _session.DeviceName, _playlist.Count);

            SetPlaylistIndex(0);
        }

        private void SetPlaylistIndex(int index)
        {
            if (index < 0 || index >= _playlist.Count)
            {
                _playlist.Clear();
                _photoSlideshow = null;
                _currentPlaylistIndex = -1;
                Device.Stop();
                return;
            }

            _currentPlaylistIndex = index;
            var currentItem = _playlist[index];

            if (currentItem.StreamUrl != null)
            {
                var streamInfo = currentItem.StreamInfo;
                Device.SetAvTransport(
                    currentItem.StreamInfo.MediaType,
                    true,
                    currentItem.StreamUrl,
                    GetDlnaHeaders(currentItem),
                    currentItem.Didl,
                    index > 0,
                    streamInfo.StartPositionTicks > 0 && streamInfo.IsDirectStream ? streamInfo.StartPositionTicks : 0);

                if (currentItem.StreamInfo.MediaType == DlnaProfileType.Photo)
                {
                    _photoTransitionTimer.Change(DlnaPlayTo.GetInstance().Configuration.PhotoTransitionalTimeout * 1000, Timeout.Infinite);
                    _photoSlideshow = PlaystateCommand.PlayPause;
                }

                SendNextTrackMessage();
            }
        }

        /// <summary>
        /// Override this method and dispose any objects you own the lifetime of if disposing is true.
        /// </summary>
        /// <param name="disposing">True if managed objects should be disposed, if false, only unmanaged resources should be released.</param>
        private void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (!disposing)
            {
                return;
            }

            _deviceDiscovery.DeviceLeft -= OnDeviceDiscoveryDeviceLeft;
            Device.PlaybackStart -= OnDevicePlaybackStart;
            Device.PlaybackProgress -= OnDevicePlaybackProgress;
            Device.PlaybackStopped -= OnDevicePlaybackStopped;
            Device.MediaChanged -= OnDeviceMediaChanged;
            Device.OnDeviceUnavailable = null;
            Device.Dispose();

            _disposed = true;
        }

        private void SendGeneralCommand(GeneralCommand? command)
        {
            if (command == null)
            {
                return;
            }

            switch (command.Name)
            {
                case GeneralCommandType.VolumeDown:
                    Device.VolumeDown();
                    return;

                case GeneralCommandType.VolumeUp:
                    Device.VolumeUp();
                    return;

                case GeneralCommandType.Mute:
                    Device.Mute();
                    return;

                case GeneralCommandType.Unmute:
                    Device.Unmute();
                    return;

                case GeneralCommandType.ToggleMute:
                    Device.ToggleMute();
                    return;

                case GeneralCommandType.SetAudioStreamIndex:
                    if (!command.Arguments.TryGetValue("Index", out string? index))
                    {
                        throw new ArgumentException("SetAudioStreamIndex argument cannot be null");
                    }

                    if (int.TryParse(index, NumberStyles.Integer, CultureDefault.UsCulture, out var val))
                    {
                        SetAudioStreamIndex(val);
                        return;
                    }

                    throw new ArgumentException("Unsupported SetAudioStreamIndex value supplied.");

                case GeneralCommandType.SetSubtitleStreamIndex:
                    if (!command.Arguments.TryGetValue("Index", out index))
                    {
                        throw new ArgumentException("SetSubtitleStreamIndex argument cannot be null");
                    }

                    if (int.TryParse(index, NumberStyles.Integer, CultureDefault.UsCulture, out val))
                    {
                        SetSubtitleStreamIndex(val);
                        return;
                    }

                    throw new ArgumentException("Unsupported SetSubtitleStreamIndex value supplied.");

                case GeneralCommandType.SetVolume:
                    if (!command.Arguments.TryGetValue("Volume", out string? vol))
                    {
                        throw new ArgumentException("Volume argument cannot be null");
                    }

                    if (!int.TryParse(vol, NumberStyles.Integer, CultureDefault.UsCulture, out var volume))
                    {
                        throw new ArgumentException("Unsupported volume value supplied.");
                    }

                    Device.Volume = volume;
                    return;
            }
        }

        private void SetAudioStreamIndex(int? newIndex)
        {
            var media = Device.CurrentMediaInfo;

            if (media != null)
            {
                var info = StreamParams.ParseFromUrl(media.Url, _libraryManager, _mediaSourceManager);

                if (info.Item != null)
                {
                    var newPosition = GetProgressPositionTicks(info) ?? 0;

                    var user = !_session.UserId.Equals(Guid.Empty) ? _userManager.GetUserById(_session.UserId) : null;
                    var newItem = CreatePlaylistItem(info.Item, user, newPosition, info.MediaSourceId, newIndex, info.SubtitleStreamIndex);
                    if (newItem == null)
                    {
                        _logger.LogError("Unable to seek on null stream.");
                        return;
                    }

                    bool seekAfter = newItem.StreamInfo.IsDirectStream;

                    // Pass our intentions to the device, so that it doesn't restart at the beginning, only to then seek.
                    if (newItem.StreamUrl == null)
                    {
                        _logger.LogError("Unable set audio on null stream.");
                        return;
                    }

                    Device.SetAvTransport(
                        newItem.StreamInfo.MediaType,
                        !seekAfter,
                        newItem.StreamUrl,
                        GetDlnaHeaders(newItem),
                        newItem.Didl,
                        true,
                        seekAfter ? newPosition : 0);
                    SendNextTrackMessage();
                }
            }
        }

        private void SetSubtitleStreamIndex(int? newIndex)
        {
            var media = Device.CurrentMediaInfo;

            if (media?.Url != null)
            {
                var info = StreamParams.ParseFromUrl(media.Url, _libraryManager, _mediaSourceManager);

                if (info.Item != null)
                {
                    var newPosition = GetProgressPositionTicks(info) ?? 0;

                    var user = !_session.UserId.Equals(Guid.Empty) ? _userManager.GetUserById(_session.UserId) : null;
                    var newItem = CreatePlaylistItem(info.Item, user, newPosition, info.MediaSourceId, info.AudioStreamIndex, newIndex);
                    if (newItem == null)
                    {
                        _logger.LogError("Unable to set subtitle index on null stream.");
                        return;
                    }

                    bool seekAfter = newItem.StreamInfo.IsDirectStream && newPosition > 0;

                    // Pass our intentions to the device, so that it doesn't restart at the beginning, only to then seek.
                    Device.SetAvTransport(
                        newItem.StreamInfo.MediaType,
                        !seekAfter,
                        newItem.StreamUrl!,
                        GetDlnaHeaders(newItem),
                        newItem.Didl,
                        true,
                        seekAfter ? newPosition : 0);
                    SendNextTrackMessage();
                }
            }
        }

        /// <summary>
        /// Defines the <see cref="StreamParams"/> class.
        /// </summary>
        private class StreamParams
        {
            private MediaSourceInfo? _mediaSource;

            /// <summary>
            /// Initializes a new instance of the <see cref="StreamParams"/> class.
            /// </summary>
            /// <param name="itemId">The <see cref="Guid"/>.</param>
            private StreamParams(Guid itemId) => ItemId = itemId;

            /// <summary>
            /// Gets the ItemId.
            /// </summary>
            public Guid ItemId { get; }

            /// <summary>
            /// Gets a value indicating whether IsDirectStream.
            /// </summary>
            public bool IsDirectStream { get; private set; }

            /// <summary>
            /// Gets the StartPositionTicks.
            /// </summary>
            public long StartPositionTicks { get; private set; }

            /// <summary>
            /// Gets the AudioStreamIndex.
            /// </summary>
            public int? AudioStreamIndex { get; private set; }

            /// <summary>
            /// Gets the SubtitleStreamIndex.
            /// </summary>
            public int? SubtitleStreamIndex { get; private set; }

            /// <summary>
            /// Gets the MediaSourceId.
            /// </summary>
            public string? MediaSourceId { get; private set; }

            /// <summary>
            /// Gets the Item.
            /// </summary>
            public BaseItem? Item { get; private set; }

            /// <summary>
            /// Gets or sets the LiveStreamId.
            /// </summary>
            private string? LiveStreamId { get; set; }

            /// <summary>
            /// Gets or sets the media source manager.
            /// </summary>
            private IMediaSourceManager? MediaSourceManager { get; set; }

            /// <summary>
            /// Parses a stream from a url.
            /// </summary>
            /// <param name="url">The url.</param>
            /// <param name="libraryManager">A <see cref="ILibraryManager"/> instance.</param>
            /// <param name="mediaSourceManager">A <see cref="IMediaSourceManager"/> instance.</param>
            /// <returns>A <see cref="StreamParams"/> instance.</returns>
            public static StreamParams ParseFromUrl(string? url, ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager)
            {
                if (string.IsNullOrEmpty(url))
                {
                    // This condition is met on the initial loading of media, when the last media url is null.
                    return new StreamParams(Guid.Empty);
                }

                var request = new StreamParams(GetItemId(url));

                if (request.ItemId.Equals(Guid.Empty))
                {
                    return request;
                }

                request.Item = libraryManager.GetItemById(request.ItemId);
                request.MediaSourceManager = mediaSourceManager;

                var index = url.IndexOf('?', StringComparison.Ordinal);
                if (index == -1)
                {
                    return request;
                }

                var query = url[(index + 1)..];
                Dictionary<string, string> values = QueryHelpers.ParseQuery(query).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
                request.MediaSourceId = values.GetValueOrDefault("MediaSourceId");
                request.LiveStreamId = values.GetValueOrDefault("LiveStreamId");
                request.IsDirectStream = string.Equals("true", values.GetValueOrDefault("Static"), StringComparison.OrdinalIgnoreCase);
                request.AudioStreamIndex = GetIntValue(values, "AudioStreamIndex");
                request.SubtitleStreamIndex = GetIntValue(values, "SubtitleStreamIndex");
                request.StartPositionTicks = GetLongValue(values, "StartPositionTicks");
                return request;
            }

            /// <summary>
            /// Gets the media source.
            /// </summary>
            /// <param name="cancellationToken">A <see cref="CancellationToken"/>.</param>
            /// <returns>A <see cref="Task{MediaSourceInfo}"/> or null.</returns>
            public async Task<MediaSourceInfo?> GetMediaSource(CancellationToken cancellationToken)
            {
                if (_mediaSource != null)
                {
                    return _mediaSource;
                }

                if (Item is not IHasMediaSources)
                {
                    return null;
                }

                if (MediaSourceManager != null)
                {
                    _mediaSource = await MediaSourceManager.GetMediaSource(Item, MediaSourceId, LiveStreamId, false, cancellationToken).ConfigureAwait(false);
                }

                return _mediaSource;
            }

            /// <summary>
            /// Extracts the guid from the url.
            /// </summary>
            /// <param name="url">Url to parse.</param>
            /// <returns>A <see cref="Guid"/>, or Guid.Empty if not found.</returns>
            private static Guid GetItemId(string url)
            {
                var segments = new string[] { "/video/", "/audio/", "/Items/" };

                foreach (var segment in segments)
                {
                    int i = url.IndexOf(segment, StringComparison.OrdinalIgnoreCase);
                    if (i == -1)
                    {
                        continue;
                    }

                    i += segment.Length;
                    int j = url.IndexOf('/', i + 1);
                    if (Guid.TryParse(url[i..j], out var result))
                    {
                        return result;
                    }
                }

                return Guid.Empty;
            }
        }
    }
}
