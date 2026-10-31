# Valheim ModGate 0.4.9

## Official connector updates

Settings checks `octerockman/ModGate` GitHub releases through the native HTTPS transport. It requires an official runtime asset with a GitHub SHA-256 digest and matching plugin/preloader assembly identities and versions. It stages the pair under `BepInEx/ModGate/self-updates/`, retains backups, waits for normal process exit, and uses verified replacement with rollback on recoverable failure. The preloader stops a premature launch before loading the world when a queued pair is incomplete. This is not an independent code signature.

Client preferences are `BepInEx/ModGate/self-update-preferences.json`; checks are enabled at most every 12 hours. Installation requires a user action. Early releases default on in this alpha. Server flags `autoUpdateModGate` (default false) and `modGateIncludePrereleases` control the separate connector updater; `autoUpdateOnRestart` must also be true. It checks/stages during startup and applies on the next shutdown. Windows supports install and relaunch; Linux requires sh, /proc, flock and sha256sum, then a manual or panel restart after completion. See the player and server-owner guides for recovery and host timing.

ModGate compares a joining player's loaded mods with the server's current requirements. It prepares four server profiles plus an offline profile, with separate mod configs.

## Install or upgrade

Stop Valheim and the server, then install both included files on the server and every player:

    BepInEx/plugins/ValheimModGate/ValheimModGate.dll
    BepInEx/patchers/ValheimModGate/ValheimModGate.Preloader.dll

Keep existing configs and profiles. Install both 0.4.9 DLLs on the server and every client (network protocol 6). Older saved profiles remain usable. BepInEx must already be installed; ModGate never installs or updates it. Start through the same installation or mod-manager profile as before.

This release prepares the public package and protects ModGate's own author/package folder during profile changes. It retains **Server health** for administrators, **Shift+F6**, gameplay-input blocking, collapsible config files, **Reload configs**, explicit dependency choices, the sidebar and separate Mod details tab, profile checks and targeted repairs, restore points, and support reports. Existing clientOnly, newer-version allowances, server manifest folders, password remembering and isolated server-update transport are retained.

## Client-only mods and newer client versions

Set a mod entry's **side** to **clientOnly** to require it on players while preventing BepInEx from creating its plugin instance on a dedicated server. Keep the mod files under the server's plugins folder: ModGate reads their DLL metadata without running the plugin, advertises them to clients and can update them normally. This affects normal plugins, not preloader patchers or arbitrary code loaded by another mod. Listen-host players keep their client mods loaded.

The filter is applied after BepInEx's assembly patches and before plugin loading, including cached discovery results. Restart the server after changing side; already-running plugins cannot be unloaded. Changing side back restores normal discovery on the following restart. Files and configs are retained. A server mod that hard-depends on a suppressed client-only plugin can fail its normal BepInEx dependency check; classify that dependency correctly.

Client-only entries are retained even when automatic discovery cannot find an installed DLL. For a manually declared mod absent from the server, supply its real GUID, plugin version and download mapping. Installed client-only DLLs refresh detected metadata while preserving side and owner settings.

To accept a player's newer installed version, set this top-level server setting:

    "allowNewerClientVersions": true

The default is false. Individual mod entries can override it using **allowNewerVersion**: true permits newer versions, false requires the exact version, and null follows the server-wide setting. Older versions remain rejected. ModGate itself always requires its exact version and matching protocol.

Accepted newer mods display green with **Newer accepted** and are copied into the profile with their actual DLL versions instead of downloading the older server version. Their actual available package manifests are used for dependency resolution. Server requirements are never rewritten to pretend the DLL is an older version. Acceptance is a server policy choice; other mods can still enforce their own version or compatibility checks.

## Navigating ModGate

Press **Shift+F6** with either Shift key to open or close ModGate from the main menu or while playing. The home screen also has **Mod profiles (Shift+F6)**. Unsaved edits still require Save, Discard or Keep editing before closing. While the window is open, ModGate blocks gameplay buttons, movement, attacks and camera input, and cancels autorun and held block.

The sidebar provides Profiles, Settings and quick access to saved profiles. Offline has a full-width card above the four server slots. Server cards use two columns when space allows and stack on smaller windows; empty slots have an outlined card. Saved cards show mod counts and Active/Ready/Needs update/Incomplete badges. Status is based on the last known requirements; the live server is checked on every join.

- **Open profile** opens the mod editor.
- **Activate & restart** appears on every saved card, including Offline and the already-active profile. It schedules that setup for the next launch and relaunches Valheim on Windows.
- **More** contains server-profile deletion, with a confirmation popup.
- **Settings** contains source priority, the password-keeper master toggle, download cleanup, config sync and remove-all.
- **Check profile** checks the selected setup; **Restore…** opens its saved restore points.

A profile has **Mods**, **Configs**, **Mod details**, and **Activity** tabs; **Server health** is available while connected. Mods supports searching and filtering, enabled/disabled bundles, and adding permitted installed mods. Plugins sharing a folder or package switch together with their assets. Disabled files and their configs are retained for re-enabling. Use Mod details to browse package information with its own search. Changes are reviewed before applying on restart. ModGate itself stays enabled.

The join screen shows missing mods, version changes and available optional modules. Optional mods start expanded and can be collapsed. Matching mods are collapsed by default; search and filters make them accessible. Rows show the mod's name and source, installed/server versions, and a status badge; smaller screens stack these fields. Green means installed and matching, red means required and missing, and yellow means a version change or an absent optional mod. Equivalent trailing-zero versions match. Newer versions match only when the server explicitly allows them; compatibility is not otherwise guessed.

Setup separates choosing mods, preparing requirements and dependencies, downloading, review, installation and restart. The progress view identifies the current operation and package; downloads show transferred bytes and prepared package counts. Back-navigation preserves selections. Activity contains progress, errors, dependency explanations, a Copy error button, and expandable technical details. **Open setup log** and exit controls remain outside the scrolling content.

Actual profile installation happens in the preloader after the old game closes and before plugins load. The in-game window shows preparation and the scheduled restart; there is no live ModGate progress window while Valheim is closed. Startup and restart logs record that part of the operation. Valheim does not reconnect automatically after restarting.

## Optional feature modules

ModGate reads BepInEx hard/soft dependency declarations from the DLL. A mod such as TeleportEverything can show selectable modules including QuickTeleport, TeleportBoats, TeleportCarts and TeleportCreatures. DLL declarations are recommendations for selection: a module marked **optional** remains a player choice even when another DLL declares a hard dependency on it.

Entries explicitly marked **required** or **clientOnly** by the server remain checked and locked. Selections are saved per server profile and restored on reconnect. Missing or outdated declared DLL dependencies produce warnings; setup can continue, but BepInEx may refuse to load a mod without its declared hard dependencies. Version requirements, server-only exclusions and restrictions on extra mods still apply.

New modules referenced by hard or soft DLL dependencies default to optional during server discovery. Existing **side** choices and field locks are preserved. If a server already lists TeleportEverything's modules as required, its owner must change those entries to **optional** to permit player choice. Existing required or server-only classifications are not changed automatically.

The expanded **Suggested dependencies** section offers additional package downloads when their metadata is available. These start unchecked. Preparation can discover more suggestions; check the ones you want, then prepare the profile again. Changing a download choice discards the old prepared review. Suggestions cannot bypass a server's ban on extra mods or server-only packages.

## Password remembering

On Windows, the password keeper is **enabled by default**. The normal Valheim password dialog includes **Remember password for this server**. A password entered there is saved only after a successful connection. Future joins to the same ModGate server identity and connection host can enter it automatically.

Passwords use Windows current-account encryption and stay under:

    BepInEx/ModGate/private-passwords/

They are separate from profile mods/configs, server-mods.json, exports and logs. There is no plain-text fallback. A rejected saved password is forgotten; reconnect and enter the new password normally. A temporary decryption failure falls back to manual entry.

Open a server profile's **Configs** tab for **Change password**, **Forget password**, and its remember toggle. **Settings** contains the master switch; turning it off removes saved entries. Preference flags contain no passwords. Secure persistence is available on Windows; other platforms use normal password entry. Remembering requires a valid ModGate server identity.

## Per-profile configs

The **Configs** tab includes an editor for profile-associated BepInEx **.cfg** files. Files start collapsed. Select a filename to expand its settings directly below that filename; select it again to collapse. Search settings in the open file, read their descriptions, change booleans or choices, edit text/numeric values, and reset individual settings when a default is declared. Type, choice and numeric-range metadata is validated before saving. Private password/token-like settings are not exposed as editable values. Global BepInEx and ModGate configs are excluded.

**Reload configs** refreshes the file list and collapses the open file. For the active profile, it also asks eligible running mods' bound BepInEx config files to reload from disk. For an inactive profile, it refreshes the saved files only. Unsaved edits are protected by the same Save/Discard/Keep editing choice. Saved edits queued for the next launch stay queued; reload does not apply them. Each mod decides whether reloaded settings take effect immediately or only after restarting.

Unsaved edits offer Save, Discard or Keep editing before navigation. Changes to the active profile are queued for the next launch so a running plugin cannot overwrite them at shutdown. The preloader merges only the edited fields; unrelated settings saved by the game are retained. Inactive-profile saves write that profile's file with a backup. Queued edits remain tied to their profile, and conflicting external edits are kept for review rather than silently overwritten.

New profiles default to copying applicable configs from the active profile. The Configs tab also offers offline configs or fresh defaults.

Updating an existing profile defaults to **Keep this server's existing configs**. Updating an inactive profile therefore does not replace its settings with whichever profile happens to be active. An explicit replacement keeps the old files under:

    BepInEx/ModGate/config-backups/

**Sync applicable configs to all profiles** copies current settings to other profiles containing the same mod GUID. Previous destination values are backed up. Global BepInEx/ModGate settings are excluded. The summary is in ModGate/config-sync-report.txt.

For profile copying and sync, ModGate recognizes registered BepInEx config paths and GUID-named cfg/json/yaml files. The built-in editor handles .cfg files; other formats remain available through **Open config folder**. Arbitrary custom data files are not guessed. Mods requiring special configuration storage may need manual handling.

## Server configs for administrators

While playing, press **Shift+F6** and choose **Server configs** in the sidebar, or **Server configs (admin)** from a profile's **Configs** tab. This opens the connected server's mod **.cfg** files and **ModGate/server-mods.json**. Local profile settings remain separate.

Remote access requires both a normal server admin-list entry and an owner-issued access code. The server creates its owner key at:

    BepInEx/config/ModGate/admin-access.key

The owner can provide that code privately to trusted administrators. Enter it in **Admin access code**, then choose **Connect / refresh server files**. The client keeps it in memory until disconnect. Requests use an HMAC proof bound to the connection's session nonce, contents and sequence; the raw code is not sent as a request field. The server checks authorization for each request, rejects replayed requests, and limits request rates. Signed sequences let long-running health refresh continue without filling a replay cache. The local hosting process can use its local owner access without entering the remote code.

Mod .cfg files use typed setting controls and validation. Password/token-like entries are excluded, unsupported settings stay read-only, and unedited sections/comments are retained. Loader/global files and private paths are not offered. **server-mods.json** uses a JSON editor for its supported schema fields; field-lock asterisks, owner links and existing classification rules are preserved. Invalid fields or values are rejected. Unresolved download mappings can remain while an administrator corrects the server setup.

Server files also start collapsed, with the selected document immediately below its filename. **Reload configs** refreshes the authenticated server listing and reloads eligible running mods' bound .cfg files on the host. It preserves pending edits. Queued .cfg changes and ModGate's server settings JSON still require a server restart; a mod must support reload for its current disk settings to apply live.

**Save server changes** queues the edit on the server. Apply it by restarting the server through its normal host controls. The preloader backs up the existing file and applies the validated edit before plugins load. Ordinary .cfg edits merge only changed fields so unrelated shutdown writes survive. Conflicting field edits or a changed server policy stop the queued edit for review instead of overwriting newer settings.

Queued mod-config edits remain tied to the active profile slot and server identity. If a local host switches profiles before applying them, return to the original profile or review the pending edit on the host. The edit remains queued and the newly selected profile's file is kept. ModGate's server-mods.json is server-wide.

Canceling a client's wait or disconnecting does not undo a save the server has already accepted. Refresh the server files to check their current/pending state before retrying. Remote files are limited to 256 KiB, with up to 1,024 files in a listing; larger or unsupported files require host-side editing. Owner access keys and queued config values are excluded from support reports.

## Server health for administrators

While connected, open **Server health** from the sidebar or main tabs. It reports the connected server, regardless of which saved profile you were browsing. Remote access uses the same server admin-list entry and owner-issued access code as server configs. The local hosting process uses local owner access.

Opening the page sends no request. Choose **Refresh server health** for one sample or **Start auto refresh** for a sample every two seconds. Only one request runs at a time. Auto refresh stops when you leave the view, close ModGate, disconnect, or receive an error. In-flight replies from an old view or server cannot restore its data after leaving or reconnecting.

The dashboard shows process CPU, resident and managed memory, main-loop update intervals/cadence, server-session uptime, player count, network traffic rates and the outgoing queue. CPU **100% means one logical CPU core**, so a process using several cores can exceed 100%. Managed memory overlaps the process-memory measurement. Listen-host process readings also include that player's client work. Update cadence describes observed game updates; it is not a simulation TPS or client rendering measurement.

Remote **Request round trip** measures the authenticated request and response, including server processing, on Steam or crossplay. Its change between replies is shown as jitter. Local-owner access displays **Local host / Not applicable** for network latency. Connection history keeps up to 60 round-trip/jitter samples in memory and is cleared on disconnect; telemetry is not saved to disk.

Some readings need a second sample. Unsupported readings show **Unavailable**; GPU and temperature data are not collected. A sample received more than six seconds ago is marked **Stale**. Server process readings are cached for two seconds, and freshness is measured from receipt on the client. Viewing health uses the administrator authorization path and does not read or edit config contents.

## Profile checks and targeted repair

Open a saved profile and select **Check profile**. ModGate compares its installed plugin identities, versions and hard dependencies with the recorded setup. Where a trusted package receipt exists, it also checks for missing, changed and untracked files. Active-profile checks can distinguish installed files from the versions currently loaded by the game.

Select repairable issues and choose **Repair selected files**. ModGate downloads the exact recorded packages again, checks their files and DLL identities against the trusted receipts, then prepares the repair for review and restart. Other packages, disabled bundles and profile configs are retained. The original files stay in place until the preloader applies the reviewed transaction.

Automatic repair is limited to ModGate-owned package groups with an exact supported download mapping and trusted file evidence. Unmanaged mods, unknown baselines, additional untracked files or changed upstream package contents require manual attention. Checking a profile does not install or upgrade BepInEx, and it does not prove that different mods are compatible at runtime.

## Restore points

Use **Restore…** to list a profile's saved setups or **Save restore point now** to capture one manually. ModGate also saves a restore point before replacing, editing or restoring an existing profile. Successful client play can record a working setup for later recovery.

Restore points contain the profile's owned plugins and assets, configs, disabled bundles and saved metadata. They remain under:

    BepInEx/ModGate/profile-history/

Global loader/ModGate files and its password store remain separate. Linked paths are refused. Payloads and receipts are hash-verified before restoration; changes made after review stop the restore instead of being overwritten unnoticed.

**Restore & restart** prepares the selected setup and activates it on the next launch. Offline and server profiles are supported. The current setup receives its own restore point first, and the replacement uses the same recovery journal as other profile changes. Restoring an older setup does not change a live server's requirements; it may still need updating before joining.

Repeated captures with the same purpose and unchanged files/metadata reuse an existing verified restore point. History is not deleted automatically. Restore points use local disk space and stay separate from the download cache.

## Mod details and support

Open the **Mod details** tab, search by name or GUID, and choose a mod to read its **Overview**, **Dependencies**, or **Release notes**. **Choose another mod** returns to the browser. Browsing details keeps the Mods tab's selections. Details distinguish the plugin DLL version, loaded version, package mapping and server policy. Required and optional dependencies, minimum versions and which mods use a dependency are shown separately. README/release notes are displayed when available in the local package; missing notes do not trigger a new repository search.

**Activity -> Create support report** lets you choose setup logs/errors, mod names/versions/sources, and health results for a local ZIP under:

    BepInEx/ModGate/reports/

Password data and raw config contents are excluded. Review the ZIP before sharing it. **Report a bug on Discord** opens [the support community](https://discord.gg/AaVzBfPcYH); creating a report does not upload or send it automatically.

## Downloads, retries and dependencies

Thunderstore and Hexium are the normal automatic sources. Settings saves priority in:

    BepInEx/config/ModGate/download-sources.txt

Exact server pins keep their chosen source. Selected unpinned dependency downloads can try the other repository when a release is missing. BepInEx dependencies are always skipped.

Matching installed mods are copied with their assets instead of being downloaded again. Their package metadata can provide dependency suggestions; otherwise preparation resolves that metadata. Packages outside the selected server mod list are offered as unchecked suggestions rather than downloaded automatically. Check a suggestion and prepare again to include it. Missing declared DLL dependencies are reported as warnings. Explicit server-required mods still must be present at an accepted version, and manual Nexus files still require a supported installed copy or manual installation.

Completed Thunderstore/Hexium archives are cached with local checksums under ModGate/download-cache. Retries reuse verified completed archives. Interrupted transfers can resume when the server provides suitable range/identity information. Hexium retains four range workers and checkpoints completed chunks. Changed or unsupported responses restart or stop safely rather than appending unrelated bytes.

Transient transfer errors retry up to twice. Failed/cancelled staging payloads are removed; resumable downloads remain in the cache. **Clear download cache** removes unscheduled staging and cached transfers, including completed archives. It does not remove saved profile mods, configs or restore-point history. Cleanup is disabled while an install/review is using those files.

There are no fixed client payload byte/file-count caps. Available disk space and runtime resources remain practical limits. Archive containment, links, protected files, source rules, receipts and rollback checks remain enforced. Network metadata bounds, wrapper depth, dedicated-server update budgets and config-sync limits are separate.

If an exact trusted-source version is unavailable, setup can try the latest release of the same package/repository and resolve its dependencies again. An available archive containing the wrong plugin version can also trigger replacement. Substitutions appear in the review. They never rewrite the server's DLL requirements or spoof a match. After setup stops or is canceled, **Retry setup** and **Override and continue anyway** remain available on the join screen. Override can prepare an incomplete profile, but missing dependencies can prevent loading and the server can still reject it.

## Server discovery and download links

The server scans loaded BepInEx plugins and maintains:

    BepInEx/config/ModGate/server-mods.json

The file is indented with spacing between mods. The old server-mods.discovered.json is unused. Generated DLL dependency declarations are no longer written into the editable mod entries. They are kept internally in BepInEx/ModGate/plugin-dependencies.json and still accompany the client policy for module/dependency handling. Existing inline dependency blocks are migrated automatically; owner settings and the mod list are retained. No manual JSON cleanup is needed. Missing or stale generated data is refreshed from installed DLLs; the cache never creates mod entries. DLL GUID, display name and plugin version determine identity and client requirements. Package version remains separate: for example, package JsonDotNET 13.0.4 can legitimately contain its detector plugin at 1.0.0. Manifests provide package/source hints; README text does not determine plugin identity.

Trusted repository lookup uses package metadata without candidate archive downloads or full DLL/hash inspection. Actual profile preparation reads the staged DLL metadata. Package provenance and checksum checks remain enforced, and the actual source is shown during review.

Existing downloadUrl, nexusUrl, side, automatic and autoUpdate values are preserved. An asterisk in a text field locks that stored field; runtime processing strips the marker. It is not a version wildcard. Other detected fields refresh. Automatically discovered removed entries disappear; explicit client-only requirements can use automatic:false.

For newly discovered mods, names containing Server Side/Serverside, ignoring punctuation and case, default to serverOnly. Additional GUIDs or exact names can be listed in config/ModGate/server-only.txt. Existing classifications are preserved. Review names that cannot identify the mod's intended client/server role.

Supported side entries are **required** (server/client requirement), **optional** (player choice), **serverOnly** (never offered to players), and **clientOnly** (required for players, suppressed on dedicated servers). Server-only mods are not offered to clients. Mixed packages containing prohibited plugins are rejected.

### Manifests for loose DLLs

When the server starts, ModGate creates:

    BepInEx/config/ModGate/manifests/

Each discovered plugin sitting directly in BepInEx/plugins gets its own folder, named by its actual plugin GUID, with a README identifying the mod and DLL. ModGate itself is excluded. For example, place OctoPack's original package manifest here:

    BepInEx/config/ModGate/manifests/OctoPack/manifest.json

Copy the mod's original manifest.json from its download into that folder. Keep the filename manifest.json. No files need to move out of plugins. ModGate creates instructions, never a fake active manifest. Existing manifests and owner-edited instructions are preserved.

The GUID comes from server-mods.json. DLL name, plugin name and GUID may differ; use the generated folder/README. Unusual, reserved or case-colliding GUIDs receive a deterministic guid-<hash> folder so two plugins cannot share a manifest accidentally. Installed plugins with their own folders can also use a manually created GUID fallback folder if their package manifest is missing. Suppressed clientOnly DLLs use this system too.

An existing valid package manifest, including a Windows mod-manager target manifest, takes priority. A shared BepInEx/plugins/manifest.json is not applied to unrelated loose DLLs. Each server-supplied manifest is limited to 64 KiB and read as package metadata using the existing source/dependency rules. Invalid, oversized, inaccessible or linked fallback paths produce a ModGate Manifests warning while normal discovery continues.

The manifest supplies package name/version, website and dependency hints. DLL GUID, display name and plugin version remain authoritative; different DLL/package versions do not create extra mod entries. Restart the server or reconnect after adding the file to trigger fresh discovery; reconnect again if background lookup is still running. The preloader updater uses the resulting saved package mapping on a subsequent restart.

For owner-selected automatic links, use a version-specific HTTPS Thunderstore or Hexium link in downloadUrl, for example:

    https://thunderstore.io/package/download/ValheimModding/Jotunn/2.29.2/
    https://hexium.gg/package/download/Bascinet/OctoPack/1.7.4/

Only supported routes on thunderstore.io or hexium.gg are accepted for manual automatic-download links. Redirects remain restricted to approved provider delivery hosts. A preserved link that disagrees with a detected package is kept and reported for the owner to correct.

Nexus remains manual: set nexusUrl to a Valheim Nexus mod page (optionally its Files tab), keep downloadUrl empty, and use the in-game link to download/install through Nexus. No account credentials are scraped or handled. Matching installed Nexus files can be copied into a profile.

Lookup results are in config/ModGate/lookup-report.json. Background lookup has a deadline and retry cooldown; already-matching clients can join while it runs. Reconnect after an unfinished lookup completes.

## Restart, deletion and recovery

Reviewed installs and edits offer **Install/Save and restart** or **Install/Save on next launch**. Windows restart waits for normal exit of the exact original process, preserves the active BepInEx bootstrap and manager settings, then requires the new ModGate process to acknowledge profile readiness. It never force-kills Valheim. It does not automatically reconnect.

Both ModGate DLLs must be in their correct folders. Unusual/non-Windows launchers can use the manual next-launch option. Keep using the same external mod-manager profile. Patchers and loader settings remain global.

Deleting an active server profile restores offline first. Deleting an inactive profile keeps the current setup. Offline cannot be deleted. **Remove all downloaded mods** removes ModGate's server-profile copies and restores the original offline setup.

Profile installs, edits and restores use receipts and recovery journals. If startup fails, keep the journal while diagnosing it; interrupted moves can be rolled back or completed on the next launch. Use profile checks and restore points for damaged saved setups, and Activity/support reports for the error details. Useful files:

    BepInEx/ModGate/logs/profile-setup.txt
    BepInEx/ModGate/install-error.txt
    BepInEx/ModGate/restart-status.txt
    BepInEx/ModGate/restart-error.txt

Escape and fixed exit controls remain available during setup. Native password remembering uses the same mouse-input fallback as the mod window.

## Server auto-update (OFF by default)

Server automatic updates remain off by default:

    "autoUpdateOnRestart": false,
    "autoUpdateAllMods": false

The master switch must be true before any individual autoUpdate:true entry is eligible. null follows update-all; false excludes a mod. Updates run at dedicated-server startup before plugins load, using configured Thunderstore/Hexium packages. BepInEx, ModGate, patcher/loader packages and manual Nexus entries are not automatically updated.

When auto-update is enabled for an entry, its advertised package follows the updated installation. An older manual downloadUrl for that same package and repository stays in the config but does not force clients back to the old release. Unrelated links or disabled auto-updates still produce a configuration conflict rather than silently changing their meaning.

Server startup updates use the operating system's curl directly. The preloader has no managed HTTP references and does not initialize System.Net.Http before Valheim starts. Client profile downloads retain the managed transport with a secure system fallback for SSL/TLS failures.

Windows uses the system curl.exe; Linux uses /usr/bin/curl or /bin/curl. Requests stream files, support resumption/ranges, validate repository redirect hosts and use normal certificate verification. ModGate ignores curl config files and starts the helper without a visible window. A host without curl, current CA certificates, a correct clock or working HTTPS access can still fail. Update failures retain the installed files and report their cause; the preloader never falls back to managed HTTP. Automatic updating remains off by default.

Startup logs include passive snapshots of already-loaded HTTP assemblies before and after ModGate's startup work and after Valheim server startup, including their paths and module IDs. These diagnostics do not load an HTTP assembly. They help compare runs with ModGate enabled and disabled without changing Valheim's public-IP code.

Packages need isolated ordinary plugin folders. Updates validate plugin identities and transaction paths, preserve configs/protected settings, keep old files and recover interrupted swaps. Declared package dependency problems are reported as warnings rather than removing an otherwise eligible update; the updater does not download extra dependency packages automatically. BepInEx may still refuse to load an updated mod whose hard dependencies are missing. Inspect config/ModGate/server-update-report.json. Linked/shared folders and unsupported installers require manual management.

## Build and validation

PowerShell 7 and a .NET SDK with the necessary reference packs are required. Game and loader assemblies are references only:

    ./build.ps1 -GameDirectory "A:/SteamLibrary/steamapps/common/Valheim" -BepInExCoreDirectory "path/to/BepInEx/core"

Windows encryption tests require the normal Windows user context. The build script runs the automated checks; validate joining, profile installation, restart and reconnection in your own environment. Automated fixtures are not an in-game multiplayer test.

Implementation references: [BepInEx discovery](https://github.com/BepInEx/BepInEx/blob/v5-lts/BepInEx/Bootstrap/TypeLoader.cs), [BepInEx chainloader](https://github.com/BepInEx/BepInEx/blob/v5-lts/BepInEx/Bootstrap/Chainloader.cs), [curl options](https://curl.se/docs/manpage.html).
