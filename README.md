DLNA PlayTo plugin for Jellyfin 10.8.0.0

- DLNA logging between server and client.
- Ability to handle non-standard and corrupt xml device responses.
- SSDP Packet Tracing
- Restrict UDP ports to a specific range
- Auto create simple dlna profiles from device detection.

**Settings**

**EnablePlayToDebug**

Enables dlna play to debugging.

**ClientDiscoveryIntervalSeconds**

Sets the initial ssdp client discovery interval time (in seconds).
Once a device has been detected, this discovery interval will drop to **ClientNotificationInterval** seconds.
        
**ClientNotificationInterval**

Sets the continuous ssdp client discovery interval time (in seconds).

**CommunicationTimeout**

Sets the amount of time given for the device to respond in ms.

**UserAgent**

Sets the USERAGENT that is sent to devices.

**TimerInterval**

Sets the frequency of the device polling (ms).

**QueueInterval**

Sets the command queue processing frequency.

**FriendlyName**

Sets the friendly name that is used.

**UseNetworkDiscovery**

Enables/disables network discovery. Used with **StaticDevices**.
        
**StaticDevices**

Lists the static devices to be instantiated on the system.
