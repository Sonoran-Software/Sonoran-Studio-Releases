SONORAN STUDIO FIVEM RESOURCE

Requirements
- Sonoran Studio desktop companion
- Sonoran Studio Pro or Sonoran One

Install
1. Extract the sonoran_studio folder into your server's resources directory.
2. Add "ensure sonoran_studio" to server.cfg.
3. Restart the server, join it, and keep Sonoran Studio open on your computer.

Do not run this resource alongside SonoranCAD. The SonoranCAD resource already
contains the same Studio bridge and running both will send duplicate events.

The resource synchronizes emergency lights and indicators, and exposes these
Streamer.bot triggers in Sonoran Studio:
- Weapon drawn and holstered
- Player died and revived (dead to alive; framework-independent)
- On foot, ground vehicle, aircraft, and watercraft
- Health lower-than and higher-than limits configured in Sonoran Studio

The resource is client-only and framework-independent. It only connects to
Sonoran Studio on the same computer at 127.0.0.1:9990. Use /setstudioport in the
FiveM client console if the desktop companion is configured for a different port.
The health limits default to 35% and 50% and can be changed beside the Health
threshold scene or in Streamer.bot. The same limits control both integrations.
