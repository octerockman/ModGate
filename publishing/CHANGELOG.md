# Changelog

## 0.4.7

- Added Support Mod Creators in the sidebar, with Active profile only by default and an All profiles filter.
- List physically installed profile mods, including retained disabled mods, without removed-mod history or restore points. Combine matching package versions and pin ModGate first.
- Outline stars open known mod-page links. They do not submit or verify votes, connect accounts or save endorsement states.
- Use bounded local package PNG icons where available, with an initials fallback. Original files are not modified.
- Retain the 0.4.6 Linux admin-key fix and independent server config discovery.

## 0.4.6

- Fixed private admin-key creation on Linux/Unity Mono by writing and flushing through the original native file descriptor instead of wrapping it in a managed FileStream.
- Keep genuine creation/write failures visible instead of replacing their cause with an invalid-key message.
- Admin-key errors no longer stop normal server mod discovery and config refresh. Server config and health access still require a valid owner key and verified administrator.
- Keep existing invalid keys for owner review. After upgrading, stop the server and rename the invalid key to a backup once so a new private key can be generated.

## 0.4.5

- Added ModGate updates from the official GitHub releases in Settings: check, download for review, then install after closing or install and restart on Windows.
- Server version mismatches offer the matching official connector release before preparing a profile.
- Both DLLs are verified against the release asset SHA-256 and their assembly identities and versions. Installation waits for normal shutdown, retains backups, and restores the previous pair when recovery is possible.
- Added separately controlled server self-updating, off by default. It stages during startup and applies on the next normal shutdown without forcing a running world to stop.
- Existing profiles, configs and BepInEx are kept. Install this build manually once to enable future self-updates.

## 0.4.4

- Prepare the first public package with a mod-page description, player and server-owner guides, package metadata, and an icon.
- Keep ModGate installed when switching profiles from an author/package folder created by a mod manager.

## 0.4.3

- Add an administrator Server health tab with CPU and memory readings, connection latency/jitter, network traffic, player count, session uptime and update timing.
- Add manual refresh, optional two-second polling and bounded connection history.
- Keep unavailable measurements distinct from zero and discard stale responses after leaving or reconnecting.
- Support long-running authenticated refresh without filling the legacy request replay cache.

## 0.4.2

- Use Shift+F6 to open ModGate in the main menu or during play.
- Block gameplay and camera input while the panel is open.
- Expand config editing directly below the selected file and add Reload configs.
- Offer dependency downloads as explicit suggestions while retaining server-required mod rules.

## 0.4.0–0.4.1

- Add the sidebar interface, clearer profile cards and preparation progress.
- Add profile checks and repair, config editing, restore points, separate mod details, and support reports.
- Add authenticated server config access for administrators.

Earlier development builds established server mod comparison, reviewed downloads, four server profiles plus Offline, per-profile configs, optional modules, recovery and opt-in server updating.
