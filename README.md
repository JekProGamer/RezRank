# RezRank

Standalone CounterStrikeSharp plugin for custom CS2 ranks, XP progression and player statistics.

## Current foundation

- .NET 10 and CounterStrikeSharp.API 1.0.369.
- MySQL storage keyed by SteamID (never by nickname).
- Player table is created automatically on plugin initialization.
- Player name is stored as display information and can change without losing progression.
- Last activity is stored in UTC.
- Inactivity cleanup is configured for 96 hours, with a daily check interval.
- MySQL credentials are configured locally in `config.json`; do not commit real passwords.

## Configuration

The repository contains a safe example `config.json`. Change the database credentials on the server before enabling the plugin.

`DatabaseCleanup.InactiveDays` controls the inactivity threshold. `CheckIntervalHours` controls how often the scheduled cleanup should run. Bypass exclusions will be applied when the Bypass player configuration is implemented.

## Development status

This is the initial project foundation. Player event integration, scheduled cleanup execution, XP/rank progression, Bypass handling, commands and in-game rank display are still being implemented and tested.
