# feat: Windows-Native 5" Display System Metrics Dashboard (PCGauger)

**Created:** 2026-07-18
**Type:** feat (greenfield)
**Origin:** User request — "tiny 5in display for my PC that displays system metrics like Trigone Remote System Monitor / pcGauge. Windows native, no Android client. All standard stats auto-grabbed."

---

## 1. Problem Frame

The user bought a 5-inch (800×480) HDMI secondary display for their Windows PC and wants it to show live system telemetry — the same kind of data that **Trigone Remote System Monitor** and **pcGauge** surface. Both reference tools are built around a *Windows server + remote client* model (Trigone = Windows server broadcasting over TCP/port 19150 to an Android app; pcGauge = similar local/remote pairing). The user explicitly does **not** want that: no Android client, no network server, no second machine. They want a single Windows app that renders directly onto the little HDMI monitor.

The goal is a **Windows-native, single-process dashboard** that:
- Automatically detects and targets the 5" display (800×480).
- Shows the standard stat set out of the box: CPU usage, GPU usage, RAM, VRAM, storage usage/I-O, network up/down, plus temperatures and clocks where available.
- Requires no manual configuration of sensors — everything is grabbed automatically on launch.
- Looks like the gauge/dashboard style of Trigone/pcGauge.

### Research Summary: How the Reference Tools Work

**Trigone Remote System Monitor**
- Two-part suite: a *Windows server* (`.NET Framework 4.x`) that reads hardware sensors and broadcasts over the LAN (default TCP port **19150**), and an *Android/BlackBerry client* that renders the data.
- Server reads: temperatures (CPU/cores, GPU, motherboard, HDD), CPU/GPU load, CPU/GPU frequencies, RAM/swap + video memory, voltages, SATA/NVMe SSD info, physical disk R/W speed, fan speeds (+ control), network up/down, logical disk usage, FPS.
- Sensor access is local to the Windows box; the Android app is purely a renderer over the network.
- **Key insight for us:** the valuable part is the *Windows-side sensor collection*. We drop the network broadcast and the Android client entirely and render locally.

**pcGauge**
- Similar architecture: a local Windows data source feeding a display surface. The "gauge" aesthetic (radial/needle gauges, bar graphs, multi-graph history) is the visual language the user wants to emulate.
- Emphasizes auto-discovery of hardware and a customizable dashboard of gauge widgets.

**Existing open-source native precedents (validated approach):**
- `rickbme/Memory-Monitor` — .NET 8 WinForms app for a 1920×480 strip display; auto-detects the display, circular gauges, uses **LibreHardwareMonitor** + **NVML/ADL** interop, system tray, two display modes. Closest architectural match.
- `Allain-afk/PC_Monitoring` — .NET 8 sidecar wrapping **LibreHardwareMonitorLib** + WMI + WDDM perf counters, multi-tier CPU temp fallback ladder.
- `saqibzahoor-dev/sysmonitor` — Tauri/Rust + **LibreHardwareMonitorLib** C# sidecar; shows the full stat taxonomy we want.
- `quyennv.com/sysmonitor` — raw Win32 + GDI+ single EXE; shows the low-level API map (NtQuerySystemInformation, GetIfTable2, GlobalMemoryStatusEx, DXGI).

**Conclusion:** The proven, lowest-risk path is **.NET 8 + WinForms + LibreHardwareMonitorLib** for sensor collection, with a custom gauge renderer for the 800×480 display. This matches what the user named ("pcGauge") and what the reference tools actually do under the hood.

---

## 2. Key Technical Decisions

### KTD-1: Language / UI Framework — .NET 8 WinForms
- **Decision:** C# / .NET 8 / Windows Forms (not WPF, not a web stack, not C++).
- **Rationale:** WinForms is the lightest mainstream way to get a borderless, always-on-top, DPI-aware window pinned to a specific screen with a custom GDI+/owner-draw gauge renderer. LibreHardwareMonitorLib targets net8.0 directly. Memory-Monitor proves this exact stack works for mini-displays. Avoids the heavier WPF compositor and avoids shipping a browser engine (Tauri/Electron) for a 5" low-power panel.
- **Alternatives considered:** WPF (ricter XAML but heavier, DPI quirks on tiny panels), Tauri/Rust (excellent but adds a web frontend + Rust toolchain for little gain on a fixed native display), raw Win32/GDI+ (max performance, but slow to build the gauge UI).
- **Trade-off:** WinForms is "old" but ideal here; we use owner-draw (not designer controls) for the gauges.

### KTD-2: Sensor Collection — LibreHardwareMonitorLib as primary, Windows APIs as fallback
- **Decision:** Wrap **LibreHardwareMonitorLib (0.9.6+, net8.0)** as the single source for CPU/GPU/RAM/VRAM/temps/fans/storage/network. Supplement with native Windows APIs only where LHM is weak:
  - Network throughput: LHM network sensors *or* `GetIfTable2` (IP Helper) byte-counter deltas (more reliable for up/down rates).
  - Disk I/O throughput: LHM storage sensors *or* `DeviceIoControl` / Performance Counters.
  - Logical disk free space: `DriveInfo` (.NET) — simpler and always correct.
- **Rationale:** LHM already does the cross-vendor (Intel/AMD/NVIDIA) heavy lifting that Trigone/pcGauge rely on, including the kernel driver for ring-0 thermal sensors. Re-implementing that is pointless.
- **Admin requirement:** CPU/GPU *temperature* needs the LHM kernel driver (WinRing0), which requires **elevated** privileges. The app will request admin via an `app.manifest` (`requireAdministrator`) so temps "just work." Usage/load/clocks/VRAM/network/disk free space work fine non-elevated; the plan must degrade gracefully (show `—` for temp) if not elevated rather than crash.

### KTD-3: Display Targeting — auto-detect 800×480 (and similar) secondary screen
- **Decision:** On startup, enumerate `Screen.AllScreens`. Pick the screen whose bounds best match a small panel (height ≤ ~600 and width ≤ ~1000, or explicitly 800×480). If found, move the borderless form onto it and size to the full panel. If not found, fall back to a normal window on the primary display (so the app is still usable during dev / before the panel is connected).
- **Rationale:** Matches Memory-Monitor's "auto-detect the mini display" behavior; no manual screen selection needed.
- **DPI:** Set `SetProcessDPIAware()` / `PerMonitorV2` awareness so 800×480 renders crisp, not blurry/oversized.

### KTD-4: Rendering — owner-draw gauge + bar controls, 1 Hz refresh
- **Decision:** Build lightweight custom controls (no third-party chart lib):
  - `GaugeControl` — radial/arc gauge with needle + value text + label (Trigone/pcGauge style).
  - `BarPanel` — horizontal bar with history sparkline (optional).
  - A main `DashboardForm` that lays out a grid of gauges sized for 800×480 (e.g., 2 rows × 3–4 columns, or a single row of large gauges).
- **Refresh:** A `System.Timers.Timer` at **1000 ms** drives `computer.Accept(visitor)` (LHM update) + network/disk delta calc, then `Invalidate()` the form. UI thread only reads latest values — no blocking sensor calls on the paint path.
- **Rationale:** 1 Hz matches Trigone's feel and keeps CPU footprint tiny (the user is likely gaming; the monitor must not steal resources).

### KTD-5: Stats taxonomy (auto-grabbed, no config)
The dashboard shows, automatically, the following — mapped to LHM sensor types:

| Stat | Source (LHM) | Fallback |
|------|--------------|----------|
| CPU usage % | `SensorType.Load` "CPU Total" | `PerformanceCounter` |
| CPU temp | `SensorType.Temperature` "CPU Package" | WMI thermal zone / `—` |
| CPU clock | `SensorType.Clock` | — |
| GPU usage % | GPU `Load` "GPU Core" / "D3D" | WDDM perf counter |
| GPU temp | GPU `Temperature` | `—` |
| GPU VRAM used/total | GPU `Data` / `SmallData` (memory) | NVML |
| RAM used/total | `SensorType.Data` (memory) | `GlobalMemoryStatusEx` |
| Disk usage % per drive | `DriveInfo` free space | — |
| Disk I/O R/W | Storage `Throughput` | `GetIfTable2`-style / PDH |
| Network up/down | Network `Throughput` | `GetIfTable2` byte deltas |
| Fan speed (if present) | `SensorType.Fan` | — |
| Date / time | system clock | — |

### KTD-6: Packaging & launch
- **Decision:** Single self-contained `.exe` (`.NET 8` `PublishSingleFile`, `win-x64`, `Trimmed`) + a WiX or Inno Setup installer optional. Add to `shell:startup` so it boots with Windows and lands on the panel automatically.
- **Rationale:** "Set and forget" on a secondary panel. Matches the user's "tiny display for my PC" intent.

---

## 3. Scope Boundaries

### In scope
- Windows 10/11 x64 only.
- Single-process native dashboard rendering to the 5" HDMI panel.
- Auto-collection of the stat set in KTD-5.
- Auto display detection + fallback window.
- Gauge + (optional) bar visual modes.
- System tray icon with: show/hide, display-mode toggle, exit, run-at-startup.
- Admin-elevated launch for full sensor access, graceful degradation otherwise.

### Out of scope (deferred)
- Android / remote client (explicitly not wanted).
- Network broadcast server (Trigone-style) — local only.
- Fan *control* (write-back) — read-only display. (Trigone can control fans; we show RPM only.)
- Theming editor / user-customizable widget layout (v1 ships a fixed, good-looking layout; customization is a later iteration).
- FPS / game-detection overlay (can be added later via PresentMon; not in v1).
- macOS / Linux builds.

---

## 4. High-Level Technical Design

```
                +-----------------------------+
                |        DashboardForm         |  borderless, topmost,
                |   (800x480, owner-draw)      |  pinned to mini-display
                +--------------+--------------+
                               | 1 Hz timer (UI thread)
                               v
                +-----------------------------+
                |      MetricsCollector        |  owns the update loop
                |  - computer.Accept(visitor)  |
                |  - network byte-delta calc   |
                |  - disk free-space snapshot  |
                +--------------+--------------+
                               | reads
                               v
   +-------------------+   +-------------------+   +----------------------+
   | LibreHardwareMonitor| | Windows APIs       | | .NET BCL             |
   | (CPU/GPU/RAM/      | | GetIfTable2 (net), | | DriveInfo (disk free),|
   |  VRAM/temp/fan/    | | PDH / DeviceIoCtrl | | DateTime (clock)      |
   |  storage/net)      | | (disk I/O)         | |                      |
   +-------------------+   +-------------------+   +----------------------+
                               ^
                               | kernel driver (WinRing0) — needs admin
                +-----------------------------+
                | app.manifest (requireAdmin) |
                +-----------------------------+
```

**Data flow:** `MetricsCollector` runs the LHM visitor + native deltas on a 1 Hz timer, writes results into a plain `MetricsSnapshot` struct. `DashboardForm.Paint` reads the latest snapshot and draws gauges. No sensor I/O on the paint path.

---

## 5. Implementation Units

### U1 — Project scaffold & manifest
Create the `PCGauger` solution: a .NET 8 WinForms project (`net8.0-windows`), `app.manifest` with `requireAdministrator` + DPI-aware declaration, `app.manifest`/`launchSettings`, and reference `LibreHardwareMonitorLib` (NuGet). Add a `MetricsSnapshot` model class.
- **Files:** `PCGauger/PCGauger.csproj`, `PCGauger/app.manifest`, `PCGauger/Models/MetricsSnapshot.cs`, `PCGauger/Program.cs`
- **Tests:** `PCGauger.Tests/ScaffoldTests.cs` — assert app builds, `MetricsSnapshot` defaults are sane (all zeros / null, not NaN).

### U2 — LibreHardwareMonitor wrapper (HardwareMonitorService)
Implement `HardwareMonitorService` that constructs `Computer` with all hardware enabled, `Open()`s it, and exposes an `Update()` using the visitor pattern. Provide typed accessors: `GetCpuLoad()`, `GetCpuTemp()`, `GetCpuClock()`, `GetGpuLoad()`, `GetGpuTemp()`, `GetGpuVramUsedTotal()`, `GetRamUsedTotal()`, `GetFanRpm()`, `GetStorageTemps()`, plus a raw `GetAllSensors()` for debugging.
- **Files:** `PCGauger/Services/HardwareMonitorService.cs`, `PCGauger/Services/UpdateVisitor.cs`
- **Tests:** `HardwareMonitorServiceTests.cs` — with a mock/fake `Computer`, verify visitor traversal calls `Update()` on each hardware + subhardware and that accessors return the sensor value by name/type. (Real hardware access is covered manually, not in unit tests.)

### U3 — Network & disk throughput collectors (native fallback)
Implement `NetworkMonitor` using `GetIfTable2` (P/Invoke IP Helper) to compute per-adapter up/down bytes/sec via delta between ticks; pick the active adapter (non-loopback, operational). Implement `DiskMonitor` for per-drive free space (`DriveInfo`) and I/O throughput (PDH/`DeviceIoControl` counters) aggregated across fixed drives.
- **Files:** `PCGauger/Services/NetworkMonitor.cs`, `PCGauger/Services/DiskMonitor.cs`, `PCGauger/Native/IpHelper.cs` (P/Invoke)
- **Tests:** `NetworkMonitorTests.cs` — given two synthetic byte snapshots 1s apart, assert computed rate = delta/sec. `DiskMonitorTests.cs` — assert `DriveInfo` free-space math (used = total − free) and that only `Fixed` drives are counted.

### U4 — MetricsCollector orchestration
Tie U2+U3 into a single `MetricsCollector` with a 1 Hz `Update()` that fills a `MetricsSnapshot`. Handles missing data gracefully (null/—) so a disabled sensor never throws. Single owner of the LHM update cadence.
- **Files:** `PCGauger/Services/MetricsCollector.cs`
- **Tests:** `MetricsCollectorTests.cs` — with faked hardware + network services, assert snapshot fields populate correctly and that a missing temp sensor yields `null` (not exception).

### U5 — Display detection & DashboardForm shell
Implement `DisplayManager` that enumerates `Screen.AllScreens`, selects the mini panel (height ≤ 600, width ≤ 1000, prefer exact 800×480), and returns its `Bounds`. `DashboardForm` is borderless, `TopMost`, DPI-aware, sized to the chosen screen, and falls back to a primary-display window when no mini panel is found. Hosts the 1 Hz timer that calls `MetricsCollector.Update()` then `Invalidate()`.
- **Files:** `PCGauger/UI/DisplayManager.cs`, `PCGauger/UI/DashboardForm.cs`
- **Tests:** `DisplayManagerTests.cs` — given a fake `Screen[]` (one 800×480 + primary), assert the 800×480 is selected; given only a primary, assert fallback returns primary bounds.

### U6 — Gauge & bar rendering controls
Implement `GaugeControl` (radial arc + needle + center value + label + optional sub-label for temp) and `BarPanel` (horizontal bar + rolling sparkline). Both are owner-draw `Control`s driven by a `double Value` (0–1 or 0–100) + text. Style tuned for 800×480 (large, legible, dark background, Trigone/pcGauge-like palette).
- **Files:** `PCGauger/UI/GaugeControl.cs`, `PCGauger/UI/BarPanel.cs`
- **Tests:** `GaugeControlTests.cs` — assert value clamping (negative → 0, >100 → 100) and that `Paint` does not throw with null text.

### U7 — Dashboard layout & stat binding
Arrange the gauges on `DashboardForm` for 800×480: a grid of CPU, GPU, RAM, VRAM, Disk, Network gauges (+ CPU/GPU temp as sub-labels), date/time in a corner. Bind each gauge to its `MetricsSnapshot` field every tick. Add an optional second "bar graph" mode toggle.
- **Files:** `PCGauger/UI/DashboardForm.cs` (layout), `PCGauger/UI/DashboardLayout.cs` (positions)
- **Tests:** `DashboardLayoutTests.cs` — assert all six required gauges are present in the layout and bound to distinct snapshot fields; assert layout fits within 800×480 bounds.

### U8 — System tray & startup
Add a `NotifyIcon` with context menu: Show/Hide, Toggle display mode, Run at startup (toggles `HKCU\...\Run` registry key), Exit. Double-click tray toggles visibility.
- **Files:** `PCGauger/UI/TrayIconManager.cs`
- **Tests:** `TrayIconManagerTests.cs` — assert startup registry key is set/cleared correctly (use a redirected registry view or mock).

### U9 — Publish & installer
Configure `PublishSingleFile` + `win-x64` + trim in csproj; produce a self-contained exe. Optional Inno Setup / WiX script for a one-click installer that registers startup. Document manual "add to startup" steps.
- **Files:** `PCGauger/PCGauger.csproj` (publish props), `installer/PCGauger.iss` (optional)
- **Tests:** Build-only verification (publish succeeds, exe launches and lands on the panel).

---

## 6. Dependencies & Sequencing

```
U1 (scaffold) ──► U2 (LHM wrapper) ──┐
                                    ├─► U4 (collector) ──► U5 (display+form) ──► U6 (controls) ──► U7 (layout) ──► U8 (tray) ──► U9 (publish)
U1 ──► U3 (net/disk) ──────────────┘
```
- U2 and U3 can run in parallel after U1.
- U4 depends on U2+U3.
- U5–U8 are sequential UI work building on U4.
- U9 is independent packaging at the end.

**External dependency:** `LibreHardwareMonitorLib` (NuGet, MPL-2.0 — compatible with a closed or open binary). No other runtime deps.

---

## 7. Verification Strategy

- **Unit (automated):** U1–U8 each ship a test file (see per-unit). Focus on clamping, null-safety, math (network delta, disk free), display selection, and layout-fit — all testable without real hardware via fakes.
- **Integration (manual, on the user's machine):**
  1. Build + run elevated → app appears on the 5" panel at 800×480, full-bleed.
  2. All six gauges show live, changing values within a few seconds (no manual config).
  3. CPU/GPU temp visible (confirms admin/kernel driver working); if run non-elevated, temp shows `—` and app does not crash.
  4. Stress test: launch a game / CPU burner → CPU/GPU/VRAM/Network gauges move; confirm app CPU footprint stays < ~2–3% (it must not hurt gaming).
  5. Disconnect panel → app falls back to a primary-display window; reconnect → re-detects (restart).
  6. Tray: hide/show, toggle mode, toggle startup (reboot confirms auto-launch onto panel).
- **Honest degradation:** any unsupported sensor (e.g., fan RPM on a board that doesn't expose it) shows `—`, never a fake zero.

---

## 8. Risks & Mitigations

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Microsoft Vulnerable Driver Blocklist blocks WinRing0 → no CPU temp | Medium | App still works; temp shows `—`; document Core Isolation toggle. LHM also has ACPI/perf-counter fallbacks. |
| 5" panel reports non-800×480 (scaled/DPI) | Medium | DPI-aware + bounds-based detection (≤600×≤1000), not hardcoded 800×480. |
| Admin prompt annoys user | Low | Required only for temps; usage/load/net/disk work without it. Document. |
| GPU vendor gap (Intel iGPU VRAM) | Low | iGPU reports shared memory; show "shared w/ CPU" like PC_Monitoring. |
| App steals gaming resources | Low | 1 Hz, owner-draw, no web engine; verify <3% CPU in integration test. |

---

## 9. Assumptions

- The 5" display connects as a standard **HDMI secondary monitor** (Windows sees it as a `Screen`), not a WinUSB-only device (BeadaPanel-style). If it's WinUSB-only, rendering would require the vendor SDK instead of a normal window — out of scope; user should use the HDMI/graphics-driver mode.
- Windows 10/11 x64 with .NET 8 runtime (or self-contained exe bundles it).
- User is comfortable running the app elevated for full sensor access.

---

## 10. Sources & Research

- Trigone Remote System Monitor — https://www.trigonesoft.com/ (server + Android client; port 19150; sensor taxonomy)
- pcGauge — gauge/dashboard aesthetic reference
- LibreHardwareMonitorLib — https://www.nuget.org/packages/LibreHardwareMonitorLib/ (net8.0; CPU/GPU/RAM/VRAM/temp/fan/storage/network)
- rickbme/Memory-Monitor — .NET 8 WinForms mini-display gauge app (architectural precedent)
- Allain-afk/PC_Monitoring — LHM + WMI + WDDM sidecar, multi-tier temp fallback
- saqibzahoor-dev/sysmonitor — full stat taxonomy via LHM sidecar
- quyennv.com/sysmonitor — low-level Windows API map (NtQuerySystemInformation, GetIfTable2, GlobalMemoryStatusEx, DXGI)
- Waveshare / Elecrow 5" 800×480 HDMI LCD specs — confirms 800×480 panel standard
