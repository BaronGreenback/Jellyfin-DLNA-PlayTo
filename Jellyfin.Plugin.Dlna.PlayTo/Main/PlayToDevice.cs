using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Plugin.Dlna.Culture;
using Jellyfin.Plugin.Dlna.Didl;
using Jellyfin.Plugin.Dlna.EventArgs;
using Jellyfin.Plugin.Dlna.Model;
using Jellyfin.Plugin.Dlna.PlayTo.EventArgs;
using Jellyfin.Plugin.Dlna.PlayTo.Model;
using Jellyfin.Plugin.Dlna.Profiles;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Notifications;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dlna.PlayTo.Main
{
    /// <summary>
    /// Enum of service type indices.
    /// </summary>
    internal enum ServiceType
    {
        /// <summary>
        /// The index of the connection manager service.
        /// </summary>
        ConnectionManager = 0,

        /// <summary>
        /// The index of the render control service.
        /// </summary>
        RenderControl = 1,

        /// <summary>
        /// The index of the transport service.
        /// </summary>
        AvTransport = 2
    }

    /// <summary>
    /// Enum of queue commands.
    /// </summary>
    internal enum QueueCommands
    {
        /// <summary>
        /// Queue command.
        /// </summary>
        Queue = 0,

        /// <summary>
        /// Queue next whilst playing (Not supported on all devices).
        /// </summary>
        QueueNext = 1,

        /// <summary>
        /// Set device volume command.
        /// </summary>
        SetVolume = 2,

        /// <summary>
        /// Play command.
        /// </summary>
        Play = 3,

        /// <summary>
        /// Stop command.
        /// </summary>
        Stop = 4,

        /// <summary>
        /// Pause command.
        /// </summary>
        Pause = 5,

        /// <summary>
        /// Mute sound.
        /// </summary>
        Mute = 6,

        /// <summary>
        /// Unmute sound.
        /// </summary>
        UnMute = 7,

        /// <summary>
        /// Seek command.
        /// </summary>
        Seek = 10,

        /// <summary>
        /// Toggle mute.
        /// </summary>
        ToggleMute = 9
    }

    /// <summary>
    /// Code for managing a DLNA PlayTo device.
    ///
    /// The core of the code is based around the three methods ProcessSubscriptionEvent, TimerCallback and ProcessQueue.
    ///
    /// All incoming user actions are queue into the _queue, which is subsequently
    /// actioned by ProcessQueue function. This not only provides a level of rate limiting,
    /// but also stops repeated duplicate commands from being sent to the devices.
    ///
    /// TimerCallback is the manual handler for the device if it doesn't support subscriptions.
    /// It periodically polls for the settings.
    /// ProcessSubscriptionEvent is the handler for events that the device sends us.
    ///
    /// Both these two methods work side by side getting constant updates, using mutual
    /// caching to ensure the device isn't polled too frequently.
    /// </summary>
    internal class PlayToDevice : IDisposable
    {
        /// <summary>
        /// Constants used in SendCommand.
        /// </summary>
        private const int Now = 1;
        private const int Never = 0;
        private const int Normal = -1;

        private static readonly XNamespace _ud = "urn:schemas-upnp-org:device-1-0";
        private static readonly DefaultProfile _blankProfile = new();
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        private readonly object _timerLock = new();
        private readonly object _queueLock = new();
        private readonly TransportCommands?[] _transportCommands = { null, null, null };

        /// <summary>
        /// Device's volume boundary values.
        /// </summary>
        private readonly ValueRange _volRange = new();

        /// <summary>
        /// Holds the URL for the Jellyfin web server.
        /// </summary>
        private readonly string _serverAddress;

        /// <summary>
        /// Outbound events processing queue.
        /// </summary>
        private readonly List<KeyValuePair<QueueCommands, object>> _queue = new();

        /// <summary>
        /// Host network response reply time.
        /// </summary>
        private TimeSpan _transportOffset = TimeSpan.Zero;

        /// <summary>
        /// Holds the current playback position.
        /// </summary>
        private TimeSpan _position = TimeSpan.Zero;

        private bool _disposed;
        private Timer? _timer;

        /// <summary>
        /// Connection failure retry counter.
        /// </summary>
        private int _connectFailureCount;

        /// <summary>
        /// Sound level prior to it being muted.
        /// </summary>
        private int _muteVol;

        /// <summary>
        /// True if this player is using subscription events.
        /// </summary>
        private bool _subscribed;

        /// <summary>
        /// Unique id used in subscription callbacks.
        /// </summary>
        private string? _sessionId;

        /// <summary>
        /// Transport service subscription SID value.
        /// </summary>
        private string? _transportSid;

        /// <summary>
        /// Render service subscription SID value.
        /// </summary>
        private string? _renderSid;

        /// <summary>
        /// Used by the volume control to stop DOS on volume queries.
        /// </summary>
        private int _volume;

        /// <summary>
        /// Media Type that is currently "loaded".
        /// </summary>
        private DlnaProfileType? _mediaType;

        /// <summary>
        /// Hosts the last time we polled for the requests.
        /// </summary>
        private DateTime _lastVolumeRefresh;
        private DateTime _lastTransportRefresh;
        private DateTime _lastMetaRefresh;
        private DateTime _lastPositionRequest;
        private DateTime _lastMuteRefresh;

        /// <summary>
        /// Contains the item currently playing.
        /// </summary>
        private string _mediaPlaying = string.Empty;

        /// <summary>
        /// Initializes a new instance of the <see cref="PlayToDevice"/> class.
        /// </summary>
        /// <param name="playToDeviceInfo">The <see cref="PlayToDeviceInfo"/> instance.</param>
        /// <param name="httpClientFactory">The <see cref="IHttpClientFactory"/> instance.</param>
        /// <param name="logger">The <see cref="ILogger"/> instance.</param>
        /// <param name="serverAddress">The server address to use in the response.</param>
        private PlayToDevice(PlayToDeviceInfo playToDeviceInfo, IHttpClientFactory httpClientFactory, ILogger logger, string serverAddress)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _serverAddress = serverAddress;
            TransportState = TransportState.No_Media_Present;
            Profile = _blankProfile;
            Name = playToDeviceInfo.Name;
            Uuid = playToDeviceInfo.Uuid;
            Services = playToDeviceInfo.Services;
            foreach (var svc in Services)
            {
                svc?.Normalise(playToDeviceInfo.BaseUrl);
            }

            BaseUrl = playToDeviceInfo.BaseUrl;
        }

        /// <summary>
        /// Events called when playback starts.
        /// </summary>
        public event EventHandler<PlaybackEventArgs>? PlaybackStart;

        /// <summary>
        /// Events called during playback.
        /// </summary>
        public event EventHandler<PlaybackEventArgs>? PlaybackProgress;

        /// <summary>
        /// Events called when playback stops.
        /// </summary>
        public event EventHandler<PlaybackEventArgs>? PlaybackStopped;

        /// <summary>
        /// Events called when the media changes.
        /// </summary>
        public event EventHandler<MediaChangedEventArgs>? MediaChanged;

        /// <summary>
        /// Gets or sets a value indicating the maximum wait time for http responses in ms.
        /// </summary>
        public static int CtsTimeout { get; set; } = 10000;

        /// <summary>
        /// Gets or sets a value indicating the USERAGENT that is sent to devices.
        /// </summary>
        public static string UserAgent { get; set; } = "UPnP/1.0 DLNADOC/1.50 Jellyfin/{Version}";

        /// <summary>
        /// Gets or sets a value indicating the frequency of the device polling (ms).
        /// </summary>
        public static int TimerInterval { get; set; } = 30000;

        /// <summary>
        /// Gets or sets a value indicating the user queue processing frequency (ms).
        /// </summary>
        public static int QueueInterval { get; set; } = 1000;

        /// <summary>
        /// Gets or sets a value indicating the friendly name used with devices.
        /// </summary>
        public static string FriendlyName { get; set; } = "Jellyfin";

        /// <summary>
        /// Gets the device's Uuid.
        /// </summary>
        public string Uuid { get; }

        /// <summary>
        /// Gets the device's name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets a value indicating whether the sound is muted.
        /// </summary>
        public bool IsMuted { get; private set; }

        /// <summary>
        /// Gets the current media information.
        /// </summary>
        public UBaseObject? CurrentMediaInfo { get; private set; }

        /// <summary>
        /// Gets the baseUrl of the device.
        /// </summary>
        public string BaseUrl { get; }

        /// <summary>
        /// Gets or sets the Volume.
        /// </summary>
        public int Volume
        {
            get
            {
                if (!_subscribed)
                {
                    try
                    {
                        RefreshVolumeIfNeeded().GetAwaiter().GetResult();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Ignore.
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger.LogError(ex, "{Name}: Error getting device volume.", Name);
                    }
                }

                int calculateVolume = _volRange.GetValue(_volume);
                if (Tracing)
                {
                    _logger.LogDebug("{Name}: Returning a volume setting of {Volume}.", Name, calculateVolume);
                }

                return calculateVolume;
            }

            set
            {
                if (value is < 0 or > 100)
                {
                    return;
                }

                // Make ratio adjustments as not all devices have volume level 100. (User range => Device range.)
                int newValue = _volRange.GetValue(value);
                if (newValue != _volume)
                {
                    QueueEvent(QueueCommands.SetVolume, newValue);
                }
            }
        }

        /// <summary>
        /// Gets the Duration.
        /// </summary>
        public TimeSpan? Duration { get; private set; }

        /// <summary>
        /// Gets the Position.
        /// </summary>
        public TimeSpan Position
        {
            get => _position.Add(_transportOffset);
            private set => _position = value;
        }

        /// <summary>
        /// Gets a value indicating whether IsPlaying.
        /// </summary>
        public bool IsPlaying => TransportState == TransportState.Playing;

        /// <summary>
        /// Gets a value indicating whether IsPaused.
        /// </summary>
        public bool IsPaused => TransportState is TransportState.Paused or TransportState.Paused_Playback;

        /// <summary>
        /// Gets or sets the OnDeviceUnavailable.
        /// </summary>
        public Action? OnDeviceUnavailable { get; set; }

        /// <summary>
        /// Gets the device's profile.
        /// </summary>
        public DeviceProfile Profile { get; private set; }

        /// <summary>
        /// Gets a value indicating whether trace information should be redirected to the logs.
        /// </summary>
        private static bool Tracing => DlnaPlayTo.Instance!.Configuration.EnablePlayToDebug;

        /// <summary>
        /// Gets the Render Control service.
        /// </summary>
        private DeviceService? RenderControl => Services[(int)ServiceType.RenderControl];

        /// <summary>
        /// Gets the services the device supports.
        /// </summary>
        private DeviceService?[] Services { get; }

        /// <summary>
        /// Gets a value indicating whether IsStopped.
        /// </summary>
        private bool IsStopped => TransportState == TransportState.Stopped;

        /// <summary>
        /// Gets the AVTransport service.
        /// </summary>
        private DeviceService? AvTransport => Services[(int)ServiceType.AvTransport];

        /// <summary>
        /// Gets or sets the TransportState.
        /// </summary>
        private TransportState TransportState { get; set; }

        /// <summary>
        /// Generates a PlayToDeviceInfo from the device at the address contained in the url parameter."/>.
        /// </summary>
        /// <param name="info">The <see cref="DiscoveredSsdpDevice"/> instance.</param>
        /// <param name="httpClientFactory">The <see cref="IHttpClientFactory"/> instance.</param>
        /// <param name="logger">The <see cref="ILogger"/> instance.</param>
        /// <returns>The <see cref="Task"/>.</returns>
        public static async Task<PlayToDeviceInfo?> ParseDevice(DiscoveredSsdpDevice info, IHttpClientFactory httpClientFactory, ILogger logger)
        {
            XElement document;
            try
            {
                document = await GetDataAsync(httpClientFactory, info.Location, logger).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch
#pragma warning restore CA1031 // Do not catch general exception types
            {
                return null;
            }

            var deviceType = document.Descendants(_ud.GetName("deviceType")).FirstOrDefault();
            if (deviceType == null || !deviceType.Value.StartsWith("urn:schemas-upnp-org:device:MediaRenderer:", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("Ignoring device as its a {device}", deviceType?.Value ?? "Blank");
                return null;
            }

            var uuid = document.Descendants(_ud.GetName("UDN")).FirstOrDefault();
            if (uuid == null)
            {
                return null;
            }

            var parsedUrl = new Uri(info.Location);
            var friendlyName = document.Descendants(_ud.GetName("friendlyName")).FirstOrDefault()?.Value;

            var name = string.IsNullOrWhiteSpace(friendlyName) ?
                    parsedUrl.OriginalString :
                    // Some devices include their MAC addresses as part of their name.
                    Regex.Replace(friendlyName, "([0-9A-Fa-f]{2}[:-]){5}([0-9A-Fa-f]{2})", string.Empty)
                    .Replace("()", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("[]", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Trim();

            var deviceProperties = new PlayToDeviceInfo(name, $"{parsedUrl.Scheme}://{parsedUrl.Host}:{parsedUrl.Port}", uuid.Value, info.Endpoint.Address)
            {
                FriendlyName = friendlyName
            };

            var model = document.Descendants(_ud.GetName("modelName")).FirstOrDefault();
            if (model != null)
            {
                deviceProperties.ModelName = model.Value;
            }

            var modelNumber = document.Descendants(_ud.GetName("modelNumber")).FirstOrDefault();
            if (modelNumber != null)
            {
                deviceProperties.ModelNumber = modelNumber.Value;
            }

            var manufacturer = document.Descendants(_ud.GetName("manufacturer")).FirstOrDefault();
            if (manufacturer != null)
            {
                deviceProperties.Manufacturer = manufacturer.Value;
            }

            var manufacturerUrl = document.Descendants(_ud.GetName("manufacturerURL")).FirstOrDefault();
            if (manufacturerUrl != null)
            {
                deviceProperties.ManufacturerUrl = manufacturerUrl.Value;
            }

            var modelUrl = document.Descendants(_ud.GetName("modelURL")).FirstOrDefault();
            if (modelUrl != null)
            {
                deviceProperties.ModelUrl = modelUrl.Value;
            }

            var serialNumber = document.Descendants(_ud.GetName("serialNumber")).FirstOrDefault();
            if (serialNumber != null)
            {
                deviceProperties.SerialNumber = serialNumber.Value;
            }

            var modelDescription = document.Descendants(_ud.GetName("modelDescription")).FirstOrDefault();
            if (modelDescription != null)
            {
                deviceProperties.ModelDescription = modelDescription.Value;
            }

            string[] serviceTypeList = { "urn:schemas-upnp-org:service:ConnectionManager", "urn:schemas-upnp-org:service:RenderingControl", "urn:schemas-upnp-org:service:AVTransport" };

            foreach (var services in document.Descendants(_ud.GetName("serviceList")))
            {
                var servicesList = services?.Descendants(_ud.GetName("service"));
                if (servicesList == null)
                {
                    continue;
                }

                foreach (var element in servicesList)
                {
                    var service = Create(element);
                    for (int index = 0; index < 3; index++)
                    {
                        if (service.ServiceType.StartsWith(serviceTypeList[index], StringComparison.OrdinalIgnoreCase))
                        {
                            deviceProperties.Services[index] = service;
                        }
                    }
                }
            }

            return deviceProperties;
        }

        /// <summary>
        /// Generates a PlayToDevice from the properties given."/>.
        /// </summary>
        /// <param name="deviceProperties">The <see cref="PlayToDeviceInfo"/> instance.</param>
        /// <param name="httpClientFactory">The <see cref="IHttpClientFactory"/> instance.</param>
        /// <param name="logger">The <see cref="ILogger"/> instance.</param>
        /// <param name="serverAddress">The server address to embed in the Didl.</param>
        /// <param name="profileManager">The <see cref="IDlnaProfileManager"/>.</param>
        /// <returns>The <see cref="Task"/>.</returns>
        public static async Task<PlayToDevice> CreateDevice(
            PlayToDeviceInfo deviceProperties,
            IHttpClientFactory httpClientFactory,
            ILogger logger,
            string serverAddress,
            IDlnaProfileManager profileManager)
        {
            var device = new PlayToDevice(deviceProperties, httpClientFactory, logger, serverAddress);

            // Get device capabilities.
            var capabilities = await device.GetProtocolInfo().ConfigureAwait(false);

            device.Profile = profileManager.GetProfile(deviceProperties, capabilities, true);

            return device;
        }

        /// <summary>
        /// Starts the monitoring of the device.
        /// </summary>
        /// <returns>Task.</returns>
        public async Task DeviceInitialise()
        {
            if (_timer == null)
            {
                // Reset the caching timings.
                _lastPositionRequest = DateTime.UtcNow.AddSeconds(-5);
                _lastVolumeRefresh = _lastPositionRequest;
                _lastTransportRefresh = _lastPositionRequest;
                _lastMetaRefresh = _lastPositionRequest;
                _lastMuteRefresh = _lastPositionRequest;

                try
                {
                    // Make sure that the device doesn't have a range on the volume controls.
                    var commands = await GetServiceCommands(ServiceType.RenderControl)
                        .ConfigureAwait(false);
                    var volumeSettings = commands?.StateVariables.FirstOrDefault(i => i.Name == StateVariableType.Volume);
                    if (volumeSettings != null)
                    {
                        int min = 0;
                        int max = 100;

                        if (volumeSettings.AllowedValues?.Count > 0)
                        {
                            _ = int.TryParse(volumeSettings.AllowedValues[0], out min);
                            _ = int.TryParse(volumeSettings.AllowedValues[1], out max);
                        }
                        else if (volumeSettings.AllowedValueRange?.Count > 0)
                        {
                            _ = int.TryParse(volumeSettings.AllowedValueRange["minimum"], out min);
                            _ = int.TryParse(volumeSettings.AllowedValueRange["maximum"], out max);
                        }

                        _volRange.Min = min;
                        _volRange.Max = max;
                        _volRange.Range = max - min;
                    }

                    await SubscribeAsync().ConfigureAwait(false);

                    // Update the position, volume and subscript for events.
                    await GetPositionRequest().ConfigureAwait(false);
                    await GetVolume().ConfigureAwait(false);
                    await GetMute().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (HttpRequestException ex)
                {
                    if (Tracing)
                    {
                        _logger.LogDebug(ex, "{Name}: Error initialising device.", Name);
                    }
                }

                if (Tracing)
                {
                    _logger.LogDebug("{Name}: Starting timer.", Name);
                }

                _timer = new Timer(TimerCallback, null, 500, Timeout.Infinite);

                // Start the user command queue processor.
                await ProcessQueue().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Called when the device becomes unavailable.
        /// </summary>
        /// <returns>Task.</returns>
        public async Task DeviceUnavailable()
        {
            if (_subscribed)
            {
                if (Tracing)
                {
                    _logger.LogDebug("{Name}: Killing the timer.", Name);
                }

                // Attempt to unsubscribe - may not be possible.
                await UnSubscribeAsync().ConfigureAwait(false);

                lock (_timerLock)
                {
                    _timer?.Dispose();
                    _timer = null;
                }
            }
        }

        /// <summary>
        /// Refreshes the playTo device info.
        /// </summary>
        /// <param name="deviceProperties">The <see cref="PlayToDeviceInfo"/>.</param>
        /// <param name="profileManager">The <see cref="IDlnaProfileManager"/>.</param>
        /// <returns>Task.</returns>
        public async Task RefreshDevice(PlayToDeviceInfo deviceProperties, IDlnaProfileManager profileManager)
        {
            // Get device capabilities.
            var capabilities = await GetProtocolInfo().ConfigureAwait(false);
            Profile = profileManager.GetProfile(deviceProperties, capabilities, true);
        }

        /// <summary>
        /// Decreases the volume.
        /// </summary>
        /// <returns>Task.</returns>
        public Task VolumeDown()
        {
            QueueEvent(QueueCommands.SetVolume, Math.Max(_volume - _volRange.Step, _volRange.Min));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Increases the volume.
        /// </summary>
        /// <returns>Task.</returns>
        public Task VolumeUp()
        {
            QueueEvent(QueueCommands.SetVolume, Math.Min(_volume + _volRange.Step, _volRange.Max));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Toggles Mute.
        /// </summary>
        /// <returns>Task.</returns>
        public Task ToggleMute()
        {
            AddOrCancelIfQueued(QueueCommands.ToggleMute);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Starts playback.
        /// </summary>
        /// <returns>Task.</returns>
        public Task Play()
        {
            QueueEvent(QueueCommands.Play);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Stops playback.
        /// </summary>
        /// <returns>Task.</returns>
        public Task Stop()
        {
            QueueEvent(QueueCommands.Stop);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Pauses playback.
        /// </summary>
        /// <returns>Task.</returns>
        public Task Pause()
        {
            QueueEvent(QueueCommands.Pause);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Mutes the sound.
        /// </summary>
        /// <returns>Task.</returns>
        public Task Mute()
        {
            QueueEvent(QueueCommands.Mute);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Resumes the sound.
        /// </summary>
        /// <returns>Task.</returns>
        public Task Unmute()
        {
            QueueEvent(QueueCommands.UnMute);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Moves playback to a specific point.
        /// </summary>
        /// <param name="value">The point at which playback will resume.</param>
        /// <returns>Task.</returns>
        public Task Seek(TimeSpan value)
        {
            QueueEvent(QueueCommands.Seek, value);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Specifies new media to play.
        /// </summary>
        /// <param name="mediaType">The type of media the url points to.</param>
        /// <param name="resetPlay">In we are already playing this item, do we restart from the beginning.</param>
        /// <param name="url">Url of media.</param>
        /// <param name="headers">Headers.</param>
        /// <param name="metadata">Media metadata.</param>
        /// <param name="immediate">Set to true to effect an immediate change.</param>
        /// <param name="position">Position to seek after change.</param>
        /// <returns>Task.</returns>
        public async Task SetAvTransport(DlnaProfileType mediaType, bool resetPlay, string url, string headers, string metadata, bool immediate, long position)
        {
            var media = new MediaData(url, headers, metadata, mediaType, resetPlay, position);
            if (immediate)
            {
                await QueueMedia(media).ConfigureAwait(false);
                return;
            }

            QueueEvent(QueueCommands.Queue, media);

            // Not all devices auto-play after loading media (eg. Hisense)
            QueueEvent(QueueCommands.Play);

            if (position != 0)
            {
                QueueEvent(QueueCommands.Seek, position);
            }
        }

        /// <summary>
        /// Specifies new media to play.
        /// </summary>
        /// <param name="url">Url of media.</param>
        /// <param name="headers">Headers.</param>
        /// <param name="metadata">Media metadata.</param>
        public void SetNextAvTransport(string url, string headers, string metadata)
        {
            if (!IsPlaying)
            {
                return;
            }

            var media = new MediaData(url, headers, metadata, (DlnaProfileType)_mediaType!, false, 0);
            QueueEvent(QueueCommands.QueueNext, media);
        }

        /// <summary>
        /// Disposes this object.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Creates a UBaseObject from the information provided.
        /// </summary>
        /// <param name="properties">The XML properties.</param>
        /// <returns>The <see cref="UBaseObject"/>.</returns>
        private static UBaseObject? CreateUBaseObject(Dictionary<string, string> properties)
        {
            var uBase = new UBaseObject();

            if (properties.TryGetValue("item.id", out string? value))
            {
                uBase.Id = value;
            }

            if (properties.TryGetValue("res", out value))
            {
                if (string.IsNullOrEmpty(value))
                {
                    properties.TryGetValue("TrackURI", out value);
                }

                if (!string.IsNullOrEmpty(value))
                {
                    uBase.Url = value;
                }
            }

            if (properties.TryGetValue("res", out value))
            {
                uBase.Url = value;
            }

            return string.IsNullOrEmpty(uBase.Url) ? null : uBase;
        }

        /// <summary>
        /// Creates a DeviceService from an XML element.
        /// </summary>
        /// <param name="element">The element.</param>
        /// <returns>The <see cref="DeviceService"/>.</returns>
        private static DeviceService Create(XElement element)
        {
            var type = element.GetDescendantValue(_ud.GetName("serviceType"));
            var id = element.GetDescendantValue(_ud.GetName("serviceId"));
            var scpdUrl = element.GetDescendantValue(_ud.GetName("SCPDURL"));
            var controlUrl = element.GetDescendantValue(_ud.GetName("controlURL"));
            var eventSubUrl = element.GetDescendantValue(_ud.GetName("eventSubURL"));

            return new DeviceService(type, id, scpdUrl, controlUrl, eventSubUrl);
        }

        private static string PrettyPrint(HttpHeaders m)
        {
            var sb = new StringBuilder(1024);
            foreach (var (key, value) in m)
            {
                sb.Append(key);
                sb.Append(": ");
                sb.AppendLine(value.FirstOrDefault());
            }

            return sb.ToString();
        }

        /// <summary>
        /// Gets service information from the DLNA clients.
        /// </summary>
        /// <param name="httpClientFactory">The IhttpClientFactory instance. <see cref="IHttpClientFactory"/>.</param>
        /// <param name="url">The destination URL.</param>
        /// <param name="logger">ILogger instance.</param>
        /// <returns>The <see cref="Task{XDocument}"/>.</returns>
        private static async Task<XElement> GetDataAsync(IHttpClientFactory httpClientFactory, string url, ILogger logger)
        {
            using var options = new HttpRequestMessage(HttpMethod.Get, url);
            options.Headers.UserAgent.ParseAdd(UserAgent);
            options.Headers.TryAddWithoutValidation("FriendlyName.dlna.org", FriendlyName);
            options.Headers.TryAddWithoutValidation("Accept", MediaTypeNames.Text.Xml);

            string reply = string.Empty;
            try
            {
                logger.LogDebug("GetDataAsync: Communicating with {Url}", url);

                if (Tracing)
                {
                    logger.LogDebug("-> {Url} Headers: {Headers:l}", url, PrettyPrint(options.Headers));
                }

                using var response = await httpClientFactory
                    .CreateClient(NamedClient.Default)
                    .SendAsync(options, HttpCompletionOption.ResponseHeadersRead, default).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    reply = await reader.ReadToEndAsync().ConfigureAwait(false);

                    if (XmlUtilities.ParseXml(reply, out XElement? doc))
                    {
                        if (Tracing)
                        {
                            logger.LogDebug("<- {Url}\r\n{Reply:l}", url, doc);
                        }

                        return doc;
                    }

                    logger.LogError("Invalid xml response: <- {Url}\r\n{Reply:l}", url, reply);
                }

                logger.LogDebug("Error: {Reason}", response.ReasonPhrase);

                throw new HttpRequestException(response.ReasonPhrase);
            }
            catch (XmlException)
            {
                logger.LogDebug("GetDataAsync: Badly formed XML returned {Reply:l}", reply);
                throw;
            }
            catch (HttpRequestException ex)
            {
                logger.LogDebug("GetDataAsync: Failed with {Message:l}", ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                // Show stack trace on other errors.
                logger.LogDebug(ex, "GetDataAsync: Failed.");
                throw;
            }
        }

        private async Task<TransportCommands?> GetServiceCommands(ServiceType serviceType)
        {
            var service = _transportCommands[(int)serviceType];
            if (service != null)
            {
                return service;
            }

            var commands = await GetProtocolAsync(Services[(int)serviceType]).ConfigureAwait(false);
            if (commands == null)
            {
                _logger.LogWarning("{Name}: GetProtocolAsync for {serviceType} returned null.", Name, serviceType);
                return null;
            }

            _transportCommands[(int)serviceType] = commands;
            return commands;
        }

        /// <summary>
        /// Enables the volume bar hovered over effect.
        /// </summary>
        /// <returns>Task.</returns>
        private async Task RefreshVolumeIfNeeded()
        {
            try
            {
                await GetVolume().ConfigureAwait(false);
                await GetMute().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Ignore.
            }
        }

        /// <summary>
        /// Restart the polling timer.
        /// </summary>
        /// <param name="when">When to restart the timer. Less than 0 = never, 0 = instantly, greater than 0 in 1 second.</param>
        private void RestartTimer(int when)
        {
            lock (_timerLock)
            {
                if (_disposed)
                {
                    return;
                }

                int delay = when switch
                {
                    Never => Timeout.Infinite,
                    Now => 100,
                    _ => TimerInterval
                };
                _timer?.Change(delay, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Adds an event to the user control queue.
        /// Later identical events overwrite earlier ones.
        /// </summary>
        /// <param name="command">Command to queue.</param>
        /// <param name="value">Command parameter.</param>
        private void QueueEvent(QueueCommands command, object? value = null)
        {
            lock (_queueLock)
            {
                // Does this action exist in the queue ?
                int index = _queue.FindIndex(item => item.Key == command);

                if (index != -1)
                {
                    if (Tracing)
                    {
                        _logger.LogDebug("{Name}: Replacing user event: {Command} {Value:l}", Name, command, value);
                    }

                    _queue.RemoveAt(index);
                }
                else if (Tracing)
                {
                    _logger.LogDebug("{Name}: Queuing user event: {Command} {Value:l}", Name, command, value);
                }

                _queue.Add(new KeyValuePair<QueueCommands, object>(command, value ?? 0));
            }
        }

        /// <summary>
        /// Removes an event from the queue if it exists, or adds it if it doesn't.
        /// </summary>
        private void AddOrCancelIfQueued(QueueCommands command)
        {
            lock (_queueLock)
            {
                int index = _queue.FindIndex(item => item.Key == command);

                if (index != -1)
                {
                    if (Tracing)
                    {
                        _logger.LogDebug("{Name}: Canceling user event: {Command}", Name, command);
                    }

                    _queue.RemoveAt(index);
                }
                else
                {
                    if (Tracing)
                    {
                        _logger.LogDebug("{Name} : Queuing user event: {Command}", Name, command);
                    }

                    _queue.Add(new KeyValuePair<QueueCommands, object>(command, 0));
                }
            }
        }

        /// <summary>
        /// Gets the next item from the queue.
        /// </summary>
        /// <param name="action">Next item if true is returned, otherwise null.</param>
        /// <param name="defaultAction">The value to return if not successful.</param>
        /// <returns>Success of the operation.</returns>
        private bool TryPop(out KeyValuePair<QueueCommands, object> action, KeyValuePair<QueueCommands, object> defaultAction)
        {
            lock (_queueLock)
            {
                if (_queue.Count <= 0)
                {
                    action = defaultAction;
                    return false;
                }

                action = _queue[0];
                _queue.RemoveAt(0);
                return true;
            }
        }

        private async Task ProcessQueue()
        {
            var defaultValue = new KeyValuePair<QueueCommands, object>(0, 0);

            // Infinite loop until dispose.
            while (!_disposed)
            {
                // Process items in the queue.
                while (TryPop(out var action, defaultValue))
                {
                    await SubscribeAsync().ConfigureAwait(false);

                    try
                    {
                        if (Tracing)
                        {
                            _logger.LogDebug("{Name}: Attempting action : {Key}", Name, action.Key);
                        }

                        switch (action.Key)
                        {
                            case QueueCommands.SetVolume:
                                {
                                    await SendVolumeRequest((int)action.Value).ConfigureAwait(false);
                                    break;
                                }

                            case QueueCommands.ToggleMute:
                                {
                                    if ((int)action.Value == 1)
                                    {
                                        var success = await SendMuteRequest(false).ConfigureAwait(true);
                                        if (!success)
                                        {
                                            // return to 20% of maximum.
                                            var sendVolume = _muteVol <= 0 ? _volRange.Step * 4 : _muteVol;
                                            await SendVolumeRequest(sendVolume).ConfigureAwait(false);
                                        }
                                    }
                                    else
                                    {
                                        var success = await SendMuteRequest(true).ConfigureAwait(true);
                                        if (!success)
                                        {
                                            await SendVolumeRequest(0).ConfigureAwait(false);
                                        }
                                    }

                                    break;
                                }

                            case QueueCommands.Play:
                                {
                                    await SendPlayRequest().ConfigureAwait(false);
                                    break;
                                }

                            case QueueCommands.Stop:
                                {
                                    await SendStopRequest().ConfigureAwait(false);
                                    break;
                                }

                            case QueueCommands.Pause:
                                {
                                    await SendPauseRequest().ConfigureAwait(false);
                                    break;
                                }

                            case QueueCommands.Mute:
                                {
                                    var success = await SendMuteRequest(true).ConfigureAwait(true);
                                    if (!success)
                                    {
                                        await SendVolumeRequest(0).ConfigureAwait(false);
                                    }
                                }

                                break;

                            case QueueCommands.UnMute:
                                {
                                    var success = await SendMuteRequest(false).ConfigureAwait(true);
                                    if (!success)
                                    {
                                        var sendVolume = _muteVol <= 0 ? _volRange.Step * 4 : _muteVol;
                                        await SendVolumeRequest(sendVolume).ConfigureAwait(false);
                                    }

                                    break;
                                }

                            case QueueCommands.Seek:
                                {
                                    await SendSeekRequest((TimeSpan)action.Value).ConfigureAwait(false);
                                    break;
                                }

                            case QueueCommands.Queue:
                                {
                                    await QueueMedia((MediaData)action.Value).ConfigureAwait(false);
                                    break;
                                }

                            case QueueCommands.QueueNext:
                                {
                                    await SendMediaRequest((MediaData)action.Value, false).ConfigureAwait(false);
                                    break;
                                }
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (HttpRequestException)
                    {
                        // Ignore.
                    }
                }

                await Task.Delay(QueueInterval).ConfigureAwait(false);
            }
        }

        private async Task QueueMedia(MediaData settings)
        {
            // Media Change event
            if (IsPlaying)
            {
                // Compare what is currently playing to what is being sent minus the time element.
                string thisMedia = Regex.Replace(_mediaPlaying, "&StartTimeTicks=\\d*", string.Empty);
                string newMedia = Regex.Replace(settings.Url, "&StartTimeTicks=\\d*", string.Empty);

                if (!string.Equals(thisMedia, newMedia, StringComparison.Ordinal))
                {
                    if (Tracing)
                    {
                        _logger.LogDebug("{Name}: Stopping current playback for transition.", Name);
                    }

                    bool success = await SendStopRequest().ConfigureAwait(false);

                    if (success)
                    {
                        // Save current progress.
                        TransportState = TransportState.Transitioning;
                        UpdateMediaInfo(null);
                    }
                }

                if (settings.ResetPlayback || settings.Position != TimeSpan.Zero)
                {
                    // Restart from the beginning.
                    if (Tracing)
                    {
                        _logger.LogDebug("{Name}: Resetting playback position.", Name);
                    }

                    bool success = await SendSeekRequest(settings.Position).ConfigureAwait(false);
                    if (success)
                    {
                        // Save progress and restart time.
                        UpdateMediaInfo(CurrentMediaInfo);
                        RestartTimer(Normal);

                        // We're finished. Nothing further to do.
                        return;
                    }
                }
            }

            await SendMediaRequest(settings, true).ConfigureAwait(false);
        }

        private async Task NotifyUser(string msg)
        {
            var notification = new NotificationRequest
            {
                Name = string.Format(CultureInfo.InvariantCulture, msg, "DLNA PlayTo"),
                NotificationType = _mediaType switch
                {
                    DlnaProfileType.Audio => NotificationType.AudioPlayback.ToString(),
                    DlnaProfileType.Video => NotificationType.VideoPlayback.ToString(),
                    _ => NotificationType.TaskFailed.ToString()
                }
            };

            if (Tracing)
            {
                _logger.LogDebug("{Name}: User notification: {Notification}", Name, notification.NotificationType);
            }

            try
            {
                await DlnaPlayTo.Instance!.SendNotification(notification).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                _logger.LogError(ex, "{Name} : Error sending notification.", Name);
            }
        }

        /// <summary>
        /// Sends a command to the DLNA device.
        /// </summary>
        /// <param name="service">The <see cref="DeviceService"/> to use.</param>
        /// <param name="command">Command to send.</param>
        /// <param name="postData">Information to post.</param>
        /// <param name="header"><i>ContentFeatures.dlna.org</i> header to include.</param>
        /// <returns>The <see cref="Task"/>.</returns>
        private async Task<Dictionary<string, string>?> SendCommandAsync(
            DeviceService service,
            string command,
            string postData,
            string? header = null)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(string.Empty);
            }

            var stopWatch = new Stopwatch();
            var cts = new CancellationTokenSource();

            using var options = new HttpRequestMessage(HttpMethod.Post, service.ControlUrl);
            options.Headers.UserAgent.ParseAdd(UserAgent);
            options.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{service.ServiceType}#{command}\"");
            options.Headers.TryAddWithoutValidation("Pragma", "no-cache");
            options.Headers.TryAddWithoutValidation("FriendlyName.dlna.org", FriendlyName);
            options.Headers.TryAddWithoutValidation("ContentType", MediaTypeNames.Text.Xml);
            if (!string.IsNullOrEmpty(header))
            {
                options.Headers.TryAddWithoutValidation("contentFeatures.dlna.org", header);
                options.Headers.TryAddWithoutValidation("transferMode.dlna.org", "Streaming");
            }

            options.Content = new StringContent(postData, Encoding.UTF8, MediaTypeNames.Text.Xml);

            if (Tracing)
            {
                _logger.LogDebug("{Name}:-> {Url}\r\nHeader\r\n{Headers:l}\r\nData\r\n{Data:l}", Name, service.ControlUrl, PrettyPrint(options.Headers), postData);
            }

            try
            {
                cts.CancelAfter(CtsTimeout);
                stopWatch.Start();

                var response = await _httpClientFactory
                    .CreateClient(NamedClient.Default)
                    .SendAsync(options, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

                // Get the response.
                using var responseCts = new CancellationTokenSource();
                cts.CancelAfter(CtsTimeout);
                await using var stream = await response.Content.ReadAsStreamAsync(responseCts.Token).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var xmlResponse = await reader.ReadToEndAsync().ConfigureAwait(false);

                XmlUtilities.XmlToDictionary(xmlResponse, out Dictionary<string, string>? results);
                return results;
            }
            catch (TaskCanceledException)
            {
                _logger.LogError("{Name}: SendCommandAsync timed out: {Url}.", Name, service.ControlUrl);
                return null;
            }
            catch (HttpRequestException ex)
            {
                var msg = string.Format(
                    "SendCommandAsync failed. {0}:-> {1}\r\nHeader\r\n{2:}\r\nData\r\n{3:}",
                    Name,
                    service.ControlUrl,
                    PrettyPrint(options.Headers),
                    postData);
                _logger.LogError(ex, msg);
                return null;
            }
            finally
            {
                cts.Dispose();
                stopWatch.Stop();

                // Calculate just under half of the round trip time so we can make the position slide more accurate.
                _transportOffset = stopWatch.Elapsed.Divide(1.8);
            }
        }

        /// <summary>
        /// Sends a command to the device and waits for a response.
        /// </summary>
        /// <param name="serviceType">The transport type.</param>
        /// <param name="actionCommand">The actionCommand.</param>
        /// <param name="name">The name.</param>
        /// <param name="commandParameter">The commandParameter.</param>
        /// <param name="dictionary">The dictionary.</param>
        /// <param name="header">The header.</param>
        /// <returns>The <see cref="Task{XDocument}"/>.</returns>
        private async Task<Dictionary<string, string>?> SendCommandResponseRequired(
            ServiceType serviceType,
            string actionCommand,
            object? name = null,
            string? commandParameter = null,
            Dictionary<string, string>? dictionary = null,
            string? header = null)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(string.Empty);
            }

            string? postData;

            var service = Services[(int)serviceType];
            var commands = await GetServiceCommands(serviceType).ConfigureAwait(false);
            var command = commands?.ServiceActions.FirstOrDefault(c => c.Name == actionCommand);
            if (service == null)
            {
                _logger.LogDebug("{Name}: Service {Service} not implemented.", Name, service);
                return null;
            }

            if (command == null)
            {
                _logger.LogDebug("{Name}: Command {Command} not supported.", Name, command);
                return null;
            }

            // commands cannot be null here, so ignore compiler warnings.
            if (commandParameter != null)
            {
                postData = commands!.BuildPost(command, service.ServiceType, name, commandParameter);
            }
            else if (dictionary != null)
            {
                postData = commands!.BuildPost(command, service.ServiceType, name, dictionary);
            }
            else if (name != null)
            {
                postData = commands!.BuildPost(command, service.ServiceType, name);
            }
            else
            {
                postData = commands!.BuildPost(command, service.ServiceType);
            }

            if (Tracing)
            {
                _logger.LogDebug("{Name}: Transmitting {Command} to device.", Name, command.Name);
            }

            return await SendCommandAsync(service, command.Name, postData, header).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends a command to the device, verifies receipt, and does return a response.
        /// </summary>
        /// <param name="serviceType">The transport commands type.</param>
        /// <param name="actionCommand">The action command to use.</param>
        /// <param name="name">The name.</param>
        /// <param name="commandParameter">The command parameter.</param>
        /// <param name="dictionary">The dictionary.</param>
        /// <param name="header">The header.</param>
        /// <returns>Returns success of the task.</returns>
        private async Task<bool> SendCommand(
            ServiceType serviceType,
            string actionCommand,
            object? name = null,
            string? commandParameter = null,
            Dictionary<string, string>? dictionary = null,
            string? header = null)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(string.Empty);
            }

            var result = await SendCommandResponseRequired(serviceType, actionCommand, name, commandParameter, dictionary, header).ConfigureAwait(false);
            if (result != null && result.TryGetValue(actionCommand + "Response", out string _))
            {
                return true;
            }

            if (result == null)
            {
                return false;
            }

            result.TryGetValue("faultstring", out var fault);
            result.TryGetValue("errorCode", out var errorCode);
            result.TryGetValue("errorDescription", out var errorDescription);

            string msg = $"{Name} : Cmd : {actionCommand}. Fault: {fault}. Code: {errorCode}. {errorDescription}";
            await NotifyUser(msg).ConfigureAwait(false);
            _logger.LogError(msg);

            if (!Tracing)
            {
                return false;
            }

            foreach (var (key, value) in result)
            {
                _logger.LogDebug("{Name}: {Key} = {Value:l}", Name, key, value);
            }

            return false;
        }

        /// <summary>
        /// Checks to see if DLNA subscriptions are implemented, and if so subscribes to changes.
        /// </summary>
        /// <param name="service">The service<see cref="DeviceService"/>.</param>
        /// <param name="sid">The SID for renewal, or null for subscription.</param>
        /// <returns>Task.</returns>
        private async Task<string?> SubscribeInternalAsync(DeviceService? service, string? sid)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(string.Empty);
            }

            if (service?.EventSubUrl == null)
            {
                return string.Empty;
            }

            using var options = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), service.EventSubUrl);
            options.Headers.UserAgent.ParseAdd(UserAgent);
            options.Headers.TryAddWithoutValidation("Accept", MediaTypeNames.Text.Xml);

            // Renewal or subscription?
            if (string.IsNullOrEmpty(sid))
            {
                if (string.IsNullOrEmpty(_sessionId))
                {
                    // If we haven't got a GUID yet - get one.
                    _sessionId = Guid.NewGuid().ToString();
                }

                // Create a unique callback url based up our GUID.
                options.Headers.TryAddWithoutValidation("CALLBACK", $"<{_serverAddress}/Dlna/Eventing/{_sessionId}>");
            }
            else
            {
                // Re-subscription id.
                options.Headers.TryAddWithoutValidation("SID", "uuid:{sid}");
            }

            options.Headers.TryAddWithoutValidation("HOST", _serverAddress);
            options.Headers.TryAddWithoutValidation("NT", "upnp:event");
            options.Headers.TryAddWithoutValidation("TIMEOUT", "Second-60");

            // uPnP v2 permits variables that are to returned at events to be defined.
            options.Headers.TryAddWithoutValidation("STATEVAR", "Mute,Volume,CurrentTrackURI,CurrentTrackMetaData,CurrentTrackDuration,RelativeTimePosition,TransportState");

            if (Tracing)
            {
                _logger.LogDebug("->{Name}:\r\nHeaders: {Headers:l}", service.EventSubUrl, PrettyPrint(options.Headers));
            }

            var cts = new CancellationTokenSource();
            try
            {
                cts.CancelAfter(CtsTimeout);
                using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                    .SendAsync(options, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (Tracing)
                    {
                        _logger.LogDebug("{Name}:<- Error:{StatusCode} : {Reason}", Name, response.StatusCode, response.ReasonPhrase);
                    }

                    return string.Empty;
                }

                if (!_subscribed)
                {
                    if (Tracing)
                    {
                        _logger.LogDebug("{Name}: SUBSCRIBE successful.", Name);
                    }

                    return response.Headers.GetValues("SID").FirstOrDefault();
                }
            }
            catch (TaskCanceledException)
            {
                _logger.LogError("{Name}: SUBSCRIBE timed out.", Name);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "{Name}: SUBSCRIBE failed: {StatusCode}", Name, ex.StatusCode);
            }
            finally
            {
                cts.Dispose();
            }

            return string.Empty;
        }

        /// <summary>
        /// Attempts to subscribe to the multiple services of a DLNA client.
        /// </summary>
        /// <returns>The <see cref="Task"/>.</returns>
        private async Task SubscribeAsync()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(string.Empty);
            }

            if (!_subscribed)
            {
                try
                {
                    // Start listening to DLNA events that come via the url through the PlayToManger.
                    DlnaPlayTo.Instance!.DlnaEvents += ProcessSubscriptionEvent;

                    // Subscribe to both AvTransport and RenderControl events.
                    _transportSid = await SubscribeInternalAsync(AvTransport, _transportSid).ConfigureAwait(false);
                    _renderSid = await SubscribeInternalAsync(RenderControl, _renderSid).ConfigureAwait(false);

                    if (Tracing)
                    {
                        _logger.LogDebug("{Name}: AVTransport SID {avtId}, RenderControl {RCId}.", Name, _transportSid, _renderSid);
                    }

                    _subscribed = true;
                }
                catch (ObjectDisposedException)
                {
                    // Ignore.
                }
            }
        }

        /// <summary>
        /// Resubscribe to DLNA events.
        /// Use in the event trigger, as an async wrapper.
        /// </summary>
        /// <returns>The <see cref="Task"/>.</returns>
        private async Task ResubscribeToEvents()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(string.Empty);
            }

            await SubscribeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Attempts to unsubscribe from a DLNA client.
        /// </summary>
        /// <param name="service">The service<see cref="DeviceService"/>.</param>
        /// <param name="sid">The sid.</param>
        /// <returns>Returns success of the task.</returns>
        private async Task<bool> UnSubscribeInternalAsync(DeviceService? service, string? sid)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(string.Empty);
            }

            if (service?.EventSubUrl == null || string.IsNullOrEmpty(sid))
            {
                return false;
            }

            using var options = new HttpRequestMessage(new HttpMethod("UNSUBSCRIBE"), service.EventSubUrl);
            options.Headers.UserAgent.ParseAdd(UserAgent);
            options.Headers.TryAddWithoutValidation("SID", "uuid: {sid}");
            options.Headers.TryAddWithoutValidation("Accept", MediaTypeNames.Text.Xml);

            if (Tracing)
            {
                _logger.LogDebug("-> {Name}:\r\nHeaders : {Headers:l}", service.EventSubUrl, PrettyPrint(options.Headers));
            }

            var cts = new CancellationTokenSource();
            try
            {
                cts.CancelAfter(CtsTimeout);

                using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                    .SendAsync(options, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    if (Tracing)
                    {
                        _logger.LogDebug("{Name}: UNSUBSCRIBE succeeded.", Name);
                    }

                    return true;
                }
                else if (Tracing)
                {
                    _logger.LogDebug("{Name}: UNSUBSCRIBE failed. {Content:l}", Name, response.Content);
                }
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("{Name}: UNSUBSCRIBE timed out.", Name);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "{Name}: UNSUBSCRIBE failed.", Name);
            }
            finally
            {
                cts.Dispose();
            }

            return false;
        }

        /// <summary>
        /// Attempts to unsubscribe from the multiple services of the DLNA client.
        /// </summary>
        /// <returns>The <see cref="Task"/>.</returns>
        private async Task UnSubscribeAsync()
        {
            if (_subscribed)
            {
                try
                {
                    // stop processing events.
                    DlnaPlayTo.Instance!.DlnaEvents -= ProcessSubscriptionEvent;

                    var success = await UnSubscribeInternalAsync(AvTransport, _transportSid).ConfigureAwait(false);
                    if (success)
                    {
                        // Keep Sid in case the user interacts with this device.
                        _transportSid = string.Empty;
                    }

                    success = await UnSubscribeInternalAsync(RenderControl, _renderSid).ConfigureAwait(false);
                    if (success)
                    {
                        _renderSid = string.Empty;
                    }

                    _subscribed = false;
                }
                catch (ObjectDisposedException)
                {
                    // Ignore.
                }
            }
        }

        /// <summary>
        /// This method gets called with the information the DLNA clients have passed through eventing.
        /// </summary>
        /// <param name="sender">PlayToController object.</param>
        /// <param name="args">Arguments passed from DLNA player.</param>
        private async void ProcessSubscriptionEvent(object? sender, DlnaEventArgs args)
        {
            if (args.Id != _sessionId)
            {
                return;
            }

            if (Tracing)
            {
                _logger.LogDebug("{Name}:<-Event received:\r\n{Response:l}", Name, args.Response);
            }

            try
            {
                if (XmlUtilities.XmlToDictionary(args.Response, out var reply))
                {
                    if (Tracing)
                    {
                        _logger.LogDebug("{Name}: Processing a subscription event.", Name);
                    }

                    // Render events.
                    if (reply.TryGetValue("Mute.val", out string? value) && int.TryParse(value, out int mute))
                    {
                        _lastMuteRefresh = DateTime.UtcNow;
                        if (Tracing)
                        {
                            _logger.LogDebug("Muted: {Mute}", mute);
                        }

                        IsMuted = mute == 1;
                    }

                    if (reply.TryGetValue("Volume.val", out value) && int.TryParse(value, out int volume))
                    {
                        if (Tracing)
                        {
                            _logger.LogDebug("{Name}: Volume: {Volume}", Name, volume);
                        }

                        _lastVolumeRefresh = DateTime.UtcNow;
                        _volume = volume;
                    }

                    if ((reply.TryGetValue("TransportState.val", out value) ||
                        reply.TryGetValue("CurrentTransportState.val", out value))
                        && Enum.TryParse<TransportState>(value, true, out var ts))
                    {
                        if (Tracing)
                        {
                            _logger.LogDebug("{Name}: TransportState: {State}", Name, ts);
                        }

                        // Mustn't process our own change playback event.
                        if (ts != TransportState && TransportState != TransportState.Transitioning)
                        {
                            TransportState = ts;

                            if (ts == TransportState.Stopped)
                            {
                                _lastTransportRefresh = DateTime.UtcNow;
                                UpdateMediaInfo(null);
                                RestartTimer(Normal);
                            }
                        }
                    }

                    // If the position isn't in this update, try to get it.
                    if (TransportState == TransportState.Playing)
                    {
                        if (!reply.TryGetValue("RelativeTimePosition.val", out value))
                        {
                            if (Tracing)
                            {
                                _logger.LogDebug("{Name}: Updating position as not included.", Name);
                            }

                            // Try and get the latest position update
                            await GetPositionRequest().ConfigureAwait(false);
                        }
                        else if (TimeSpan.TryParse(value, CultureDefault.UsCulture, out TimeSpan rel))
                        {
                            if (Tracing)
                            {
                                _logger.LogDebug("{Name}: RelativeTimePosition: {Position}", Name, rel);
                            }

                            Position = rel;
                            _lastPositionRequest = DateTime.UtcNow;
                        }
                    }

                    if (reply.TryGetValue("CurrentTrackDuration.val", out value)
                        && TimeSpan.TryParse(value, CultureDefault.UsCulture, out TimeSpan dur))
                    {
                        if (Tracing)
                        {
                            _logger.LogDebug("{Name}: CurrentTrackDuration: {Duration}", Name, dur);
                        }

                        Duration = dur;
                    }

                    UBaseObject? currentObject;

                    // Have we parsed any item metadata?
                    if (reply.ContainsKey("DIDL-Lite.xmlns"))
                    {
                        currentObject = CreateUBaseObject(reply);
                    }
                    else
                    {
                        currentObject = await GetMediaInfo().ConfigureAwait(false);
                    }

                    if (currentObject != null)
                    {
                        UpdateMediaInfo(currentObject);
                    }

                    _ = ResubscribeToEvents();
                }
                else if (Tracing)
                {
                    _logger.LogDebug("{Name}: Received blank event data : ", Name);
                }
            }
            catch (ObjectDisposedException)
            {
                // Ignore.
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError("{Name}: Unable to parse event response.", Name);
                if (Tracing)
                {
                    _logger.LogDebug(ex, "{Name}: Received:\r\n{Response:l}", Name, args.Response);
                }
            }
        }

        /// <summary>
        /// Timer Callback function that polls the DLNA status.
        /// </summary>
        /// <param name="sender">The sender.</param>
        private async void TimerCallback(object? sender)
        {
            if (_disposed)
            {
                return;
            }

            if (Tracing)
            {
                _logger.LogDebug("{Name}: Timer firing.", Name);
            }

            try
            {
                var transportState = await GetTransportStatus().ConfigureAwait(false);

                if (transportState == TransportState.Error)
                {
                    _logger.LogError("{Name}: Unable to get TransportState.", Name);

                    // Assume it's a one off.
                    RestartTimer(Normal);
                }
                else
                {
                    TransportState = transportState;

                    // If we're not playing anything make sure we don't get data more
                    // often than necessary to keep the Session alive.
                    if (transportState == TransportState.Stopped)
                    {
                        UpdateMediaInfo(null);
                        RestartTimer(Never);
                    }
                    else
                    {
                        var response = await SendCommandResponseRequired(ServiceType.AvTransport, "GetPositionInfo").ConfigureAwait(false);
                        if (response == null || response.Count == 0)
                        {
                            RestartTimer(Normal);
                            return;
                        }

                        if (response.TryGetValue("TrackDuration", out string? duration) && TimeSpan.TryParse(duration, CultureDefault.UsCulture, out TimeSpan dur))
                        {
                            Duration = dur;
                        }

                        if (response.TryGetValue("RelTime", out string? position) && TimeSpan.TryParse(position, CultureDefault.UsCulture, out TimeSpan rel))
                        {
                            Position = rel;
                        }

                        // Get current media info.
                        UBaseObject? currentObject;

                        // Have we parsed any item metadata?
                        if (!response.ContainsKey("DIDL-Lite.xmlns"))
                        {
                            // If not get some.
                            currentObject = await GetMediaInfo().ConfigureAwait(false);
                        }
                        else
                        {
                            currentObject = CreateUBaseObject(response);
                        }

                        if (currentObject != null)
                        {
                            UpdateMediaInfo(currentObject);
                        }

                        RestartTimer(Normal);
                    }

                    _connectFailureCount = 0;
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (HttpRequestException ex)
            {
                if (_disposed)
                {
                    return;
                }

                _logger.LogError(ex, "{Name}: Error updating device info.", Name);
                if (_connectFailureCount++ >= 3)
                {
                    if (Tracing)
                    {
                        _logger.LogDebug("{Name}: Disposing device due to loss of connection.", Name);
                    }

                    OnDeviceUnavailable?.Invoke();
                    return;
                }

                RestartTimer(Normal);
            }
        }

        private async Task<bool> SendVolumeRequest(int value)
        {
            if (_volume == value)
            {
                return false;
            }

            // Adjust for items that don't have a volume range 0..100.
            if (await SendCommand(ServiceType.RenderControl, "SetVolume", value).ConfigureAwait(false))
            {
                _volume = value;
            }

            return true;
        }

        /// <summary>
        /// Requests the volume setting from the client.
        /// </summary>
        /// <returns>Task.</returns>
        private async Task<bool> GetVolume()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(string.Empty);
            }

            if (_lastVolumeRefresh.AddSeconds(5) <= _lastVolumeRefresh)
            {
                return true;
            }

            string? volume = string.Empty;
            try
            {
                var response = await SendCommandResponseRequired(ServiceType.RenderControl, "GetVolume").ConfigureAwait(false);
                if (response != null && response.TryGetValue("GetVolumeResponse", out volume))
                {
                    if (!response.TryGetValue("CurrentVolume", out volume) || !int.TryParse(volume, out int value))
                    {
                        return true;
                    }

                    _volume = value;
                    if (_volume > 0)
                    {
                        _muteVol = _volume;
                    }

                    _lastVolumeRefresh = DateTime.UtcNow;

                    return true;
                }

                _logger.LogWarning("{Name}: GetVolume Failed.", Name);
            }
            catch (ObjectDisposedException)
            {
                // Ignore.
            }
            catch (FormatException)
            {
                _logger.LogError("{Name}: Error parsing GetVolume {Volume}.", Name, volume);
            }

            return false;
        }

        /// <summary>
        /// Requests the volume setting from the client.
        /// </summary>
        /// <returns>Task.</returns>
        private async Task<string> GetProtocolInfo()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(string.Empty);
            }

            try
            {
                var response = await SendCommandResponseRequired(ServiceType.ConnectionManager, "GetProtocolInfo").ConfigureAwait(false);
                if (response != null && response.TryGetValue("Sink", out string? settings))
                {
                    return settings!;
                }

                _logger.LogWarning("{Name}: GetProtocolInfo Failed.", Name);
            }
            catch (NullReferenceException)
            {
                // Ignore
            }
            catch (ObjectDisposedException)
            {
                // Ignore.
            }

            return string.Empty;
        }

        private async Task<bool> SendPauseRequest()
        {
            if (IsPaused)
            {
                return false;
            }

            if (!await SendCommand(ServiceType.AvTransport, "Pause", 1).ConfigureAwait(false))
            {
                return false;
            }

            // Stop user from issuing multiple commands.
            TransportState = TransportState.Paused;
            RestartTimer(Now);

            return true;
        }

        private async Task<bool> SendPlayRequest()
        {
            if (IsPlaying)
            {
                return false;
            }

            if (!await SendCommand(ServiceType.AvTransport, "Play", 1).ConfigureAwait(false))
            {
                return false;
            }

            // Stop user from issuing multiple commands.
            TransportState = TransportState.Playing;
            RestartTimer(Now);

            return true;
        }

        private async Task<bool> SendStopRequest()
        {
            if (!IsPlaying && !IsPaused)
            {
                return false;
            }

            if (!await SendCommand(ServiceType.AvTransport, "Stop", 1).ConfigureAwait(false))
            {
                return false;
            }

            // Stop user from issuing multiple commands.
            TransportState = TransportState.Stopped;
            RestartTimer(Now);

            return true;
        }

        /// <summary>
        /// Returns information associated with the current transport state of the specified instance.
        /// </summary>
        /// <returns>Task.</returns>
        private async Task<TransportState> GetTransportStatus()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(string.Empty);
            }

            if (_lastTransportRefresh.AddSeconds(5) >= DateTime.UtcNow)
            {
                return TransportState;
            }

            var response = await SendCommandResponseRequired(ServiceType.AvTransport, "GetTransportInfo").ConfigureAwait(false);
            if (response != null && response.ContainsKey("GetTransportInfoResponse"))
            {
                if (response.TryGetValue("CurrentTransportState", out string? transportState))
                {
                    if (Enum.TryParse<TransportState>(transportState, true, out var state))
                    {
                        _lastTransportRefresh = DateTime.UtcNow;
                        return state;
                    }

                    _logger.LogWarning("{Name}: Unable to parse CurrentTransportState {State}.", Name, TransportState);
                    return TransportState.Error;
                }

                _logger.LogWarning("{Name}: GetTransportState did not include CurrentTransportState.", Name);
                return TransportState.Error;
            }

            _logger.LogWarning("{Name}: GetTransportInfo failed.", Name);

            return TransportState.Error;
        }

        private async Task<bool> SendMuteRequest(bool value)
        {
            var result = false;

            if (IsMuted != value)
            {
                result = await SendCommand(ServiceType.RenderControl, "SetMute", value ? 1 : 0).ConfigureAwait(false);
            }

            return result;
        }

        /// <summary>
        /// Gets the mute setting from the client.
        /// </summary>
        /// <returns>Task.</returns>
        private async Task<bool> GetMute()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(string.Empty);
            }

            if (_lastMuteRefresh.AddSeconds(5) <= _lastMuteRefresh)
            {
                return true;
            }

            var response = await SendCommandResponseRequired(ServiceType.RenderControl, "GetMute").ConfigureAwait(false);
            if (response != null && response.ContainsKey("GetMuteResponse"))
            {
                if (response.TryGetValue("CurrentMute", out string? muted))
                {
                    IsMuted = string.Equals(muted, "1", StringComparison.OrdinalIgnoreCase);
                    return true;
                }

                _logger.LogWarning("{Name}: CurrentMute missing from GetMute.", Name);
            }
            else
            {
                _logger.LogWarning("{Name}: GetMute failed.", Name);
            }

            return false;
        }

        private async Task<bool> SendMediaRequest(MediaData settings, bool immediate)
        {
            var cmd = immediate ? "SetAVTransportURI" : "SetNextAVTransportURI";

            if (Tracing)
            {
                _logger.LogDebug(
                    "{Name}:\r\n{Command} Uri: {Url}\r\nDlnaHeaders: {Headers:l}\r\n{Metadata:l}",
                    Name,
                    cmd,
                    settings.Url,
                    settings.Headers,
                    settings.Metadata);
            }

            var dictionary = new Dictionary<string, string>
            {
                { "CurrentURI", settings.Url },
                { "CurrentURIMetaData",  Profile.EncodeContextOnTransmission ? HttpUtility.HtmlEncode(settings.Metadata) : settings.Metadata }
            };

            if (!await SendCommand(ServiceType.AvTransport, cmd, settings.Url, dictionary: dictionary, header: settings.Headers).ConfigureAwait(false))
            {
                return false;
            }

            await Task.Delay(50).ConfigureAwait(false);

            if (!await SendPlayRequest().ConfigureAwait(false))
            {
                return false;
            }

            // Update what is playing.
            _mediaPlaying = settings.Url;
            _mediaType = settings.MediaType;
            RestartTimer(Now);

            return true;
        }

        /// <summary>
        /// Runs the GetMediaInfo command.
        /// </summary>
        /// <returns>The <see cref="Task{UBaseObject}"/>.</returns>
        private async Task<UBaseObject?> GetMediaInfo()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(string.Empty);
            }

            if (_lastMetaRefresh.AddSeconds(5) >= DateTime.UtcNow)
            {
                return CurrentMediaInfo;
            }

            var response = await SendCommandResponseRequired(ServiceType.AvTransport, "GetMediaInfo").ConfigureAwait(false);
            if (response != null && response.ContainsKey("GetMediaInfoResponse"))
            {
                _lastMetaRefresh = DateTime.UtcNow;
                RestartTimer(Normal);

                var retVal = new UBaseObject();
                if (response.TryGetValue("item.id", out string? value))
                {
                    retVal.Id = value;
                }

                if (response.TryGetValue("CurrentURI", out value) && !string.IsNullOrWhiteSpace(value))
                {
                    retVal.Url = value;
                }

                return retVal;
            }

            _logger.LogWarning("{Name}: GetMediaInfo failed.", Name);

            return null;
        }

        private async Task<bool> SendSeekRequest(TimeSpan value)
        {
            if (!IsPlaying && !IsPaused)
            {
                return false;
            }

            if (!await SendCommand(
                ServiceType.AvTransport,
                "Seek",
                string.Format(CultureInfo.InvariantCulture, "{0:hh}:{0:mm}:{0:ss}", value),
                "REL_TIME").ConfigureAwait(false))
            {
                return false;
            }

            Position = value;
            RestartTimer(Now);
            return true;
        }

        private async Task<bool> GetPositionRequest()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(string.Empty);
            }

            if (_lastPositionRequest.AddSeconds(5) >= DateTime.UtcNow)
            {
                return true;
            }

            // Update position information.
            try
            {
                var response = await SendCommandResponseRequired(ServiceType.AvTransport, "GetPositionInfo").ConfigureAwait(false);
                if (response != null && response.ContainsKey("GetPositionInfoResponse"))
                {
                    if (response.TryGetValue("TrackDuration", out string? value) && TimeSpan.TryParse(value, CultureDefault.UsCulture, out TimeSpan d))
                    {
                        Duration = d;
                    }

                    if (response.TryGetValue("RelTime", out value) && TimeSpan.TryParse(value, CultureDefault.UsCulture, out TimeSpan r))
                    {
                        Position = r;
                        _lastPositionRequest = DateTime.Now;
                    }

                    RestartTimer(Normal);
                    return true;
                }
            }
            catch (ObjectDisposedException)
            {
                // Ignore.
            }

            return false;
        }

        /// <summary>
        /// Retrieves SSDP protocol information.
        /// </summary>
        /// <param name="services">The service to extract.</param>
        /// <returns>The <see cref="Task{TransportCommands}"/>.</returns>
        private async Task<TransportCommands?> GetProtocolAsync(DeviceService? services)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(string.Empty);
            }

            if (services == null)
            {
                return null;
            }

            var document = await GetDataAsync(_httpClientFactory, services.ScpdUrl, _logger).ConfigureAwait(false);

            return TransportCommands.Create(document, _logger);
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

            if (disposing)
            {
                _timer?.Dispose();
                DlnaPlayTo.Instance!.DlnaEvents -= ProcessSubscriptionEvent;
            }

            _disposed = true;
        }

        /// <summary>
        /// Updates the media info, firing events.
        /// </summary>
        /// <param name="mediaInfo">The mediaInfo<see cref="UBaseObject"/>.</param>
        private void UpdateMediaInfo(UBaseObject? mediaInfo)
        {
            var previousMediaInfo = CurrentMediaInfo;
            CurrentMediaInfo = mediaInfo;
            try
            {
                if (mediaInfo != null)
                {
                    if (previousMediaInfo == null)
                    {
                        if (IsStopped || string.IsNullOrWhiteSpace(mediaInfo.Url))
                        {
                            return;
                        }

                        if (Tracing)
                        {
                            _logger.LogDebug("{Name}: Firing playback started event.", Name);
                        }

                        PlaybackStart?.Invoke(this, new PlaybackEventArgs(mediaInfo));
                    }
                    else if (mediaInfo.Id == previousMediaInfo.Id)
                    {
                        if (string.IsNullOrWhiteSpace(mediaInfo.Url))
                        {
                            return;
                        }

                        if (Tracing)
                        {
                            _logger.LogDebug("{Name}: Firing playback progress event.", Name);
                        }

                        PlaybackProgress?.Invoke(this, new PlaybackEventArgs(mediaInfo));
                    }
                    else
                    {
                        if (Tracing)
                        {
                            _logger.LogDebug("{Name}: Firing media change event.", Name);
                        }

                        MediaChanged?.Invoke(this, new MediaChangedEventArgs(previousMediaInfo, mediaInfo));
                    }
                }
                else if (previousMediaInfo != null)
                {
                    if (Tracing)
                    {
                        _logger.LogDebug("{Name}: Firing playback stopped event.", Name);
                    }

                    PlaybackStopped?.Invoke(this, new PlaybackEventArgs(previousMediaInfo));

                    _mediaType = null;
                }
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                _logger.LogError(ex, "{Name}: UpdateMediaInfo erred.", Name);
            }
        }

        private record ValueRange
        {
            public double Range { get; set; } = 100;

            public int Min { get; set; }

            public int Max { get; set; } = 100;

            public int Step => (int)Math.Round(Range / 100 * 5);

            public int GetValue(int value)
            {
                return (int)Math.Round(Range / 100 * value);
            }
        }

        private class MediaData
        {
            public MediaData(string uri, string headers, string metadata, DlnaProfileType mediaType, bool resetPlayBack, long position)
            {
                Url = uri;
                Headers = headers;
                MediaType = mediaType;
                ResetPlayback = resetPlayBack;
                Metadata = metadata;
                Position = position == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(position);
            }

            /// <summary>
            /// Gets a value indicating whether the playback position should be changed.
            /// </summary>
            public bool ResetPlayback { get; }

            /// <summary>
            /// Gets a value indicating the url that should be loaded.
            /// </summary>
            public string Url { get; }

            /// <summary>
            /// Gets the metadata that is to be sent to the device.
            /// </summary>
            public string Metadata { get; }

            /// <summary>
            /// Gets the additional headers that should be sent to the device.
            /// </summary>
            public string Headers { get; }

            /// <summary>
            /// Gets the media type of the url.
            /// </summary>
            public DlnaProfileType MediaType { get; }

            /// <summary>
            /// Gets the position of the playback.
            /// </summary>
            public TimeSpan Position { get; }
        }
    }
}
