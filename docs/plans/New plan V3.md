\# feat: PCGauger — Windows-Native System Metrics Dashboard

---

\*\*Revised:\*\* 2026-07-18 (v3 — scoped to a buildable v1)

---

\## 0.1 Status (updated 2026-07-18)

| Chunk | Scope | Status | Notes |
|-------|-------|--------|-------|
| Chunk 1 | CPU + RAM, real GUI, 60s history, top-process | Done | Committed 048194a. Live: CPU ~50% @ 3701MHz/12c, RAM 37%. Drag/resize works. |
| Chunk 1.5 | GPU, Disk, Network, adaptive grid, detachable tiles | Mostly done | Committed 4dfbc08 (GPU/Disk/grid/detach) + 27ebcfb (grab-handle UX) + 8358834 (detach fix). Network deferred by user choice — not built yet. |
| Chunk 2 | Kiosk mode, config persistence, themes, auto-start | Not started | |
| Chunk 3 | Elevated sensor helper (temp/fan/voltage/SMART) | Not started | Separate initiative; out of scope until decided. |

**Chunk 1.5 remaining:** NetworkProvider (GetIfTable2/GetIfEntry2, item 11) is the only unbuilt item. Items 12 (adaptive grid) and 13 (top-process-by-GPU) are done. Detach UX (grab handle to detach, click to re-attach) is working and verified.
\## 0. What changed in this revision



You asked for three things, and they simplify the plan a lot:



1\. \*\*No admin-requiring code in v1 at all.\*\* That removes LibreHardwareMonitor, temperatures, fan RPM, voltages, and SMART entirely from scope for now — not deferred-with-a-plan like in v2, just not designed yet. If you want that later, it's a separate initiative with its own elevated-process architecture (see v2 for that design if you ever want it back).

2\. \*\*Standalone app, no client/server — confirmed, and simplified further.\*\* v2 had grown a "kiosk mode, auto-detect the mini display, fullscreen" requirement. That's still useful eventually, but it's not needed to get CPU/RAM on screen, so it's pushed to a later chunk. v1 is just a normal app window, like pcGauge is — you size it and drag it to the mini display yourself.

3\. \*\*Ordered build chunks\*\*, so you can get something real on screen fast and expand from there.



\---



\## 1. V1 Scope



\*\*In scope:\*\*

\- CPU metrics (list above)

\- RAM metrics (list above)

\- A real, polished GUI — not a placeholder — since that's explicitly the point of chunk 1

\- Standalone single `.exe`, `asInvoker` manifest, no elevation anywhere

\- Normal (non-fullscreen) window, reasonably sized for a small display, manually positioned by you



\*\*Explicitly not in v1 (not even stubbed out):\*\*

\- GPU, Disk, Network, System metrics → v1.5

\- Temperatures, fan RPM, voltages, SMART → out of scope until you decide you want the admin-gated helper process design

\- Client/server, remote monitoring, Android companion → permanent non-goal, not a phase

\- Auto display-detection / fullscreen kiosk mode → later chunk, after there's enough content to fill a screen

\- Themes beyond one solid default → later chunk



\---



\## 2. Tech Approach (unchanged from v2, still the right call)



\- WinForms host + SkiaSharp for rendering (GPU-backed surface, not software raster — keeps CPU usage low, which matters since this runs alongside games)

\- `IMetricProvider` interface with an immutable snapshot published on a timer, even though v1 only has two providers — worth doing now so v1.5 slots in without a rewrite



```csharp

public interface IMetricProvider

{

&#x20;   void Update(TimeSpan elapsed);

&#x20;   IEnumerable<Metric> GetMetrics();

}

```



\- Refresh rates: CPU 250ms, RAM 500ms (same reasoning as before — CPU is the fastest-moving number, RAM barely needs faster than 500ms)



\---



\## 3. Build Order



\### Chunk 1 — V1: CPU + RAM with a real GUI



Goal: something you'd actually want running on the display, even before GPU/Disk/Network exist.



1\. \*\*Solution scaffold\*\* — WinForms project, `asInvoker` manifest, single project (no separate helper process to wire up yet).

2\. \*\*Metrics infrastructure\*\* — `IMetricProvider`, a snapshot object, a polling loop on a `System.Threading.Timer` (or similar), fault isolation so one provider throwing doesn't take down the window.

3\. \*\*CpuProvider\*\* — `GetSystemTimes` for aggregate %, `CallNtPowerInformation(ProcessorInformation)` for per-core frequency and (via `NtQuerySystemInformation(SystemProcessorPerformanceInformation)`) per-core usage, `Environment.ProcessorCount` for core/thread counts.

4\. \*\*MemoryProvider\*\* — `GlobalMemoryStatusEx` for total/used/free RAM and pagefile/committed usage.

5\. \*\*Dashboard renderer, first pass\*\* — SkiaSharp surface, two tiles: a CPU tile (usage bar + sparkline + clock speed + core count) and a RAM tile (usage bar + sparkline + used/total). This is where "nice working GUI" actually gets decided — worth spending real time on layout, typography, and one good theme before adding more metrics.

6\. \*\*60-second rolling history buffer\*\* — shared utility both tiles use for their sparklines; build it generically now since GPU/Disk/Network will need the same thing.

7\. \*\*Top process by CPU / top process by RAM\*\* — optional stretch inside chunk 1, cheap once you're already polling; a nice differentiator to have working before you move on.

8\. \*\*Manual smoke test on the actual mini display\*\* — confirm the window looks right at your display's real resolution before calling v1 done. This is the point to catch DPI/scaling weirdness early, not after four more providers are built on top of it.



\*\*Chunk 1 is done when:\*\* CPU and RAM tiles are live, updating at their target refresh rates, look good on the actual hardware, and idle CPU/memory overhead of the app itself is negligible.



\### Chunk 1.5 — Expand metrics: GPU, Disk, Network



9\. \*\*GpuProvider\*\* — PDH `\\GPU Engine(\*)\\Utilization Percentage` summed per adapter across engine types, `IDXGIAdapter3::QueryVideoMemoryInfo` for VRAM. No admin required for either. Test against whatever GPU you actually have; if you ever add a second machine with a different vendor, validate there too — the `GPU Engine` counter aggregation is the one part of this that's known to behave inconsistently across vendors.

10\. \*\*StorageProvider\*\* — `GetDiskFreeSpaceEx` for capacity, PDH `PhysicalDisk`/`LogicalDisk` counters (`Disk Bytes/sec`, `Avg. Disk Queue Length`) for activity.

11\. \*\*NetworkProvider\*\* — `GetIfTable2`/`GetIfEntry2`, delta between polls for throughput; auto-pick the adapter on the default route.

12\. \*\*Layout engine, second pass\*\* — now that there are 5 tiles instead of 2, this is the point to build an actual adaptive grid rather than hand-placing tiles.

13\. \*\*Top process, extended\*\* — apply the same pattern from chunk 1 to GPU usage now that GpuProvider exists.



\*\*Chunk 1.5 is done when:\*\* all five core metric groups are live with the same polish level as chunk 1.



\### Chunk 2 — Polish (after 1.5, order flexible)



\- Auto display-detection + fullscreen kiosk mode on the target display, with manual override

\- Config persistence (`%LOCALAPPDATA%\\PCGauger\\config.json` — theme, layout, window position)

\- Additional themes, threshold-based coloring (tile shifts color near a usage/temp threshold you set)

\- Optional auto-start with Windows (`HKCU\\...\\Run`, still no admin)



\### Chunk 3 — Only if you decide you want it later



\- Elevated sensor helper for temperature/fan/voltage/SMART, isolated in its own process so the main app stays unprivileged. This is a meaningfully bigger scope increase (separate exe, driver dependency, named-pipe IPC, AV-flagging risk to manage) — worth treating as its own project rather than folding into v1.5 or v2.



\---



\## 4. Notes carried over from earlier research (still relevant)



\- pcGauge, which you're using as the reference model for "standalone app," is itself just a normal desktop app with draggable tiles — not a server, not elevated, not fullscreen-forced. Chunk 1's plain window matches that model well.

\- Keep the provider/snapshot separation from the start even though v1 only has two providers — it's the thing that makes chunk 1.5 an addition instead of a rewrite.

