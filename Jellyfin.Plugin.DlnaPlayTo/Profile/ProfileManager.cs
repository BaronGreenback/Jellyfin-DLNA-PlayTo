using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.DlnaPlayTo.Model;
using Jellyfin.Plugin.Ssdp.Profiles;
using MediaBrowser.Model.Dlna;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DlnaPlayTo.Profile
{
    /// <summary>
    /// Defines the <see cref="ProfileManager" />.
    /// </summary>
    internal class ProfileManager
    {
        private readonly ILogger<ProfileManager> _logger;
        private readonly IDlnaProfileManager _dlnaProfileManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProfileManager"/> class.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger{ProfileManager}"/>.</param>
        /// <param name="dlnaProfileManager">The <see cref="IDlnaProfileManager"/>.</param>
        public ProfileManager(
            ILogger<ProfileManager> logger,
            IDlnaProfileManager dlnaProfileManager)
        {
            _logger = logger;
            _dlnaProfileManager = dlnaProfileManager;
        }

        /// <summary>
        /// Gets the Default Profile.
        /// </summary>
        /// <param name="playToDeviceInfo">The <see cref="PlayToDeviceInfo"/>.</param>
        /// <returns>The <see cref="DeviceProfile"/>.</returns>
        public DeviceProfile GetDefaultProfile(PlayToDeviceInfo playToDeviceInfo)
        {
            if (playToDeviceInfo == null)
            {
                throw new ArgumentNullException(nameof(playToDeviceInfo));
            }

            return DlnaPlayTo.Instance!.Configuration.AutoCreatePlayToProfiles
                ? AutoCreateProfile(playToDeviceInfo)
                : new DeviceProfile()
                {
                    ProtocolInfo = playToDeviceInfo.Capabilities
                };
        }

        /// <summary>
        /// Gets the profile.
        /// </summary>
        /// <param name="deviceId">The device information.</param>
        /// <returns>DeviceProfile.</returns>
        public DeviceProfile GetProfile(PlayToDeviceInfo deviceId)
        {
            var deviceInfo = deviceId.ToDeviceIdentification();

            var profile = _dlnaProfileManager.GetProfiles()
                .FirstOrDefault(i => i.Identification != null && IsMatch(deviceInfo, i.Identification));

            if (profile != null)
            {
                _logger.LogDebug("Found matching device profile: {Name}", profile.Name);
            }
            else
            {
                _logger.LogDebug("No profile found. Using the default as a template.");
                profile = GetDefaultProfile(deviceId);
            }

            return profile;
        }

        /// <summary>
        /// Auto create a profile for <paramref name="deviceInfo"/>.
        /// </summary>
        /// <param name="deviceInfo">The <see cref="PlayToDeviceInfo"/>.</param>
        /// <returns>The <see cref="DeviceProfile"/>.</returns>
        private DeviceProfile AutoCreateProfile(PlayToDeviceInfo deviceInfo)
        {
            DeviceProfile profile = _dlnaProfileManager.GetDefaultProfile();

            profile.Name = deviceInfo.Name;
            profile.Identification = new DeviceIdentification
            {
                FriendlyName = deviceInfo.Name,
                Manufacturer = deviceInfo.Manufacturer ?? string.Empty,
                ModelName = deviceInfo.ModelName ?? string.Empty,
                ManufacturerUrl = deviceInfo.ManufacturerUrl ?? string.Empty,
                ModelDescription = deviceInfo.ModelDescription ?? string.Empty,
                ModelNumber = deviceInfo.ModelNumber ?? string.Empty,
                ModelUrl = deviceInfo.ModelUrl ?? string.Empty,
                SerialNumber = deviceInfo.SerialNumber ?? string.Empty
            };

            profile.Manufacturer = deviceInfo.Manufacturer ?? string.Empty;
            profile.FriendlyName = deviceInfo.Name;
            profile.ModelNumber = deviceInfo.ModelNumber ?? string.Empty;
            profile.ModelName = deviceInfo.ModelName ?? string.Empty;
            profile.ModelUrl = deviceInfo.ModelUrl ?? string.Empty;
            profile.ModelDescription = deviceInfo.ModelDescription ?? string.Empty;
            profile.ManufacturerUrl = deviceInfo.ManufacturerUrl ?? string.Empty;
            profile.SerialNumber = deviceInfo.SerialNumber ?? string.Empty;
            profile.ProtocolInfo = deviceInfo.Capabilities ?? profile.ProtocolInfo;
            try
            {
                _dlnaProfileManager.CreateProfile(profile);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                _logger.LogError(ex, "Error saving default profile for {Name}.", deviceInfo.Name);
            }

            return profile;
        }

        private bool IsMatch(DeviceIdentification deviceInfo, DeviceIdentification profileInfo)
        {
            if (!IsRegexOrSubstringMatch(deviceInfo.FriendlyName, profileInfo.FriendlyName))
            {
                return false;
            }

            if (!IsRegexOrSubstringMatch(deviceInfo.Manufacturer, profileInfo.Manufacturer))
            {
                return false;
            }

            if (!IsRegexOrSubstringMatch(deviceInfo.ManufacturerUrl, profileInfo.ManufacturerUrl))
            {
                return false;
            }

            if (!IsRegexOrSubstringMatch(deviceInfo.ModelDescription, profileInfo.ModelDescription))
            {
                return false;
            }

            if (!IsRegexOrSubstringMatch(deviceInfo.ModelName, profileInfo.ModelName))
            {
                return false;
            }

            if (!IsRegexOrSubstringMatch(deviceInfo.ModelNumber, profileInfo.ModelNumber))
            {
                return false;
            }

            if (!IsRegexOrSubstringMatch(deviceInfo.ModelUrl, profileInfo.ModelUrl))
            {
                return false;
            }

            if (!IsRegexOrSubstringMatch(deviceInfo.SerialNumber, profileInfo.SerialNumber))
            {
                return false;
            }

            return true;
        }

        private bool IsRegexOrSubstringMatch(string? input, string? pattern)
        {
            if (input == null || pattern == null)
            {
                return false;
            }

            try
            {
                return input.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                    || Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                _logger.LogError(ex, "Error evaluating regex pattern {Pattern}", pattern);
                return false;
            }
        }
    }
}
