# ModGate

**Your worlds. Your mods.**

**Let ModGate do most of the setup for you.** Join a Valheim server, then use one button to download its required mods from supported sources. ModGate checks what you need, finds the packages, reuses matching installed mods, and prepares your server profile. You review the result, install and restart, then join again.

Keep **four server profiles and an Offline profile**, each with its own mods and configs. Your saved setups are ready to activate when you switch servers.

**Install ModGate on both the server and every player joining it. BepInEx must already be installed.** The player needs ModGate before the download prompt can appear; a server cannot install this connector onto an unmodified game.

[Download ModGate](https://github.com/octerockman/ModGate/releases/latest) · [Server mod download guide](https://modgate.pages.dev/guides/download-valheim-server-mods/) · [Multiple server profiles guide](https://modgate.pages.dev/guides/multiple-valheim-server-profiles/)

![Animated ModGate interface preview showing profiles, downloads, and configs with example data](https://modgate.pages.dev/assets/interface-preview.gif)

*Animated interface preview — example data. Text and layout may vary in game; this is not a gameplay recording.*

## Why players want it

- **One button downloads the server's required mods together.** ModGate handles the checks, package lookup, downloads and profile preparation for supported packages.
- **Most of the work is automatic.** Matching installed mods are reused and your server setup is saved. Review the changes, choose **Install and restart**, and reconnect after the game relaunches.
- **Keep your choices.** Select the optional mods and suggested dependencies you want, then keep those choices and applicable configs in that server's profile.
- **Switch setups easily.** Activate one of four saved server profiles or return to Offline. Profile checks, repair and restore points help you recover a saved setup when needed.

Automatic downloads use supported Thunderstore and Hexium packages. Nexus downloads remain manual, and an unresolved package needs attention before it can be downloaded.

## Why server owners want it

- **Make joining easy for your players.** ModGate shares your requirements and handles most of their setup work through the in-game download prompt.
- **Let ModGate maintain the detected mod list.** It discovers installed plugins and looks up supported downloads. You review the classifications and correct any unresolved mappings.
- **Customize your server's rules.** Choose required and optional mods, keep server-only mods off player downloads, and specify mods that players need only on the client.
- **Choose whether players can bring their own client-side mods.** Allow additional client mods, or restrict players to your approved list. Your required-mod rules still apply.
- **Control versions and updates.** Require your chosen versions or allow newer client versions. Enable eligible server updates for all mods or selected mods at startup. **Automatic updating is off by default.**
- **Manage settings in game.** Authorized admins can edit supported server configs and inspect server health through ModGate.

## A player's first join

1. Install ModGate and launch Valheim through your usual BepInEx setup.
2. Join a server that also runs ModGate. If your setup differs, ModGate shows the changes needed.
3. Required mods are already selected. Choose any optional mods and suggestions you want, then press **Download and review**. ModGate checks packages, downloads what is needed from supported sources, and prepares the profile for you.
4. Choose **Install and restart**, or schedule installation for your next launch. Windows can relaunch Valheim automatically; other launchers can use a manual restart.
5. Join the server again after the restart. ModGate checks the current requirements on every join.

ModGate saves the server profile for future use. Press **Shift+F6** to open the panel, edit a profile, choose a different setup, or activate Offline. Changes to loaded mods apply after restarting; Valheim does not reconnect automatically.

## Installation

Both the server and each player need the **same ModGate version**. Each installation needs these two components:

```text
BepInEx/plugins/ValheimModGate/ValheimModGate.dll
BepInEx/patchers/ValheimModGate/ValheimModGate.Preloader.dll
```

For a manual install, close the game or stop the server, then copy the package's **BepInEx** folder into the corresponding Valheim installation and merge the folders. Keep existing configs and profiles when upgrading. The preloader DLL belongs in **patchers**, not plugins.

Mod managers can use their own author/package folders underneath plugins and patchers. Keep launching the same external manager profile: ModGate's five profiles belong to that installation, and loader settings and patchers remain global.

BepInEx is a prerequisite. ModGate does not install or update BepInEx, including when another mod lists it as a dependency. The package declares the loader dependency for external mod managers; their dependency handling is separate from ModGate's installer.

## Server setup

Start the server once with both ModGate DLLs installed. Its discovered mod list is maintained in:

```text
BepInEx/config/ModGate/server-mods.json
```

Review the classifications and resolve any missing download mappings before inviting players. Use these values for a mod's **side**:

| Value | What it means |
| --- | --- |
| `required` | A required server mod that joining players also need. |
| `optional` | A player can choose whether to include it. |
| `serverOnly` | Runs on the server and is never offered to players. |
| `clientOnly` | Required for players; its normal plugin instance is suppressed on dedicated servers. |

Newly discovered names containing “Server Side” or “Serverside” default to server-only. Review the list: a name cannot reliably establish every mod's intended role. Existing owner classifications are preserved.

For a loose DLL without its own package folder, place the original package's **manifest.json** in the generated folder under **BepInEx/config/ModGate/manifests/**. Each folder identifies the real plugin GUID. Package metadata helps lookup; the installed DLL remains the authority for plugin identity and version.

The included **docs/SERVER-OWNER-GUIDE.md** explains download mappings, version choices, updating and administrator access. **server-mods.example.json** is a reference example, not a ready-made list for your server.

## More than downloads

- **Per-profile configs:** edit supported BepInEx settings, copy applicable settings when creating profiles, or sync them between profiles. Config files expand below their own buttons. Reload supported running-mod configs from disk.
- **Recovery:** profile checks, targeted repair, restore points, setup logs, retry controls and support reports. An override can continue an incomplete preparation, but cannot make missing dependencies load or force a server to accept it.
- **Remembered passwords:** Windows can remember a server password after a successful join. This is enabled by default and can be disabled. Saved passwords use Windows account encryption.
- **Server administration:** existing admin-list membership plus an owner-issued access code protects remote configs and health. View server CPU/memory, request latency and jitter, network traffic, players, session uptime and update timing. Unsupported measurements show as unavailable; GPU and temperatures are not collected.
- **Clear controls:** a profile sidebar, separate Mod details tab, visible progress, and a bug-report link. Gameplay and camera input are blocked while the ModGate window is open.

## Sources, compatibility and practical limits

Automatic setup uses supported Thunderstore and Hexium packages. You can choose a preferred source order; an explicit server package pin retains its source. Nexus entries open an owner-supplied download page for manual installation. ModGate does not copy arbitrary mod files directly from the server to players.

Only accept mod downloads from servers you trust: mods run code on your computer. A matching list is a setup check, not a guarantee that every combination of mods will work together. Missing hard dependencies can still prevent BepInEx from loading a mod. A newer substitute release cannot bypass the server's version policy.

Windows supports automatic relaunch and encrypted password storage. Other launchers/platforms can use normal password entry and the manual next-launch option. Available disk space limits how many profile copies, downloads and restore points you can keep.

Version **0.4.4** is an early release. Automated tests cover the setup and recovery logic; actual rendering, host permissions and mod interactions still need validation in your own environment.

## Help and bug reports

Open **Activity** or **Open setup log** for preparation details. Include the ModGate version, the exact error, and what you were doing when reporting a problem. The panel can create a support report; do not share passwords or your server's admin access key.

[Report a bug or get help on Discord](https://discord.gg/AaVzBfPcYH)

## Source and downloads

[ModGate website — downloads and setup guides](https://modgate.pages.dev/)

[Source code on GitHub](https://github.com/octerockman/ModGate) · [GitHub releases](https://github.com/octerockman/ModGate/releases)

## Free and open source

**ModGate is free and open source under the [MIT License](LICENSE).** Anyone can use, copy, edit, fork and redistribute it, including modified versions and commercial use. Keep the copyright and license notices with copies or substantial portions of the software. You do not need to ask for permission to edit or share ModGate.

Valheim is developed by Iron Gate; ModGate is an independent community mod.
