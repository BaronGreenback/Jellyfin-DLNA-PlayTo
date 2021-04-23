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
using Jellyfin.Plugin.Dlna.Profiles;
using Jellyfin.Plugin.Dlna.Ssdp;
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
        private readonly PlayToDevice _device;
        private readonly IDlnaProfileManager _profileManager;
        private int _currentPlaylistIndex = -1;
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
        /// <param name="dlnaProfileManager">The <see cref="IDlnaProfileManager"/>.</param>
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
            IDlnaProfileManager dlnaProfileManager)
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
            _device = device;
            _profileManager = dlnaProfileManager;

            _device.OnDeviceUnavailable = OnDeviceUnavailable;
            _device.PlaybackStart += OnDevicePlaybackStart;
            _device.PlaybackProgress += OnDevicePlaybackProgress;
            _device.PlaybackStopped += OnDevicePlaybackStopped;
            _device.MediaChanged += OnDeviceMediaChanged;
            _deviceDiscovery.DeviceLeft += OnDeviceDiscoveryDeviceLeft;
        }

        /// <summary>
        /// Gets a value indicating whether the session is active.
        /// </summary>
        public bool IsSessionActive => !_disposed;

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
            _device.DeviceInitialise().ConfigureAwait(false);

            if (_disposed)
            {
                return Task.CompletedTask;
            }

            return name switch
            {
                SessionMessageType.Play => SendPlayCommand(data as PlayRequest),
                SessionMessageType.Playstate => SendPlaystateCommand(data as PlaystateRequest),
                SessionMessageType.GeneralCommand => SendGeneralCommand(data as GeneralCommand),
                _ => Task.CompletedTask
            };
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
                _profileManager.DeleteProfile(_device.Profile.Id);
                _ = _device.DeviceUnavailable();
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

            if (usn.Contains(_device.Uuid, StringComparison.OrdinalIgnoreCase)
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

                var positionTicks = GetProgressPositionTicks(streamInfo);

                await ReportPlaybackStopped(streamInfo, positionTicks).ConfigureAwait(false);

                var mediaSource = await streamInfo.GetMediaSource(CancellationToken.None).ConfigureAwait(false);

                var duration = mediaSource == null ?
                    _device.Duration?.Ticks :
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
                    await SetPlaylistIndex(_currentPlaylistIndex + 1).ConfigureAwait(false);
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
            var ticks = _device.Position.Ticks;

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
                IsMuted = _device.IsMuted,
                IsPaused = _device.IsPaused,
                MediaSourceId = info.MediaSourceId,
                AudioStreamIndex = info.AudioStreamIndex,
                SubtitleStreamIndex = info.SubtitleStreamIndex,
                VolumeLevel = _device.Volume,

                // TODO
                CanSeek = true,

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

        private Task SendPlayCommand(PlayRequest? command)
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

            var playlist = new PlaylistItem[len];
            playlist[0] = CreatePlaylistItem(items[0], user, command.StartPositionTicks ?? 0, command.MediaSourceId, command.AudioStreamIndex, command.SubtitleStreamIndex);
            for (int i = 1; i < len; i++)
            {
                playlist[i] = CreatePlaylistItem(items[i], user, 0, null, null, null);
            }

            _logger.LogDebug("{Name} - Playlist created", _session.DeviceName);

            switch (command.PlayCommand)
            {
                // track offset./
                case PlayCommand.PlayNow:
                    // Reset playlist and re-add tracks.
                    _playlist.Clear();
                    _playlist.AddRange(playlist);
                    _currentPlaylistIndex = playlist.Length > 0 ? 0 : -1;
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
                        if (_device.IsPlaying)
                        {
                            return Task.CompletedTask;
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

                        if (_device.IsPlaying)
                        {
                            return Task.CompletedTask;
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

            return PlayItems();
        }

        private Task SendPlaystateCommand(PlaystateRequest? command)
        {
            if (command == null)
            {
                return Task.CompletedTask;
            }

            switch (command.Command)
            {
                case PlaystateCommand.Stop:
                    _playlist.Clear();
                    return _device.Stop();

                case PlaystateCommand.Pause:
                    return _device.Pause();

                case PlaystateCommand.Unpause:
                    return _device.Play();

                case PlaystateCommand.PlayPause:
                    return _device.IsPaused ? _device.Play() : _device.Pause();

                case PlaystateCommand.Seek:
                    return Seek(command.SeekPositionTicks ?? 0);

                case PlaystateCommand.NextTrack:
                    return SetPlaylistIndex(_currentPlaylistIndex + 1);

                case PlaystateCommand.PreviousTrack:
                    return SetPlaylistIndex(_currentPlaylistIndex - 1);
            }

            return Task.CompletedTask;
        }

        private async Task Seek(long newPosition)
        {
            var media = _device.CurrentMediaInfo;

            if (media != null)
            {
                var info = StreamParams.ParseFromUrl(media.Url, _libraryManager, _mediaSourceManager);

                if (info.Item != null && !info.IsDirectStream)
                {
                    var user = !_session.UserId.Equals(Guid.Empty) ? _userManager.GetUserById(_session.UserId) : null;
                    var newItem = CreatePlaylistItem(info.Item, user, newPosition, info.MediaSourceId, info.AudioStreamIndex, info.SubtitleStreamIndex);
                    if (newItem.StreamUrl == null)
                    {
                        _logger.LogError("Unable to seek on null stream.");
                        return;
                    }

                    await _device.SetAvTransport(newItem.StreamInfo.MediaType, true, newItem.StreamUrl, GetDlnaHeaders(newItem), newItem.Didl, true).ConfigureAwait(false);
                    SendNextTrackMessage();

                    return;
                }

                await SeekAfterTransportChange(newPosition, CancellationToken.None).ConfigureAwait(false);
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
            _device.SetNextAvTransport(nextItem.StreamUrl!, GetDlnaHeaders(nextItem), nextItem.Didl);
        }

        private void AddItemFromId(Guid id, ICollection<BaseItem> list)
        {
            var item = _libraryManager.GetItemById(id);
            if (item.MediaType is MediaType.Audio or MediaType.Video)
            {
                list.Add(item);
            }
        }

        private PlaylistItem CreatePlaylistItem(
            BaseItem item,
            User? user,
            long startPostionTicks,
            string? mediaSourceId,
            int? audioStreamIndex,
            int? subtitleStreamIndex)
        {
            var mediaSources = item is IHasMediaSources
                ? _mediaSourceManager.GetStaticMediaSources(item, true, user).ToArray()
                : Array.Empty<MediaSourceInfo>();

            var playlistItem = GetPlaylistItem(item, mediaSources, _device.Profile, _session.DeviceId, mediaSourceId, audioStreamIndex, subtitleStreamIndex);
            playlistItem.StreamInfo.StartPositionTicks = startPostionTicks;

            playlistItem.StreamUrl = DidlBuilder.NormalizeDlnaMediaUrl(playlistItem.StreamInfo.ToUrl(_serverAddress, _accessToken));

            var itemXml = new DidlBuilder(
                _device.Profile,
                user,
                _imageProcessor,
                _serverAddress,
                _accessToken,
                _userDataManager,
                _localization,
                _mediaSourceManager,
                _logger,
                _mediaEncoder,
                _libraryManager,
                (48, 48))
                .GetItemDidl(item, user, null, _session.DeviceId, FilterHelper.Filter(), playlistItem.StreamInfo);

            playlistItem.Didl = itemXml;

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

        private async Task<bool> PlayItems()
        {
            _logger.LogDebug("{Name} - Playing {Count} items", _session.DeviceName, _playlist.Count);

            await SetPlaylistIndex(0).ConfigureAwait(false);
            return true;
        }

        private async Task SetPlaylistIndex(int index)
        {
            if (index < 0 || index >= _playlist.Count)
            {
                _playlist.Clear();
                _currentPlaylistIndex = -1;
                await _device.Stop().ConfigureAwait(false);
                return;
            }

            _currentPlaylistIndex = index;
            var currentitem = _playlist[index];

            if (currentitem.StreamUrl != null)
            {
                await _device.SetAvTransport(currentitem.StreamInfo.MediaType, true, currentitem.StreamUrl, GetDlnaHeaders(currentitem), currentitem.Didl, index > 0).ConfigureAwait(false);
                SendNextTrackMessage();
            }

            var streamInfo = currentitem.StreamInfo;
            if (streamInfo.StartPositionTicks > 0 && streamInfo.IsDirectStream)
            {
                await SeekAfterTransportChange(streamInfo.StartPositionTicks, CancellationToken.None).ConfigureAwait(false);
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
            _device.PlaybackStart -= OnDevicePlaybackStart;
            _device.PlaybackProgress -= OnDevicePlaybackProgress;
            _device.PlaybackStopped -= OnDevicePlaybackStopped;
            _device.MediaChanged -= OnDeviceMediaChanged;
            _device.OnDeviceUnavailable = null;
            _device.Dispose();

            _disposed = true;
        }

        private Task SendGeneralCommand(GeneralCommand? command)
        {
            if (command == null)
            {
                return Task.CompletedTask;
            }

            switch (command.Name)
            {
                case GeneralCommandType.VolumeDown:
                    return _device.VolumeDown();
                case GeneralCommandType.VolumeUp:
                    return _device.VolumeUp();
                case GeneralCommandType.Mute:
                    return _device.Mute();
                case GeneralCommandType.Unmute:
                    return _device.Unmute();
                case GeneralCommandType.ToggleMute:
                    return _device.ToggleMute();
                case GeneralCommandType.SetAudioStreamIndex:
                    if (!command.Arguments.TryGetValue("Index", out string? index))
                    {
                        throw new ArgumentException("SetAudioStreamIndex argument cannot be null");
                    }

                    if (int.TryParse(index, NumberStyles.Integer, CultureDefault.UsCulture, out var val))
                    {
                        return SetAudioStreamIndex(val);
                    }

                    throw new ArgumentException("Unsupported SetAudioStreamIndex value supplied.");

                case GeneralCommandType.SetSubtitleStreamIndex:
                    if (!command.Arguments.TryGetValue("Index", out index))
                    {
                        throw new ArgumentException("SetSubtitleStreamIndex argument cannot be null");
                    }

                    if (int.TryParse(index, NumberStyles.Integer, CultureDefault.UsCulture, out val))
                    {
                        return SetSubtitleStreamIndex(val);
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

                    _device.Volume = volume;
                    return Task.CompletedTask;

                default:
                    return Task.CompletedTask;
            }
        }

        private async Task SetAudioStreamIndex(int? newIndex)
        {
            var media = _device.CurrentMediaInfo;

            if (media != null)
            {
                var info = StreamParams.ParseFromUrl(media.Url, _libraryManager, _mediaSourceManager);

                if (info.Item != null)
                {
                    var newPosition = GetProgressPositionTicks(info) ?? 0;

                    var user = !_session.UserId.Equals(Guid.Empty) ? _userManager.GetUserById(_session.UserId) : null;
                    var newItem = CreatePlaylistItem(info.Item, user, newPosition, info.MediaSourceId, newIndex, info.SubtitleStreamIndex);

                    bool seekAfter = newItem.StreamInfo.IsDirectStream;

                    // Pass our intentions to the device, so that it doesn't restart at the beginning, only to then seek.
                    if (newItem.StreamUrl == null)
                    {
                        _logger.LogError("Unable set audio on null stream.");
                        return;
                    }

                    await _device.SetAvTransport(newItem.StreamInfo.MediaType, !seekAfter, newItem.StreamUrl, GetDlnaHeaders(newItem), newItem.Didl, true).ConfigureAwait(false);
                    SendNextTrackMessage();

                    if (seekAfter)
                    {
                        await SeekAfterTransportChange(newPosition, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task SetSubtitleStreamIndex(int? newIndex)
        {
            var media = _device.CurrentMediaInfo;

            if (media?.Url != null)
            {
                var info = StreamParams.ParseFromUrl(media.Url, _libraryManager, _mediaSourceManager);

                if (info.Item != null)
                {
                    var newPosition = GetProgressPositionTicks(info) ?? 0;

                    var user = !_session.UserId.Equals(Guid.Empty) ? _userManager.GetUserById(_session.UserId) : null;
                    var newItem = CreatePlaylistItem(info.Item, user, newPosition, info.MediaSourceId, info.AudioStreamIndex, newIndex);

                    bool seekAfter = newItem.StreamInfo.IsDirectStream && newPosition > 0;

                    if (newItem.StreamUrl == null)
                    {
                        _logger.LogError("Unable to set subtitle index on null stream.");
                        return;
                    }

                    // Pass our intentions to the device, so that it doesn't restart at the beginning, only to then seek.
                    await _device.SetAvTransport(newItem.StreamInfo.MediaType, !seekAfter, newItem.StreamUrl, GetDlnaHeaders(newItem), newItem.Didl, true).ConfigureAwait(false);
                    SendNextTrackMessage();

                    if (seekAfter)
                    {
                        await SeekAfterTransportChange(newPosition, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task SeekAfterTransportChange(long positionTicks, CancellationToken cancellationToken)
        {
            const int MaxWait = 15000000;
            const int Interval = 500;

            var currentWait = 0;

            while (!_device.IsPlaying && currentWait < MaxWait)
            {
                await Task.Delay(Interval, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                currentWait += Interval;
            }

            await _device.Seek(TimeSpan.FromTicks(positionTicks)).ConfigureAwait(false);
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
                request.Item = libraryManager.GetItemById(request.ItemId);
                request.MediaSourceManager = mediaSourceManager;

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
            private static Guid GetItemId(string? url)
            {
                if (string.IsNullOrEmpty(url))
                {
                    throw new ArgumentNullException(nameof(url));
                }

                var parts = url.Split('/');

                for (var i = 0; i < parts.Length - 1; i++)
                {
                    var part = parts[i];

                    if (!string.Equals(part, "audio", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(part, "videos", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (Guid.TryParse(parts[i + 1], out var result))
                    {
                        return result;
                    }
                }

                return Guid.Empty;
            }
        }
    }
}
