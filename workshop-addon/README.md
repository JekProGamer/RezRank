# RezRank custom TAB badges

This addon contains the 18 custom SVG skillgroup icons used by RezRank.

## Resource mapping

The plugin assigns Counter-Strike 2 matchmaking skillgroup type 11 and the following custom icon IDs:

- RezRank 1–18 map to skillgroup IDs 19–36.
- `skillgroup19.svg` is Silver I.
- `skillgroup36.svg` is The Global Elite.

The icons are stored under:
`panorama/images/icons/skillgroups/`

## Installation

1. Build this folder as a Counter-Strike 2 Workshop addon using the Counter-Strike 2 Workshop Tools.
2. Publish the addon privately or make it available to the intended players.
3. Have every player who needs to see the custom icons install/subscribe to the addon and restart CS2.
4. Install the updated RezRank DLL on the game server and restart the server.

The server plugin assigns the custom skillgroup IDs; the client addon supplies the matching SVG files. Both parts are required. Merely copying the SVG files onto the dedicated server does not install them on players' clients.

## Important

This directory is the source layout for the client Workshop addon. It is not a ready-to-upload VPK. Package/publish it with the CS2 Workshop Tools before distributing it to players.
