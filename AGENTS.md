# PCGauger — Agent instructions

## Repo

- **Single C# .NET 8 WinForms project** at `src/PCGauger/PCGauger.csproj`.
- **No test project, no test framework.** Verify via `dotnet build`.
- **No typecheck step** — nullable enabled in csproj, but no dedicated checker.
- **Version** in `<Version>` in the csproj (currently `2.8.0`). Bump before release.
- **`publish/` and `*.zip` are gitignored** — never commit build artifacts.
- Pre-existing warning `CS8601` at `MainForm.cs:298` (line drifts with edits) — leave it.

## Build & run

```powershell
dotnet run --project src/PCGauger
dotnet build src/PCGauger/PCGauger.csproj -c Release   # compile check
dotnet publish src/PCGauger/PCGauger.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\<name>
```

## Release pipeline

1. Bump `<Version>` in csproj, commit (source only), push.
2. Build single-file publish (command above).
3. Zip contents: `Compress-Archive -Path "publish\<name>\*" -DestinationPath "publish\PCGauger-vX.Y.Z-win-x64.zip"`
4. Release + upload: `gh release create` (use `--notes-file`, not inline text) then `gh release upload`. Verify with `gh release view`.

See `HANDOFF.md §4` for full commands and gotchas.

## Architecture at a glance

| Layer | Key files |
|---|---|
| Entry | `Program.cs` — STAThread, splash, exception barriers, `Application.Run(MainForm)` |
| Metrics | `Metrics/Providers/*.cs` — `IMetricProvider` with `Update`/`GetMetrics` |
| Poller | `Infrastructure/MetricPoller.cs` — 1s timer, per-provider async isolation, fault isolation |
| Rendering | `Rendering/TileRenderer.cs` — SkiaSharp software render, flow-based layout in `TileVisual.Finish` |
| Grid | `Rendering/GridLayout.cs` — adaptive column count from aspect ratio |
| Canvas | `HitTestSurface.cs` — `SKControl` with alpha-preserving `SetDIBitsToDevice` GDI present |
| Config | `%LOCALAPPDATA%\PCGauger\config.json` (atomic save, auto-migrates v1) |

- **Notable:** all P/Invoke + COM interop (DXGI, PDH, NtQuerySystemInformation). No elevation (`asInvoker`).
- **Threading:** providers run on pool tasks, never block UI. `MetricPoller` has re-entrancy guards and bounded drain on `Remove()`.

## Key conventions & quirks

- **Graceful degradation** over clipping — graph drops out first, then details line, then both return when space permits. Never overlap.
- **Fade-truncation** (gradient dissolve) used instead of ellipsis for overflow text.
- **Graph anatomy:** a 16px top band inside the plot holds the axis-max label (+ dual-graph legend); the curve ceiling maps just UNDER it, so values never poke above the "100%" label. The dashed line is the **alert threshold** (percent graphs only; hidden when alerts are off or off-scale).
- **Detached tiles** have their own renderer instance; axis-hysteresis state is per-renderer.
- `InvariantGlobalization: true` in csproj.
- `git push` prints a benign `RemoteException` banner in PowerShell — push still succeeds; verify `master -> master`.
- `gh` CLI is authenticated (account `YamanKoudmani`). Releases on GitHub.

## Key references

- **`HANDOFF.md`** — detailed architecture, build/release commands, owner preferences, gotchas (read it first).
- **`README.md`** — feature list, requirements, metric data sources.

## Session anchor — v2.8.0 shipped (Clarity Release)

### Shipped in v2.8.0
- **GPU core temperature** in the GPU tile details line (NVAPI for NVIDIA, ADL2 for AMD; new `NvapiInterop.cs` / `AdlxInterop.cs`; hidden when unavailable).
- **Settings pane polish**: section dividers (uppercase label + rule), ← Back / Done buttons, footer separator + hover tooltips, close confirmation when pane open, 18px pane title, neutralized dark-theme pane backgrounds.
- **Accent color byte-order fix** in `TileConfig.ApplyTo` (`SKColor` takes R,G,B,A; persisted ARGB loaded shifted → pink). NOTE: users with a pre-fix config may still hold shifted values; deleting `%LOCALAPPDATA%\PCGauger\config.json` resets.
- **Sparkline clarity fix**: 16px reserved top band for the axis-max label/legend; the curve ceiling maps just UNDER the "100%" label (curve can no longer poke above its own label). `GraphFloor` 40 → 56 to preserve minimum curve height.
- **Dashed line repurposed → alert threshold** (`TileVisual.SparkAlertThreshold`, renamed from `SparkTypicalMax`): percent graphs (CPU/RAM/GPU) only, drawn in `AlertColor` @ ~43% alpha at its true value; hidden when alerts are off or the threshold is above the current axis.

### Key decisions
- Dashed-line semantics chosen by owner (over recent-average / 95th-percentile / label-the-90%): it shows the Settings → Alerts threshold; cross it → tile alerts. No fallback line when alerts are off.
- Root cause of the "values exceed 100%" misread was visual crowding: curve ceiling, axis label, and reference line all shared the same ~5px band at the graph top.
- GPU utilization is clamped at 100 in `GpuProvider` (summed PDH engine counters can exceed 100 on multi-engine GPUs).

### Relevant files
- `src/PCGauger/Rendering/TileRenderer.cs`: `DrawSparklinePath` (top band, threshold line), `DrawSparkline` (threshold assignment), settings-pane drawing
- `src/PCGauger/Rendering/TileRenderer.Devices.cs`: device-aware pane overloads
- `src/PCGauger/Metrics/Providers/GpuProvider.cs`: temp polling, util clamp; `NvapiInterop.cs` / `AdlxInterop.cs` (new)
- `src/PCGauger/MainForm.cs`: footer, close confirmation
- `src/PCGauger/Infrastructure/AppConfig.cs`: `TileConfig.ApplyTo` byte-order fix
