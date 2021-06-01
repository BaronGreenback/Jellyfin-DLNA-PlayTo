DLNA PlayTo plugin for Jellyfin 10.8.0.0

- DLNA logging between server and client.
- Ability to handle non-standard and invalid xml device responses.
- SSDP Packet Tracing
- Restrict UDP ports to a specific range
- Auto create simple dlna profiles from device detection.
- Basic Photo slideshow implementation.
- Discovery broadcasts become less frequent once devices are located.
- Much faster detection of devices.
- Real time setting changes.
- If DLNA server plugin is loaded, any client details are shared to help aid in better playback.

**Settings**

**EnablePlayToDebug**

Enables dlna play to debugging.

**ClientDiscoveryInitialInterval**

Sets the initial ssdp client discovery interval time (in seconds).
Once a device has been detected, this discovery interval will drop to **ClientNotificationInterval** seconds.
        
**ClientDiscoveryInterval**

Sets the continuous ssdp client discovery interval time (in seconds).

**CommunicationTimeout**

Sets the amount of time given for the device to respond in ms.

**UserAgent**

Sets the USERAGENT that is sent to devices.

**DevicePollingInterval**

Sets the frequency of the device polling (ms).

**QueueProcessingInterval**

Sets the command queue processing frequency.

**FriendlyName**

Sets the friendly name that is used.

**UseNetworkDiscovery**

Enables/disables network discovery. Used with **StaticDevices**.
        
**StaticDevices**

Lists the static devices to be instantiated on the system.

**PhotoTransitionalTimeout**

The amount of time each photo will appear on screen before the next one is auto-loaded.
