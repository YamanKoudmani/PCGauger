# PCGauger

A lightweight, Windows-native system metrics dashboard — built to sit on a small secondary display next to your main screen and show live CPU, GPU, RAM, disk, and network stats without getting in the way of games or requiring admin rights.

Rendered with SkiaSharp on a WinForms host. Single `.exe`, runs unprivileged (`asInvoker`), negligible idle overhead.

<img width="957" height="617" alt="image" src="https://github.com/user-attachments/assets/60ad9c72-6dc3-4b2f-aa14-58d0e1577256" />

## Features

### Metrics

| Tile | What it shows | Source |
|------|---------------|--------|
| **CPU** | Usage %, per-core clock speeds, core/thread count, top process by CPU | `GetSystemTimes`, `CallNtPowerInformation`, `NtQuerySystemInformation` |
| **RAM** | Used/total memory, committed/pagefile usage, top process by RAM | `GlobalMemoryStatusEx` |
| **GPU** | Utilization %, VRAM usage, top process by GPU | Per-adapter PDH `GPU Engine` counters (matched by LUID), `IDXGIAdapter3::QueryVideoMemoryInfo` |
| **Disk** | Drive capacity, read/write activity | `GetDiskFreeSpaceEx`, per-volume PDH `LogicalDisk` counters |
| **Network** | Upload/download throughput | `GetIfTable2` / `GetIfEntry2`, per-adapter; defaults to the adapter on the default route |

No admin, no drivers, no background services — everything comes from unprivileged Win32/PDH APIs.

### Multi-device tiles

- **Multiple disks, GPUs, and NICs** — add as many Disk, GPU, and Network tiles as you have devices, from the Tiles section of the global settings pane ("＋ Add" per kind).
- **Per-tile device selector** — each Disk/GPU/Network tile shows its device in the header and can switch devices from its settings pane. Devices already shown on another tile are marked in the list.
- **Removable tiles** — any Disk/GPU/Network tile can be removed from its settings pane; its settings are remembered if you re-add it later.
- **Hotplug aware** — if a device disappears (USB drive, disabled adapter), its tile shows a calm "Device unavailable" state and resumes when the device returns; you can re-point the tile at another device anytime.

### Tiles & layout

- **Adaptive grid** that reflows as tiles are added, removed, or resized.
- **Detachable tiles** — grab a tile's handle to pop it out into its own always-on-top window; click to re-attach. Detached positions are remembered.
- **Drag-to-reorder** with a live ghost and insertion indicator; your order persists across sessions.
- **Per-tile customization** — each tile has its own settings gear controlling:
  - Accent color override
  - Title, big value, usage bar, sparkline, and secondary line visibility (each independently toggleable)
- **Sparklines** backed by a rolling history buffer with a configurable global time window (default 5 minutes).

### Appearance & behavior

- **Three themes**: Midnight (default dark), Obsidian (OLED true black), Daybreak (light). Swaps apply instantly.
- **Threshold alerts** — tiles shift color when usage crosses a configurable percentage (50–100%).
- **Units control** — adaptive auto-scaling or fixed MB/GB; configurable decimal precision.
- **Kiosk mode** — fullscreen, auto-detects the mini display panel.
- **Always-on-top** toggle (applies to detached tile windows too).
- **Launch at startup** via `HKCU\...\Run` — still no elevation required.
- Footer status bar with a global settings pane.

### Persistence

All settings live in `%LOCALAPPDATA%\PCGauger\config.json` — theme, units, thresholds, window bounds, tile instances and order, per-tile settings (keyed per device, e.g. `Disk:C:`), and detached window positions. Saves are atomic (temp file + move), a missing or corrupt file falls back to defaults without crashing, and v1 configs migrate automatically on first launch.

### Architecture

- `IMetricProvider` interface with immutable snapshots published on a polling timer — one provider throwing can't take down the window.
- Per-metric refresh rates (CPU 250 ms, RAM 500 ms) tuned to how fast each number actually moves.
- GPU-backed SkiaSharp surface keeps the app's own CPU footprint low, since it's meant to run alongside games.

## Requirements

- Windows 10 version 2004 (19041) or later
- .NET 8 runtime (desktop) — or build self-contained

## Build & run

```powershell
dotnet run --project src/PCGauger
```

Single-file publish:

```powershell
dotnet publish src/PCGauger -c Release -r win-x64 --self-contained
```

## Roadmap

Possible future work (tracked in `docs/plans/`): an optional elevated sensor helper process for temperatures, fan RPM, voltages, and SMART data — kept isolated in its own exe so the main app stays unprivileged.

