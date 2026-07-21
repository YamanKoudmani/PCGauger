# PCGauger — Session Handoff

> Context for the next working session: what this project is, how to build/release
> it, the owner's preferences, architecture notes, and gotchas learned the hard way.
> Last updated at **v2.5.0** (commit `0949fe2`).

---

## 1. What this is

**PCGauger** — a Windows hardware-monitoring dashboard. Owner-drawn SkiaSharp tiles
for **CPU / RAM / GPU / Disk / NET**, with:
- Multi-instance device support (multiple GPUs / disks / NICs, per-tile device picker).
- Per-tile settings (toggles for Title / Usage % / Bar / Graph / Details, units, accent color).
- Detachable tiles (a tile can pop out into its own window).
- Global settings pane (launch-at-startup, kiosk mode, always-on-top, threshold alert, theme, graph span, decimals).
- Footer "Top process" status bar (top CPU / RAM / GPU / Disk consumer).
- Loading splash at startup; config persisted to `%LOCALAPPDATA%\PCGauger\config.json`.

## 2. Tech stack

- **C# / .NET 8**, WinForms host (`MainForm`).
- **SkiaSharp** `HitTestSurface : SKControl` (software/GDI+ rendering, `SkiaSharp.Views.WindowsForms` 3.119.0).
- **Win32 / PDH P/Invoke** for CPU/disk/perf counters, **DXGI COM interop** for GPU
  (vtable delegates in `DxgiFactory.cs`), **`System.Net.NetworkInformation`** for NET.
- Single project: `src/PCGauger/PCGauger.csproj`. **Current version: `2.5.0`** (`<Version>` in csproj).

## 3. Repo & tooling

- GitHub: `github.com/YamanKoudmani/PCGauger`, branch `master`, remote `origin`.
- **Git auth:** Windows Credential Manager — plain `git push` works.
- **`gh` CLI is authenticated** (account `YamanKoudmani`, keyring, `repo` scope) — used for releases.
- **Do NOT commit `publish/`** — build artifacts are local-only (gitignored). Only commit source.

## 4. Build / test / release commands (verified working)

Compile check:
```powershell
dotnet build src\PCGauger\PCGauger.csproj -c Release
```
Single-file release build (what ships):
```powershell
dotnet publish src\PCGauger\PCGauger.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\<name>
```
Zip (contents, not the wrapping folder — use the `\*`):
```powershell
Compress-Archive -Path (Join-Path "publish\<name>" "*") -DestinationPath "publish\PCGauger-vX.Y.Z-win-x64.zip" -CompressionLevel Optimal
```
Release (notes via FILE, not inline — see gotchas):
```powershell
gh release create vX.Y.Z --title "vX.Y.Z" --notes-file "<tmp\notes.md>"
gh release upload vX.Y.Z "publish\PCGauger-vX.Y.Z-win-x64.zip"
gh release view vX.Y.Z --json tagName,assets   # verify
```

**Release pipeline order:** bump `<Version>` in csproj → commit **source only** → push →
build single-file → zip → `gh release create` → `gh release upload` → verify.

## 5. Owner preferences (learned this session)

- **Release packaging:** a **single portable EXE inside a zip** — NOT the 273-file
  self-contained dump. ("I wanted it to be a simple exe in a zip folder.")
- **Releases on GitHub** with clear notes; likes version framing (e.g. v2.5.0 = "Maturity Release").
- **UI/UX bar:** reliable scaling from small/portrait to large windows. Specifically:
  - **Graceful degradation over clipping** — graph drops out, then details line hides; both return when there's room. Never overlap/clip.
  - **Fade-truncation** (footer-style gradient dissolve into the card) instead of ellipsis or overflow.
  - **Aligned tiles** — a device subtitle must not push bar/graph/details down; everything lines up.
  - **Dynamic headline font with a hard minimum** (stays prominent).
- **Communication:** concise, status tables welcome, no fluff. Decisive — once verified,
  wants work committed + pushed + released without hand-holding.
- Sometimes wants a **local test build first** to eyeball changes before committing/releasing.

## 6. Architecture notes (current state)

**Metric engine**
- `Infrastructure/MetricPoller.cs` — drives providers on a 1s timer. **Rewritten in v2.1.2**:
  each provider's `Update`/`GetMetrics` runs on its **own pool task** per tick (fire-and-forget),
  so one slow provider can't starve the rest. Per-provider re-entrancy guard; `Remove()` drains
  (bounded 3s) the in-flight update so rebind-time `Dispose` is safe; `Start()` uses the timer
  only (the old `Task.Run(Tick)` + `TimeSpan.Zero` double-fire is gone).
- `Metrics/Providers/` — `CpuProvider` (GetSystemTimes + NtQuerySystemInformation + PDH clock),
  `MemoryProvider`, `GpuProvider` (DXGI + PDH GPU Engine; deferred resolve with **retry-backoff**),
  `StorageProvider` (PDH LogicalDisk; **lazy PDH-driven presence** — no per-poll `DriveInfo.IsReady`),
  `NetworkProvider` (managed `NetworkInterface`; re-selects each poll), `TopProcessProvider`
  (process enumeration + PerformanceCounter — the expensive one, now isolated by the poller).
- `Metrics/Catalogs/` — `GpuCatalog`, `NetworkCatalog`, `DiskCatalog` enumerate devices for pickers.

**Rendering**
- `Rendering/TileRenderer.cs` — tile body primitives. `DrawTitle` (inline title+subtitle, constant 30px),
  `DrawBigValue`/`DrawBigValueLiteral` (scaled headline via `BigValueFontSize`, 46px→26px floor),
  `DrawBar`, `DrawTextFaded` (fade-truncate helper), axis-hysteresis (`NextAxisMax`/`NiceCeiling`).
- `TileVisual.Finish` — **flow-based** details+graph placement with a vertical budget
  (graph floor 40px, details line 18px + 6px gaps). Graph hides below floor, details hides when
  it can't fit; details outlives the graph on a shrinking tile.
- `Rendering/TileRenderer.Devices.cs` — settings pane, device dropdown, global pane, unavailable-tile state.
- `Rendering/GridLayout.cs` (`Tile` model + grid math), `TileSettings.cs`, `Theme.cs`, `TilePalette`.
- `MainForm.cs` — startup (providers constructed **deferred**, pollers started, `BeginResolve`),
  `DrawFooter` (status bar; segments skipped when no room + hard clip left of the gear),
  `DrawCpu/Gpu/Disk/NetTile`, `SyncDeviceState` (mirrors provider availability + display name into tile).

## 7. What shipped (recent history)

- **v2.1.0** — multi-instance dashboard, per-tile settings, detachable tiles.
- **v2.1.1** — fix GPU name showing "0", NET showing a raw GUID, disk dropout debounce.
- **v2.1.2** — MetricPoller per-provider async rewrite (**startup freeze fix**), GPU resolve retry, lazy disk presence.
- **v2.5.0 (Maturity Release)** — responsive tile layout (flow-based Finish, graph/details degradation,
  headline scaling, inline subtitle, fade truncation) + footer status-bar clip fix.

## 8. Gotchas (read before you start)

- **Subagent lanes returned EMPTY this session.** explorer/fixer/designer background tasks
  repeatedly came back with no result text and **no edits applied** (git stayed clean). The
  orchestrator ended up implementing directly. **Always verify a subagent actually landed changes
  (`git status` / `git diff`) before trusting its "done."**
- **PowerShell 5.1 here-strings are finicky** — use `Set-Content` (or a temp file) for release notes;
  inline `--notes "<text>"` broke on special characters (`!`, `*`).
- **Single-file EXE + Windows Defender:** a *browser-downloaded* single-file exe can stall ~1 min
  on launch (MotW scan). Locally-run copies are fine. This is why we briefly tried multi-file —
  owner prefers single-file anyway.
- **Pre-existing warning** `CS8601` at `MainForm.cs:295` — unrelated to recent work, leave it.
- `git push` prints a benign `RemoteException` banner in PowerShell (stderr redirect of the
  `To https://...` line) — the push still succeeds; check for `master -> master`.
- Detached-tile windows have their own renderer instance; axis-hysteresis state is per-renderer.

## 9. Suggested next-session checks

1. `git log --oneline -5` and `git status` to confirm HEAD and clean tree.
2. Confirm version in `src/PCGauger/PCGauger.csproj` before any bump.
3. If releasing, follow the pipeline in §4 and verify with `gh release view`.
