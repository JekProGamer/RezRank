# RezRank custom TAB badges

- RezRank rank 1–18 maps to custom skillgroup IDs 19–36.
- Silver I uses `skillgroup19.svg`; The Global Elite uses `skillgroup36.svg`.
- Client resource path: `panorama/images/icons/skillgroups/`.

## Installation

1. Package this folder as a Counter-Strike 2 Workshop addon with CS2 Workshop Tools.
2. Publish it privately or share it with intended players.
3. Every player who should see the custom icons must install the addon and restart CS2.
4. Install the updated RezRank plugin DLL on the server and restart it.

The server plugin assigns the custom skillgroup ID; the client addon supplies the SVG. Both are required. Copying SVGs to the dedicated server alone does not install them on clients. This folder is source layout, not a compiled VPK.
