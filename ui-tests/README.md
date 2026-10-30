# Production UI verification

Run the prompt simulations with `dotnet run --project ui-tests/Prompt.Tests.csproj -c Release`. Use `-- --041-only` for layout/details-tab checks, `-- --042-only` for shortcut/accordion/reload checks, or `-- --health-ui-only` for server health refresh, lifecycle and layout checks.

Generate draw replays from the actual production `Prompt.Draw()` implementation:

```powershell
dotnet run --project ui-tests/Prompt.Tests.csproj -c Release -- --visual-preview ../work/ui043-preview
pwsh -File ui-tests/Render-VisualPreview.ps1
```

The exporter writes JSON, SVG and HTML for the mod list, dependency suggestions, profiles, details browser, selected mod, local and server config editors, server health/history, and download view at 1280 × 1000. It also captures the health dashboard at 640 × 900. The rasterizer reads each JSON viewport and opens only these local HTML files using an isolated temporary Chrome/Edge profile, then writes PNGs alongside them. Pass `-BrowserPath` or `-OutputPath` when needed.

These are **production draw replays, not in-game screenshots**. Fixed rectangles, draw order, generated texture pixels, nine-slice borders, colors and clipping are captured from production calls. The fixture data is synthetic; the flat backdrop is a preview stage. Browser text metrics and intrinsic `GUILayout` sizing approximate Unity. This helps find clipping and overlap but does not replace checking the packaged UI inside Valheim.
