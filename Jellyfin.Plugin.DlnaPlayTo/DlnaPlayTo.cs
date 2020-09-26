using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Dlna;
using Jellyfin.Api.Helpers;
using Jellyfin.Networking.Configuration;
using Jellyfin.Plugin.DlnaPlayTo.Configuration;
using Jellyfin.Plugin.DlnaPlayTo.Main;
using Jellyfin.Plugin.DlnaPlayTo.Profile;
using Jellyfin.Plugin.Ssdp;
using Jellyfin.Plugin.Ssdp.EventArgs;
using Jellyfin.Plugin.Ssdp.Helpers;
using Jellyfin.Plugin.Ssdp.Profiles;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Notifications;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DlnaPlayTo
{
    /// <summary>
    /// Defines the <see cref="DlnaPlayTo" />.
    /// </summary>
    public class DlnaPlayTo : BasePlugin<PlayToConfiguration>, IHasWebPages, IDisposable
    {
        private readonly ProfileManager _profileManager;
        private readonly ILogger _logger;
        private readonly ISessionManager _sessionManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IServerApplicationHost _appHost;
        private readonly IImageProcessor _imageProcessor;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ILocalizationManager _localization;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly INotificationManager _notificationManager;
        private readonly SemaphoreSlim _sessionLock = new(1, 1);
        private readonly CancellationTokenSource _disposeCancellationTokenSource = new();
        private readonly List<PlayToDevice> _devices = new();
        private readonly SsdpLocator _locator;
        private bool _disposed;
        private string? _previousSettings;

        /// <summary>
        /// Initializes a new instance of the <see cref="DlnaPlayTo"/> class.
        /// </summary>
        /// <param name="applicationPaths">The <see cref="IApplicationPaths"/>.</param>
        /// <param name="xmlSerializer">The <see cref="IXmlSerializer"/>.</param>
        /// <param name="loggerFactory">The <see cref="ILoggerFactory"/>.</param>
        /// <param name="sessionManager">The <see cref="ISessionManager"/>.</param>
        /// <param name="libraryManager">The <see cref="ILibraryManager"/>.</param>
        /// <param name="userManager">The <see cref="IUserManager"/>.</param>
        /// <param name="appHost">The <see cref="IServerApplicationHost"/>.</param>
        /// <param name="imageProcessor">The <see cref="IImageProcessor"/>.</param>
        /// <param name="httpClientFactory">The <see cref="IHttpClientFactory"/>.</param>
        /// <param name="configurationManager">The <see cref="IServerConfigurationManager"/>.</param>
        /// <param name="userDataManager">The <see cref="IUserDataManager"/>.</param>
        /// <param name="localization">The <see cref="ILocalizationManager"/>.</param>
        /// <param name="mediaSourceManager">The <see cref="IMediaSourceManager"/>.</param>
        /// <param name="mediaEncoder">The <see cref="IMediaEncoder"/>.</param>
        /// <param name="notificationManager">The <see cref="INotificationManager"/>.</param>
        /// <param name="networkManager">The <see cref="INetworkManager"/>.</param>
        /// <param name="dlnaProfileManager">The <see cref="IDlnaProfileManager"/>.</param>
        public DlnaPlayTo(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILoggerFactory loggerFactory,
            ISessionManager sessionManager,
            ILibraryManager libraryManager,
            IUserManager userManager,
            IServerApplicationHost appHost,
            IImageProcessor imageProcessor,
            IHttpClientFactory httpClientFactory,
            IServerConfigurationManager configurationManager,
            IUserDataManager userDataManager,
            ILocalizationManager localization,
            IMediaSourceManager mediaSourceManager,
            IMediaEncoder mediaEncoder,
            INotificationManager notificationManager,
            INetworkManager networkManager,
            IDlnaProfileManager dlnaProfileManager)
            : base(applicationPaths, xmlSerializer)
        {
            StreamingHelpers.ApplyDeviceProfileSettingsFunc = DlnaStreamHelper.ApplyDeviceProfileSettings;
            StreamingHelpers.AddDlnaHeadersFunc = DlnaStreamHelper.AddDlnaHeaders;

            Instance = this;
            if (networkManager == null)
            {
                throw new NullReferenceException(nameof(networkManager));
            }

            _profileManager = new ProfileManager(loggerFactory.CreateLogger<ProfileManager>(), dlnaProfileManager);
            _logger = loggerFactory.CreateLogger<DlnaPlayTo>();
            _sessionManager = sessionManager;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _appHost = appHost;
            _imageProcessor = imageProcessor;
            _httpClientFactory = httpClientFactory;
            _configurationManager = configurationManager;
            _userDataManager = userDataManager;
            _localization = localization;
            _mediaSourceManager = mediaSourceManager;
            _mediaEncoder = mediaEncoder;
            _notificationManager = notificationManager;

            _logger.LogDebug("DLNA PlayTo: Starting Device Discovery.");
            _locator = new SsdpLocator(
                _configurationManager,
                _logger,
                networkManager.GetInternalBindAddresses(),
                networkManager);

            _locator.DeviceDiscovered += OnDeviceDiscoveryDeviceDiscovered;

            Configuration.SetConfigurationManager(configurationManager);
            ConfigurationChanged = UpdateConfiguration;

            _locator.Start();

            var httpsOnly = _appHost.ListenWithHttps && configurationManager.GetConfiguration<NetworkConfiguration>("network").RequireHttps;
            if (httpsOnly)
            {
                _logger.LogError("DLNA PlayTo will not operate correctly, as HTTP needs to be enabled.");
            }
        }

        /// <summary>
        /// An event handler that is triggered on receipt of a PlayTo client subscription event.
        /// </summary>
        public event EventHandler<DlnaEventArgs>? DlnaEvents;

        /// <summary>
        /// Gets or sets the Instance.
        /// </summary>
        public static DlnaPlayTo? Instance { get; set; }

        /// <summary>
        /// Gets the Plugin Name.
        /// </summary>
        public override string Name => "DLNA PlayTo";

        /// <summary>
        /// Gets the Id.
        /// </summary>
        public override Guid Id => Guid.Parse("06955396-4e1e-4984-965c-061342f842e3");

        /// <summary>
        /// The GetPages.
        /// </summary>
        /// <returns>The <see cref="IEnumerable{PluginPageInfo}"/>.</returns>
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = this.Name,
                    EnableInMainMenu = true,
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
                }
            };
        }

        /// <summary>
        /// Method that triggers a DLNAEvents event.
        /// </summary>
        /// <param name="args">A DlnaEventArgs instance containing the event message.</param>
        /// <returns>An awaitable <see cref="Task"/>.</returns>
        public Task NotifyDevice(DlnaEventArgs args)
        {
            DlnaEvents?.Invoke(this, args);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (!_disposed)
            {
                _ = Dispose(true);
            }

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Sends a client notification message.
        /// </summary>
        /// <param name="notification">The notification to send.</param>
        /// <returns>Task.</returns>
        public async Task SendNotification(NotificationRequest notification)
        {
            await _notificationManager.SendNotification(notification, null, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Override this method and dispose any objects you own the lifetime of if disposing is true.
        /// </summary>
        /// <param name="disposing">True if managed objects should be disposed, if false, only unmanaged resources should be released.</param>
        /// <returns>The <see cref="Task"/>.</returns>
        private async Task Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _logger.LogDebug("Disposing instance.");

                    _locator.DeviceDiscovered -= OnDeviceDiscoveryDeviceDiscovered;
                    _locator.Dispose();

                    var cancellationToken = _disposeCancellationTokenSource.Token;
                    await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        // Stop any active sessions and dispose of the PlayToControllers created.
                        _sessionManager.Sessions.ToList().ForEach(s =>
                        {
                            // s.StopController<PlayToController>();
                            _sessionManager.ReportSessionEnded(s.Id);
                        });
                    }
                    catch (OperationCanceledException)
                    {
                    }
#pragma warning disable CA1031 // Do not catch general exception types
                    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
                    {
                        _logger.LogError(ex, "Error disposing of PlayToControllers.");
                    }
                    finally
                    {
                        _sessionLock.Release();
                    }

                    try
                    {
                        _disposeCancellationTokenSource.Cancel();
                    }
#pragma warning disable CA1031 // Do not catch general exception types
                    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
                    {
                        _logger.LogDebug(ex, "Error while disposing PlayToManager");
                    }

                    _sessionLock.Dispose();

                    _disposeCancellationTokenSource.Dispose();

                    // Dispose the PlayToDevices created in AddDevice.
                    foreach (var device in _devices)
                    {
                        device.Dispose();
                    }
                }

                _disposed = true;
            }
        }

        private void OnDeviceDiscoveryDeviceDiscovered(object? sender, DiscoveredSsdpDevice args)
        {
            _ = DeviceDiscoveryDeviceDiscovered(args);
        }

        private async Task DeviceDiscoveryDeviceDiscovered(DiscoveredSsdpDevice args)
        {
            if (_disposed)
            {
                return;
            }

            var cancellationToken = _disposeCancellationTokenSource.Token;
            await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (_disposed)
                {
                    return;
                }

                if (_sessionManager.Sessions.Any(i => args.Usn.Equals(i.DeviceId, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                await AddDevice(args).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                _logger.LogError(ex, "Error creating PlayTo device.");
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        private async Task AddDevice(DiscoveredSsdpDevice info)
        {
            var deviceProperties = await PlayToDevice.ParseDevice(info.Location, _httpClientFactory, _logger).ConfigureAwait(false);

            if (deviceProperties == null)
            {
                return;
            }

            var sessionInfo = _sessionManager.LogSessionActivity(
                "DLNA PlayTo",
                _appHost.ApplicationVersionString,
                info.Usn,
                deviceProperties.Name ?? info.Location.OriginalString,
                info.Endpoint.Address.ToString(),
                null);
            var controller = sessionInfo.SessionControllers.OfType<PlayToController>().FirstOrDefault();

            if (controller != null)
            {
                return;
            }

            string serverAddress = _appHost.GetSmartApiUrl(info.Endpoint.Address);

            var device = await PlayToDevice.CreateDevice(deviceProperties, _httpClientFactory, _logger, serverAddress, _profileManager).ConfigureAwait(false);
            _devices.Add(device);

#pragma warning disable CA2000 // Dispose objects before losing scope
            controller = new PlayToController(
                sessionInfo,
                _sessionManager,
                _libraryManager,
                _logger,
                _userManager,
                _imageProcessor,
                serverAddress,
                null,
                _locator,
                _userDataManager,
                _localization,
                _mediaSourceManager,
                _configurationManager,
                _mediaEncoder,
                device);
#pragma warning restore CA2000 // Dispose objects before losing scope

            sessionInfo.AddController(controller);

            _sessionManager.ReportCapabilities(sessionInfo.Id, new ClientCapabilities
            {
                PlayableMediaTypes = device.Profile.GetSupportedMediaTypes(),

                SupportedCommands = new[]
                {
                    GeneralCommandType.VolumeDown,
                    GeneralCommandType.VolumeUp,
                    GeneralCommandType.Mute,
                    GeneralCommandType.Unmute,
                    GeneralCommandType.ToggleMute,
                    GeneralCommandType.SetVolume,
                    GeneralCommandType.SetAudioStreamIndex,
                    GeneralCommandType.SetSubtitleStreamIndex,
                    GeneralCommandType.PlayMediaSource
                },

                SupportsMediaControl = true
            });

            _logger.LogInformation("DLNA Session created for {Name} - {Model}", device.Properties.Name, device.Properties.ModelName);
            _locator.SlowDown();
        }

        private void UpdateConfiguration(object? sender, BasePluginConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            var config = (PlayToConfiguration)configuration;
            PlayToDevice.CtsTimeout = config.CommunicationTimeout;
            PlayToDevice.FriendlyName = config.FriendlyName;
            PlayToDevice.QueueInterval = config.QueueInterval;
            PlayToDevice.TimerInterval = config.TimerInterval;
            PlayToDevice.UserAgent = config.UserAgent;
            if (_locator != null)
            {
                // Tracing may be set elsewhere, so we want to implement a last change wins.
                string settings = GetSettings();
                if (_previousSettings != settings)
                {
                    config.SsdpTracingFilter = IPAddress.TryParse(config.SsdpTracingFilter, out var addr) ? addr.ToString() : string.Empty;
                    _locator.Server.UdpPortRange = config.UdpPortRange;
                    _locator.Server.SetTracingFilter(config.EnableSsdpTracing, config.SsdpTracingFilter);
                    if (config.EnableSsdpTracing)
                    {
                        _logger.LogDebug("Setting SSDP tracing to : {Filter}", config.SsdpTracingFilter);
                    }
                    else
                    {
                        _logger.LogDebug("SSDP Logging disabled.");
                    }

                    _locator.Server.SaveConfiguration();
                    _previousSettings = settings;
                }

                if (config.ClientDiscoveryIntervalSeconds <= 0)
                {
                    config.ClientDiscoveryIntervalSeconds = 15;
                }

                if (config.ClientNotificationInterval <= 0)
                {
                    config.ClientNotificationInterval = 1800;
                }

                _locator.Interval = config.ClientNotificationInterval;
                _locator.InitialInterval = config.ClientDiscoveryIntervalSeconds;
            }
        }

        private string GetSettings()
        {
            return Configuration.EnableSsdpTracing.ToString()
                + Configuration.SsdpTracingFilter
                + Configuration.UdpPortRange
                + Configuration.EnableSsdpTracing.ToString();
        }
    }
}
