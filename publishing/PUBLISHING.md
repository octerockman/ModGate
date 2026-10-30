# Publishing ModGate 0.4.4

The release is prepared locally. No website upload or Discord announcement is performed by the build scripts.

## Files to use

- **ValheimModGate-0.4.4-publish.zip** is the website upload package. Keep it zipped.
- **README.md** at the project root is the public description displayed on the mod page.
- **publishing/manifest.json** contains the short description, package name, version, support link and loader dependency.
- **publishing/icon.png** is the approved 256×256 square cover with the download tagline. **icon.svg** retains the original gateway artwork; the cover compositor and 1024×1024 master are retained in images/ModGate-Square-Cover-v2/.
- **publishing/CHANGELOG.md** contains the version notes.
- The three **docs/** guides ship inside the upload ZIP for offline reference. Site rendering of relative documentation links is not assumed.

The upload ZIP includes exactly two ModGate DLLs and the public documentation/example files. It does not include BepInEx, Harmony, Cecil, game assemblies, test programs, logs, saved profiles, passwords or an admin key.

## Build the upload package

To build, test and package from source in PowerShell 7:

```powershell
./build-publish.ps1 -GameDirectory "path/to/Valheim" -BepInExCoreDirectory "path/to/BepInEx/core" -NoRestore
```

Omit **-NoRestore** if this checkout has not restored its build dependencies. Once the Release DLLs have already been built and tested, rebuild only the upload ZIP with:

```powershell
./build-publish.ps1 -UseExistingBuild
```

Output is under **dist/publishing/0.4.4/**. The script checks the manifest, description length, icon size, DLL identities/versions, explicit file list and the archived file hashes. **SHA256SUMS.txt** records the ZIP hash. It does not replace a live join/restart check or the website's own validation.

## Upload settings

| Setting | Use |
| --- | --- |
| Package name | **ModGate** |
| Version | **0.4.4** |
| Game/community | **Valheim** |
| Install location/side | **Both client and server** |
| Team | Your existing publishing team, selected on the upload page. The team name is not a manifest field. |
| Homepage/source | **https://github.com/octerockman/ModGate** |
| Support | **https://discord.gg/AaVzBfPcYH** |
| Release description | Use the README included in the ZIP. |
| Categories | Choose the site's current utility/management categories where available. |

Thunderstore requires root-level **manifest.json**, **README.md** and a **256×256 icon.png**. Its BepInEx folder routes support separate plugins and patchers. See [package requirements](https://wiki.thunderstore.io/mods/creating-a-package) and [BepInEx packaging](https://wiki.thunderstore.io/mods/packaging-your-mods).

Hexium documents support for Thunderstore-compatible packages and asks whether a mod is client, server or both. The same upload ZIP is prepared for both sites. Hexium strips the BepInExPack_Valheim dependency on upload because it assumes the loader is installed. See [Hexium packaging](https://hexium.gg/packaging).

The Thunderstore manifest declares **denikson-BepInExPack_Valheim-5.4.2351**, an existing [Valheim loader package](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/v/5.4.2351/). External managers may resolve it; ModGate itself continues to skip BepInEx downloads and updates.

## Before making the release public

The build scripts run automated checks and inspect the package. Complete the live checks with this exact ZIP: a separate client joins, prepares a changed setup, restarts and rejoins; switching back to Offline keeps ModGate available; and the intended host loads both DLLs and reports the expected server list. A manager-folder fixture is covered locally; no real mod-manager installation or hosted-server deployment was performed while preparing the files.

Review the mod page preview, icon, support link and the selected publishing team. Upload under the same team and package name for later updates. Website publication and any announcement are separate actions.
