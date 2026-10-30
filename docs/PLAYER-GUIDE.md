# ModGate player guide

ModGate does most of the work of getting your game ready for a modded Valheim server. Use **Download and review** to download the server's required mods together from supported sources. ModGate checks the requirements, finds packages, reuses matching installed mods, downloads what is needed, and prepares a saved server profile. You review it, install and restart, then join again.

You need ModGate on your computer because the server cannot install mods on your behalf. The server also needs ModGate so it can tell your copy which mods and versions to use. An unmodified Valheim client cannot display ModGate's setup window.

## Install once

1. Install BepInEx for Valheim first. ModGate assumes it is already installed.
2. For a manual install, close Valheim normally and put both ModGate files into the game installation or mod-manager profile that you normally launch:

   ```text
   BepInEx/plugins/ValheimModGate/ValheimModGate.dll
   BepInEx/patchers/ValheimModGate/ValheimModGate.Preloader.dll
   ```

3. Start Valheim as usual. Your ModGate version must match the server's; for this release, both sides need **0.4.4**. Keep your existing profiles and configs when upgrading.

Mod managers can add their own author/package folders below plugins and patchers. Keep using the same external manager profile. ModGate does not install or update BepInEx or update its own connector automatically.

## Join a server

1. Try connecting normally. If your setup matches, ModGate lets the connection continue. If it differs, review the setup window.
2. Required mods are selected for you. Choose the optional mods and suggested dependencies you want. Green means an installed version the server accepts, including a newer version when the server permits it. Red means a required mod is missing; yellow means a version change or an absent optional mod.
3. Prepare the profile. ModGate reuses matching installed mods and downloads selected supported packages from Thunderstore or Hexium. Review the prepared changes before installing.
4. Choose **Install and restart**, or schedule installation for your next launch. The preloader applies the profile before other plugins load. Automatic relaunch is supported on Windows; other launchers can use the next-launch option.
5. After restarting, join the server again. ModGate does not reconnect automatically.

Nexus downloads stay manual: follow the server-provided Nexus page, install the file according to its author's instructions, restart, and try again. Matching installed files can then be reused in the profile.

## Keep different servers separate

Press **Shift+F6** to open ModGate in the menu or while playing. It keeps up to **four server profiles and one Offline profile**, with separate applicable mod configs. Use **Activate & restart** on a saved profile to switch setups, including Offline. Keep launching the same external mod-manager profile.

Use a profile's **Mods** tab to change permitted selections, **Configs** to edit its settings, **Mod details** to read package information, and **Activity** for setup progress and errors. Optional mod sections start expanded and can be collapsed. ModGate blocks normal gameplay and camera input while its window is open.

New profiles can copy applicable settings from the active profile, use Offline settings, or start fresh. Changes to the active setup are applied on restart. **Reload configs** can reload eligible running mods' current config files, but individual mods decide whether changes take effect live.

On Windows, password remembering is enabled by default and uses your Windows account's encryption. The normal password dialog lets you choose whether to remember a server password. A password is saved only after a successful connection; you can forget it or disable remembering in ModGate.

## If setup stops

Open **Activity** or **Open setup log** for the actual error and package involved. You can retry or use **Override and continue anyway** to prepare the available files. Override does not make a missing mod compatible: BepInEx can refuse to load mods with missing hard dependencies, and the server can still reject an incomplete setup.

**Check profile** and **Restore…** help inspect or recover saved setups. **Remove all downloaded mods** restores the original Offline setup and removes ModGate's server-profile copies. It does not uninstall BepInEx.

For help, use **Report a bug** or visit [the ModGate support Discord](https://discord.gg/AaVzBfPcYH). Activity can create a support ZIP for you to review and share; it is not uploaded automatically.

