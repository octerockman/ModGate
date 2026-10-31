# ModGate server-owner guide

ModGate makes your modded server easier to join by doing most of the setup work for your players. It discovers your installed plugins, shares the requirements, and lets players download the required supported packages together with one button. ModGate checks, downloads and prepares their server profile; players review it, restart and reconnect.

You choose how your server works: which mods are required or optional, whether players may bring additional client-side mods, whether newer client versions are accepted, and which eligible server mods can update automatically at startup. Automatic updating starts off. ModGate maintains the detected list and looks up supported downloads; you review classifications and resolve missing mappings.

**Both the server and each player need ModGate.** The server supplies the requirements and checks the joining setup. The client displays the choices, downloads selected packages, and installs its local profile on restart. Players need the connector before it can show any prompt.

## Install and create the server setup

1. Ensure BepInEx is installed. Stop the server normally, then install both files:

   ```text
   BepInEx/plugins/ValheimModGate/ValheimModGate.dll
   BepInEx/patchers/ValheimModGate/ValheimModGate.Preloader.dll
   ```

2. Start the server once. ModGate scans loaded BepInEx plugins and creates its setup under `BepInEx/config/ModGate/`.
3. Stop the server and review `server-mods.json`. Confirm the server name, mod classifications, download mappings, and rules for extra or newer client mods. The included `server-mods.example.json` explains the shape; keep your generated real entries rather than replacing them with the examples.
4. Restart and check `lookup-report.json` for unresolved downloads. Background lookup may still be running after startup; allow it to finish and reconnect before testing a missing mapping again.
5. Give players ModGate **0.4.9** and test a join from a separate client. When upgrading ModGate, update both DLLs on the server and every client. Retain existing configs and profiles.

`server-mods.json` is the active owner-editable list. The older `server-mods.discovered.json` is unused. ModGate also creates `server-only.txt` for exclusions, `server-id.txt` for the server's identity, and `admin-access.key` for remote administrator access. Keep the server identity when upgrading so existing profiles continue to recognize it.

## Decide who needs each mod

Set each entry's `side` to one of these values:

| Value | Meaning |
| --- | --- |
| `required` | Players must have an accepted version. Use for mods needed on the server and clients. |
| `optional` | Players can choose whether to include the mod in this server profile. |
| `serverOnly` | The mod is for the server and is never offered to players. |
| `clientOnly` | Players must have it; ModGate prevents its ordinary plugin from loading on a dedicated server. |

Newly discovered names containing “Server Side” or “Serverside,” ignoring case and punctuation, default to `serverOnly`. Additional GUIDs or exact names can go in `server-only.txt`. Existing classifications are preserved, so edit an existing entry's `side` directly when changing its role. Review the results: names alone cannot reliably determine what a mod needs.

Keep client-only DLLs available on the server for discovery and updates. A manual client-only entry can also describe a mod absent from the server; supply its real GUID, plugin version and download mapping, and use `automatic: false` to retain the manual entry. Dedicated-server suppression applies to normal plugins, not preloader patchers or code loaded by another mod. Listen hosts keep their client plugins loaded.

## Supply reliable downloads

ModGate uses installed DLL metadata for the plugin GUID, display name and required plugin version. A package's version can differ from the DLL version; do not change the required DLL version just to match a manifest. Package manifests provide download and dependency hints.

Thunderstore and Hexium are the normal automatic lookup sources. For a mapping that needs correction, set a version-specific supported HTTPS link in `downloadUrl`, or provide the exact `package` mapping. Use `nexusUrl` for a Valheim Nexus page and leave `downloadUrl` empty for manual Nexus installs. Players follow that page themselves; ModGate does not download Nexus files automatically.

For a DLL placed directly in `BepInEx/plugins`, startup creates a per-plugin folder here:

```text
BepInEx/config/ModGate/manifests/<plugin-guid>/manifest.json
```

Copy the mod's original `manifest.json` into its generated folder. Its README identifies the correct DLL and GUID; some unusual GUIDs use a generated `guid-<hash>` folder. Do not use one shared plugins-folder manifest for unrelated DLLs. Restart after adding manifests, then allow lookup to finish. Existing valid package manifests take priority.

ModGate refreshes detected fields while retaining owner download links, classifications and update choices. An asterisk in a text field locks that field against refresh; runtime processing ignores the marker. It is not a version wildcard.

By default, players need the server's version. `allowNewerClientVersions: true` permits newer installed client versions; an entry's `allowNewerVersion` overrides that default. This is your compatibility policy, not a guarantee that another mod accepts the version. ModGate itself always needs its exact matching version.

Set `allowExtraClientMods: true` to let players keep additional client-side mods outside your server list. Set it to `false` to restrict them to the listed setup. This does not relax required-mod versions or server-only exclusions.

## Updates, dependencies and administration

**Server auto-update is off by default.** Enable `autoUpdateOnRestart` to permit it, then use `autoUpdateAllMods` or individual `autoUpdate` settings. Updates run at dedicated-server startup using supported Thunderstore/Hexium mappings. They do not update BepInEx, ModGate, preloader/loader packages or manual Nexus entries. Review `server-update-report.json` after enabling updates. The host needs working HTTPS access and the system curl helper.

Dependency suggestions are player choices unless you explicitly classify the mod as required or client-only. BepInEx can still reject a mod whose hard dependencies are missing. An override or replacement package cannot change your required DLL version or guarantee a successful join.

While connected, administrators can open **Shift+F6 → Server configs** to edit eligible mod `.cfg` files and ModGate's server settings. Remote access requires both a normal server admin-list entry and the owner-issued code from:

```text
BepInEx/config/ModGate/admin-access.key
```

Share that code privately with trusted administrators. Server saves queue for restart; **Reload configs** reloads eligible current `.cfg` files where the mod supports it. ModGate's server-policy JSON still requires a restart.

**Linux key recovery:** version 0.4.6 fixes key-file creation on Unity Mono. If an earlier build left an invalid or empty `admin-access.key`, stop the server, install both new DLLs, rename the invalid key to `admin-access.key.backup`, then start the server. A new private key is generated; administrators must enter that replacement key. Existing valid keys are retained. A key failure now disables admin config/health access while allowing normal mod-list discovery and config refresh to continue. Do not share the key in logs or support reports.

**Server health** uses the same administrator access. It shows server-process CPU and memory, connection round trip/jitter, traffic, players, session uptime and update timing, with optional two-second refresh. CPU 100% means one logical core. Unavailable readings are marked; GPU and temperature data are not collected.

## Updating ModGate itself

Install the 0.4.6 DLL pair manually once. Later builds can update from the official `octerockman/ModGate` GitHub releases. This is separate from updating ordinary server mods and stays **off by default**.

To opt in, set these top-level fields in `BepInEx/config/ModGate/server-mods.json`:

```json
"autoUpdateOnRestart": true,
"autoUpdateModGate": true,
"modGateIncludePrereleases": true
```

Keep `modGateIncludePrereleases` true for alpha releases, or set false for stable releases only. On startup, ModGate checks and stages a newer official version while the server keeps running. It applies the verified plugin/preloader pair after the **next normal shutdown**; it never forces a world to stop. Stop the server, wait for `BepInEx/ModGate/self-update-status.txt`, then start it. A hosting panel that immediately starts during replacement may be stopped by the preloader guard before loading the world; start it again after the helper finishes.

Windows uses its built-in PowerShell helper. Linux requires `/proc`, `/bin/sh`, `/usr/bin/flock` and `/usr/bin/sha256sum`. If your host blocks background helpers, update manually. Startup only queues for the next shutdown; it does not replace the server DLLs before that same startup.

Clients can find the server's exact official ModGate version through the mismatch prompt and Settings. Publish the runtime ZIP to the official GitHub release before using that version on a public server. An unpublished version cannot be downloaded automatically. Network failures leave the installed version in place.

Only the two ModGate DLLs are replaced. BepInEx, other mods, configs and profiles are kept. Release ZIP SHA-256 and assembly identities/versions are checked; no arbitrary server-supplied update URL is used. These checks establish consistency with the official GitHub asset, not a separate code signature.

On failure, retain `BepInEx/ModGate/self-updates/` and read `self-update-error.txt`. With the server fully stopped, restore both `backup-*.dll` files from the same update folder to the original plugin/patcher locations, or manually reinstall the complete DLL pair. Do not restore just one component. Duplicate or linked connector installations need manual attention; the updater does not remove duplicates.

For setup failures, check `lookup-report.json`, `server-update-report.json`, the BepInEx log, and the player's Activity/setup log. Use [the ModGate support Discord](https://discord.gg/AaVzBfPcYH) for bug reports. Support ZIP creation does not send anything automatically; review a report before sharing it.

