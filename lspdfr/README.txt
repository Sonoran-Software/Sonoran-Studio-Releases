SONORAN STUDIO LSPDFR INTEGRATION

Requirements
- LSPDFR and RAGE Plugin Hook
- Sonoran Studio for Windows
- Sonoran Studio Pro or Sonoran One

Install
1. Close GTA V and RAGE Plugin Hook.
2. Copy Plugins\SonoranStudio.LSPDFR.dll into your GTA V folder.
3. Open RAGE Plugin Hook settings and enable Load all plugins on startup.
4. Start RAGE Plugin Hook, launch LSPDFR, and keep Sonoran Studio open.
5. In Sonoran Studio, open Lighting and select LSPDFR.

The plugin synchronizes:
- Emergency lights, left/right indicators, and hazards
- Weapon draw and holster moments
- Player death and revival moments
- On-foot, vehicle, aircraft, and watercraft travel changes
- Health lower-than and higher-than limits configured in Sonoran Studio
- Officer persona, agency, derived duty status, and street/area
- Displayed and accepted callout names, messages, advisories, and locations

LSPDFR does not natively expose a callsign/unit number, postal, call priority/code,
Radio transmission, or panic data. Callout packs may omit optional call metadata.
The player-revived moment covers both respawns and revives because GTA natives do
not distinguish how the player came back.

The plugin only connects to Sonoran Studio on this computer at 127.0.0.1:9990.
The signed-in Studio app forwards validated overlay events through your account.
The health limits default to 35% and 50% and can be changed beside the Health
threshold scene or in Streamer.bot. The same limits control both integrations.

Documentation:
https://docs.sonoransoftware.com/studio/sonoran-studio/smart-lighting#lspdfr
