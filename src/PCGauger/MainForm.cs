using System.Drawing;
using System.Windows.Forms;
using PCGauger.Infrastructure;
using PCGauger.Metrics;
using PCGauger.Metrics.Catalogs;
using PCGauger.Metrics.Providers;
using PCGauger.Rendering;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace PCGauger;

/// <summary>
/// Host window. Owns the SkiaSharp surface, the metric poller(s), and the
/// rolling history buffers. Tiles are laid out by an adaptive grid
/// (GridLayout.Compute) and can be detached into their own window.
///
/// The poller runs on a timer thread; rendering happens on the UI thread via
/// the SKControl Paint event, which reads the latest snapshots.
/// </summary>
public sealed class MainForm : Form
{
    private readonly HitTestSurface _surface;
    private readonly MetricPoller _poller;
    private readonly MetricPoller _memPoller;
    private readonly CpuProvider _cpu;
    private readonly MemoryProvider _mem;
    private readonly TopProcessProvider _top;
    private readonly TileRenderer _renderer;
    private Theme _theme;
    private readonly AppConfig _config;
    private readonly DiskCatalog _diskCatalog = new();
    private readonly GpuCatalog _gpuCatalog = new();
    private readonly NetworkCatalog _netCatalog = new();

    /// <summary>Per-tile runtime state: the provider, its histories, and (for
    /// multi-instance kinds) the catalog. Every tile — singleton or instance —
    /// has exactly one runtime so draw callbacks and the poller treat them
    /// uniformly.</summary>
    private sealed class TileRuntime
    {
        public Tile Tile = null!;
        public IMetricProvider Provider = null!;
        public RollingHistory HistA = null!;     // primary series (usage / read / down)
        public RollingHistory? HistB;            // secondary series (write / up); null for single-series kinds
        public IDeviceCatalog? Catalog;          // non-null for multi-instance kinds
        public DateTimeOffset LastPushedTick;    // last poller tick sampled into the histories
    }

    private readonly List<TileRuntime> _runtimes = new();
    private readonly Dictionary<string, TileRuntime> _runtimeByKey = new(); // ConfigKey -> runtime

    // Tiles currently shown in THIS window. Detaching removes one and opens a
    // DetachedTileForm; closing that form re-attaches it here. Disabling a tile
    // removes it from _tiles but keeps it in _allTiles (so it can be re-enabled).
    private readonly List<Tile> _tiles = new();
    private readonly List<Tile> _allTiles = new();
    private readonly List<DetachedTileForm> _detached = new();

    private readonly System.Windows.Forms.Timer _renderTimer;
    private Rectangle _floatingBounds; // last known non-kiosk bounds, for restore

    // --- Hover / pane interaction state ---
    private int _hoverHandle = -1;
    private int _hoverGear = -1;
    private Tile? _openPaneTile;
    private bool _hoverPaneClose;
    private int _hoverSwatch = -1;

    // Global settings pane state.
    private bool _globalPaneOpen;
    private bool _hoverFooterGear;
    private bool _hoverFooterClose;
    private bool _hoverGlobalClose;
    private int _globalHoverRow = -1;

    // Per-tile device dropdown state (owned here; the renderer reads it each frame).
    private readonly TileRenderer.DevicePaneState _devicePaneState = new();
    private bool _hoverDeviceRow;
    private bool _hoverRemove;

    // Global pane instance-aware device state (counts + add-picker).
    private readonly TileRenderer.GlobalPaneDeviceState _globalDeviceState = new();
    private int _hoverAddButton = -1;
    private int _hoverPickerItem = -1;

    // --- Unified tile drag gesture state (reorder inside / pop out outside) ---
    // DragState.None  : idle.
    // DragState.Armed : MouseDown on a grab handle, button held, no move yet.
    // DragState.Reorder : moved past threshold while cursor stays in client.
    // (Pop-out is handled by handing off to Detach; the session simply ends.)
    private enum DragState { None, Armed, Reorder }
    private DragState _dragState = DragState.None;
    private Tile? _dragTile;
    private Point _dragStart;     // client point where the press began
    private Point _dragGrabOffset; // cursor offset from the tile's top-left
    private int _dragInsertAt = -1; // target slot while reordering
    private Point _dragCursor;      // last cursor (client) while dragging
    private const int DragThreshold = 6;

    private readonly SplashForm? _splash;

    public MainForm(SplashForm? splash = null)
    {
        _splash = splash;
        _config = AppConfig.Load();

        Text = "PCGauger";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Color.Black;

        // Apply persisted theme + threshold before building anything.
        _theme = Theme.FromName(_config.ThemeName);
        Format.ValueDecimals = _config.ValueDecimals;
        TileRenderer.ThresholdEnabled = _config.ThresholdEnabled;
        TileRenderer.ThresholdPercent = _config.ThresholdPercent;

        // Restore window bounds (unless kiosk will take over on load).
        if (_config.WindowW > 0 && _config.WindowH > 0)
            _floatingBounds = new Rectangle(_config.WindowX, _config.WindowY, _config.WindowW, _config.WindowH);
        else
            _floatingBounds = new Rectangle(0, 0, 420, 560);

        _surface = new HitTestSurface
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
        };
        _surface.HitTestOverride = p =>
        {
            if (ClaimedHitTest(p) != 0) return 1; // HTCLIENT
            return 0;
        };
        _surface.PaintSurface += OnPaintSurface;
        _surface.MouseMove += OnSurfaceMouseMove;
        _surface.MouseDown += OnSurfaceMouseDown;
        _surface.MouseUp += OnSurfaceMouseUp;
        _surface.MouseLeave += OnSurfaceMouseLeave;
        Controls.Add(_surface);
        KeyPreview = true; // so Escape can cancel an in-progress drag

        // Apply the persisted graph time span to every history buffer (the
        // buffers are shared by the main grid and any detached windows).
        // Must run after _surface exists — ApplyGraphWindow invalidates it.
        ApplyGraphWindow(_config.GraphWindowSeconds);

        _cpu = new CpuProvider();
        _mem = new MemoryProvider();
        _top = new TopProcessProvider();
        _renderer = new TileRenderer(_theme);

        _poller = new MetricPoller(new IMetricProvider[] { _cpu, _top }, TimeSpan.FromMilliseconds(1000));
        _memPoller = new MetricPoller(new IMetricProvider[] { _mem }, TimeSpan.FromMilliseconds(1000));

        // v1 -> v2 migration (idempotent; never throws). After this, TileInstances
        // is non-empty and every created instance has a ConfigKey entry.
        MigrateConfigV1ToV2();

        // Build a runtime + tile for every persisted instance, in creation order.
        // Providers are constructed with deferResolution:true so NO device I/O
        // happens here — the form comes up instantly and devices resolve after.
        foreach (var configKey in _config.TileInstances)
        {
            try
            {
                if (!TryParseConfigKey(configKey, out var kind, out var deviceId)) continue;
                var rt = CreateInstanceTile(kind, deviceId);
                if (rt == null) continue;
                _config.Tile(configKey).ApplyTo(rt.Tile.Settings);
                _allTiles.Add(rt.Tile);
                if (_config.Tile(configKey).Enabled)
                    _tiles.Add(rt.Tile);
            }
            catch
            {
                // A single bad instance must not break the rest of startup.
            }
        }

        // Start the pollers ONLY after every provider has been registered via
        // _poller.Add / _memPoller.Add in the loop above. Starting earlier would
        // let a Tick (which runs device I/O) race a UI-thread Add under the lock
        // and intermittently hang startup.
        _poller.Start();
        _memPoller.Start();

        // Kick off asynchronous device resolution for every resolvable provider
        // now that the tiles exist. This runs off-thread with hard timeouts, so
        // the UI thread is never blocked on DXGI/DriveInfo/NIC enumeration.
        foreach (var rt in _runtimes)
            (rt.Provider as IAsyncResolvable)?.BeginResolve();
        ApplyTileOrder();

        ApplyWindowBounds();
        TopMost = _config.AlwaysOnTop;
        ApplyWindowBounds();

        // Apply persisted always-on-top to this window (detached forms read the
        // value when constructed later).
        TopMost = _config.AlwaysOnTop;

        // Apply persisted kiosk mode at startup (fill the mini panel if enabled).
        ApplyWindowBounds();
        // Re-assert once the window is shown, in case the mini panel wasn't
        // enumerated yet when the constructor ran.
        Shown += (_, _) =>
        {
            ApplyWindowBounds();
            // Ensure the accent policy is active now the handle is visible.
            WindowComposition.Apply(Handle, _theme);
            // The form is visible and painted — dismiss the loading splash
            // (covers the single-file runtime extraction pause on first run).
            // The splash runs its own loop on the UI thread, so close it
            // directly here (we are already on the UI thread).
            _splash?.SignalReady();
        };

        _renderTimer = new System.Windows.Forms.Timer { Interval = 33 };
        _renderTimer.Tick += (_, _) => _surface.Invalidate();
        Shown += (_, _) => _renderTimer.Start();

        // First run only: register a Start Menu shortcut pointing at this exe.
        // If the user later deletes it by hand we don't recreate it.
        if (!_config.StartMenuRegistered && Infrastructure.StartMenuShortcut.EnsureCreated())
        {
            _config.StartMenuRegistered = true;
            _config.Save();
        }

        // Use the exe's embedded icon for taskbar / alt-tab.
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? Icon; }
        catch { /* keep default icon */ }
    }

    // ---- Window bounds / kiosk ----

    private void ApplyWindowBounds()
    {
        if (_config.KioskMode && TryFindMiniPanel(out var screen))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = screen.Bounds; // fill the mini panel
        }
        else
        {
            var b = _floatingBounds;
            if (b.Width == 0 || b.Height == 0)
                StartPosition = FormStartPosition.CenterScreen;
            else
            {
                // Never open off-screen: if the saved position isn't on any
                // visible monitor (e.g. a disconnected second display), clamp it
                // onto the nearest screen so the window is always reachable.
                b = ClampToVisibleScreen(b);
                StartPosition = FormStartPosition.Manual;
                Bounds = b;
            }
        }
    }

    /// <summary>
    /// Returns <paramref name="b"/> unchanged when any part overlaps a visible
    /// screen; otherwise shifts it onto the nearest screen (top-left anchored
    /// within that screen's working area). Prevents the window from launching
    /// where no monitor exists — a common cause of "the app won't open".
    /// </summary>
    private static Rectangle ClampToVisibleScreen(Rectangle b)
    {
        // The window is reachable only if its top-left corner sits inside the
        // PRIMARY screen's working area. A saved position on a secondary
        // monitor the user isn't watching (or a now-disconnected head that
        // Windows still enumerates) passes an "any screen" test and parks
        // the window where it looks gone. Default to the primary screen
        // (the one with the taskbar) so the window is always on the
        // display the user is actually looking at.
        var wa = Screen.PrimaryScreen!.WorkingArea;
        if (wa.Contains(b.Location)) return b;

        int x = Math.Max(wa.Left, Math.Min(b.X, wa.Right - b.Width));
        int y = Math.Max(wa.Top, Math.Min(b.Y, wa.Bottom - b.Height));
        return new Rectangle(x, y, b.Width, b.Height);
    }

    private static bool TryFindMiniPanel(out Screen found)
    {
        // Prefer an exact 800x480 panel; otherwise any height<=600 && width<=1000.
        Screen? exact = null, fallback = null;
        foreach (var s in Screen.AllScreens)
        {
            int w = s.Bounds.Width, h = s.Bounds.Height;
            if (w <= 1000 && h <= 600)
            {
                if (w == 800 && h == 480) exact = s;
                else if (fallback == null) fallback = s;
            }
        }
        found = exact ?? fallback;
        return found != null;
    }

    private void SetKiosk(bool on)
    {
        // Remember the current floating position only when leaving it, so a
        // later restore returns to where the window actually was.
        if (on && !_config.KioskMode)
            _floatingBounds = Bounds;
        _config.KioskMode = on;
        _config.Save();
        ApplyWindowBounds();
        _surface.Invalidate();
    }

    // ---- Tile draw callbacks (bound to current snapshots) ----
    //
    // History sampling is gated on the poller tick: the metric snapshots only
    // change at 1 Hz, so pushing once per paint frame (~30/s) would store ~30
    // identical values per real sample — bloating the ring buffers and forcing
    // every sparkline through a ~1024-point decimation + spline build per frame.
    // Pushing once per tick keeps the graph shape identical (X is time-mapped)
    // while cutting points/frame ~17x and history allocations ~25-100x.
    private static void SampleHistory(TileRuntime rt, MetricPoller poller, double a, double? b = null)
    {
        if (poller.LastUpdate == rt.LastPushedTick) return;
        rt.LastPushedTick = poller.LastUpdate;
        rt.HistA.Push(a);
        if (b.HasValue) rt.HistB!.Push(b.Value);
    }

    private void DrawCpu(SKCanvas c, SKRect r, Tile tile)
    {
        var rt = _runtimeByKey[tile.ConfigKey];
        var snap = _poller.GetSnapshot(rt.Provider);
        double usage = MetricValue(snap, "cpu.aggregate");
        SampleHistory(rt, _poller, usage);
        _renderer.DrawCpuTile(c, r, tile.Settings, TilePalette.Resolve(tile.Kind, tile.Settings),
            usage, _cpu.CurrentMhz, _cpu.PhysicalCores, _cpu.LogicalProcessors, rt.HistA.DecimatedSnapshot(256), rt.HistA.Window,
            axisKey: tile.ConfigKey);
    }
    private void DrawRam(SKCanvas c, SKRect r, Tile tile)
    {
        var rt = _runtimeByKey[tile.ConfigKey];
        var snap = _memPoller.GetSnapshot(rt.Provider);
        double load = MetricValue(snap, "mem.load");
        SampleHistory(rt, _memPoller, load);
        _renderer.SetRamHistory(rt.HistA.DecimatedSnapshot(256), rt.HistA.Window);
        _renderer.DrawRamTile(c, r, tile.Settings, TilePalette.Resolve(tile.Kind, tile.Settings),
            load, _mem.UsedPhys, _mem.TotalPhys, axisKey: tile.ConfigKey);
    }
    private void DrawGpu(SKCanvas c, SKRect r, Tile tile)
    {
        var rt = _runtimeByKey[tile.ConfigKey];
        SyncDeviceState(tile, rt);
        if (!tile.DeviceAvailable)
        {
            _renderer.DrawUnavailableTile(c, r, tile.Settings, TilePalette.Resolve(tile.Kind, tile.Settings), tile.Kind, tile.Title, tile.DeviceDisplayName, tile.DeviceDisplayName);
            return;
        }
        var snap = _poller.GetSnapshot(rt.Provider);
        double util = MetricValue(snap, "gpu.util");
        SampleHistory(rt, _poller, util);
        _renderer.SetGpuHistory(rt.HistA.DecimatedSnapshot(256), rt.HistA.Window);
        _renderer.DrawGpuTile(c, r, tile.Settings, TilePalette.Resolve(tile.Kind, tile.Settings),
            util, ((GpuProvider)rt.Provider).VramUsed, ((GpuProvider)rt.Provider).VramBudget,
            deviceSubtitle: tile.IsDeviceSelectable ? tile.DeviceDisplayName : null, axisKey: tile.ConfigKey);
    }
    private void DrawDisk(SKCanvas c, SKRect r, Tile tile)
    {
        var rt = _runtimeByKey[tile.ConfigKey];
        SyncDeviceState(tile, rt);
        if (!tile.DeviceAvailable)
        {
            _renderer.DrawUnavailableTile(c, r, tile.Settings, TilePalette.Resolve(tile.Kind, tile.Settings), tile.Kind, tile.Title, tile.DeviceDisplayName, tile.DeviceDisplayName);
            return;
        }
        var snap = _poller.GetSnapshot(rt.Provider);
        double pct = MetricValue(snap, "disk.load");
        var disk = (StorageProvider)rt.Provider;
        SampleHistory(rt, _poller, disk.ReadBytesPerSec, disk.WriteBytesPerSec);
        _renderer.SetDiskHistory(rt.HistA.DecimatedSnapshot(256), rt.HistB!.DecimatedSnapshot(256), rt.HistA.Window);
        _renderer.DrawDiskTile(c, r, tile.Settings, TilePalette.Resolve(tile.Kind, tile.Settings),
            pct, disk.TotalBytes - disk.FreeBytes, disk.TotalBytes, disk.BytesPerSec,
            deviceSubtitle: tile.IsDeviceSelectable ? tile.DeviceDisplayName : null, axisKey: tile.ConfigKey);
    }
    private void DrawNet(SKCanvas c, SKRect r, Tile tile)
    {
        var rt = _runtimeByKey[tile.ConfigKey];
        SyncDeviceState(tile, rt);
        if (!tile.DeviceAvailable)
        {
            _renderer.DrawUnavailableTile(c, r, tile.Settings, TilePalette.Resolve(tile.Kind, tile.Settings), tile.Kind, tile.Title, tile.DeviceDisplayName, tile.DeviceDisplayName);
            return;
        }
        var snap = _poller.GetSnapshot(rt.Provider);
        double down = MetricValue(snap, "net.down");
        double up = MetricValue(snap, "net.up");
        string iface = TextValue(snap, "net.name");
        var net = (NetworkProvider)rt.Provider;
        SampleHistory(rt, _poller, down, up);
        _renderer.SetNetHistory(rt.HistA.DecimatedSnapshot(256), rt.HistB!.DecimatedSnapshot(256), rt.HistA.Window);
        _renderer.DrawNetworkTile(c, r, tile.Settings, TilePalette.Resolve(tile.Kind, tile.Settings),
            down, up, iface,
            deviceSubtitle: tile.IsDeviceSelectable ? tile.DeviceDisplayName : null, axisKey: tile.ConfigKey);
    }

    /// <summary>Mirrors the bound provider's availability + display name into the
    /// tile each frame (device labels can change, e.g. volume label).</summary>
    private static void SyncDeviceState(Tile tile, TileRuntime rt)
    {
        switch (rt.Provider)
        {
            case GpuProvider gpu:
                tile.DeviceAvailable = gpu.DeviceAvailable;
                if (!string.IsNullOrEmpty(gpu.AdapterName))
                    tile.DeviceDisplayName = gpu.AdapterName;
                break;
            case StorageProvider disk:
                tile.DeviceAvailable = disk.DeviceAvailable;
                break;
            case NetworkProvider net:
                tile.DeviceAvailable = net.DeviceAvailable;
                if (net.InterfaceName is { Length: > 0 } and not "-")
                    tile.DeviceDisplayName = net.InterfaceName;
                break;
        }
    }

    // ---- Layout geometry helpers ----
    private const int FooterHeight = 28;

    private SKRect ClientRect() => new(0, 0, ClientSize.Width, ClientSize.Height);

    private SKRect GridArea()
    {
        float gap = 12;
        float bottom = ClientSize.Height - FooterHeight;
        if (bottom < gap + 40) bottom = gap + 40;
        return new SKRect(gap, gap, ClientSize.Width - gap, bottom);
    }

    // Layout rects depend only on (client size, tile count) — tile REORDERS
    // don't affect them (rects are positional by index). Cache them and rebuild
    // only when one of those inputs changes, instead of allocating 3 fresh lists
    // on every paint frame AND every WM_NCHITTEST / MouseMove message.
    private int _layoutW = -1, _layoutH = -1, _layoutCount = -1;
    private List<SKRect> _rectsCache = new();
    private List<SKRect> _handlesCache = new();
    private List<SKRect> _gearsCache = new();

    private List<SKRect> TileRects()
    {
        int w = ClientSize.Width, h = ClientSize.Height, n = _tiles.Count;
        if (w != _layoutW || h != _layoutH || n != _layoutCount)
        {
            _layoutW = w; _layoutH = h; _layoutCount = n;
            const float gap = 12;
            _rectsCache = (List<SKRect>)GridLayout.Compute(GridArea(), n, gap);
            _handlesCache = _rectsCache.Select(TileRenderer.GrabHandleRect).ToList();
            _gearsCache = _rectsCache.Select(TileRenderer.GearRect).ToList();
        }
        return _rectsCache;
    }

    private List<SKRect> HandleRects() { TileRects(); return _handlesCache; }
    private List<SKRect> GearRects() { TileRects(); return _gearsCache; }

    /// <summary>The footer-gear hit rect: a 22px square in the status band, left of the close button.</summary>
    private SKRect FooterGearRect()
    {
        var c = FooterCloseRect();
        return new SKRect(c.Left - c.Width - 6, c.Top, c.Left - 6, c.Bottom);
    }

    /// <summary>The footer close hit rect: a 22px square at the far right of the status band.</summary>
    private SKRect FooterCloseRect()
    {
        float size = 22;
        float y = ClientSize.Height - FooterHeight + (FooterHeight - size) / 2f;
        return new SKRect(ClientSize.Width - size - 8, y, ClientSize.Width - 8, y + size);
    }

    private TileRenderer.PaneLayout? PaneLayoutFor(Tile tile, SKRect rect)
    {
        if (_openPaneTile != tile) return null;
        var v = new TileVisual(tile.Kind, tile.Settings, TilePalette.Resolve(tile.Kind, tile.Settings), _theme, false);
        return _renderer.ComputePaneLayout(rect, ClientRect(), v, tile, _devicePaneState);
    }

    private TileRenderer.GlobalPaneLayout? GlobalPaneLayout()
    {
        // Keep per-kind counts fresh (cheap; small lists) so the "Disk · 2"
        // labels and the picker's "already added" state stay accurate.
        _globalDeviceState.CpuCount = CountInstances(TileKind.Cpu);
        _globalDeviceState.RamCount = CountInstances(TileKind.Ram);
        _globalDeviceState.GpuCount = CountInstances(TileKind.Gpu);
        _globalDeviceState.DiskCount = CountInstances(TileKind.Disk);
        _globalDeviceState.NetworkCount = CountInstances(TileKind.Network);
        return _renderer.ComputeGlobalPaneLayout(ClientRect(), _globalDeviceState);
    }

    // ---- multi-instance device helpers ----

    private static int DeviceDropdownMax => 5; // mirrors TileRenderer.Devices PaneDeviceDropdownMax

    private void RefreshDevicePaneItems(Tile tile)
    {
        var items = new List<TileRenderer.DeviceItem>(GetDevicesFor(tile.Kind).Count);
        foreach (var d in GetDevicesFor(tile.Kind))
            items.Add(new TileRenderer.DeviceItem
            {
                Id = d.Id,
                DisplayName = d.DisplayName,
                Detail = d.Detail,
                Checked = d.Id == tile.InstanceId,
                Disabled = IsDeviceBound(tile.Kind, d.Id) && d.Id != tile.InstanceId,
            });
        _devicePaneState.Items = items;
    }

    private void ResetDevicePaneState()
    {
        _devicePaneState.DropdownOpen = false;
        _devicePaneState.ScrollIndex = 0;
        _devicePaneState.HoverIndex = -1;
        _devicePaneState.Items = Array.Empty<TileRenderer.DeviceItem>();
        _hoverDeviceRow = false;
        _hoverRemove = false;
    }

    private void OpenPicker(TileKind kind)
    {
        _globalDeviceState.ActivePickerKind = kind;
        _globalDeviceState.PickerScrollIndex = 0;
        _globalDeviceState.PickerHoverIndex = -1;
        RefreshPickerItems(kind);
    }

    private void RefreshPickerItems(TileKind kind)
    {
        var items = new List<TileRenderer.DeviceItem>(GetDevicesFor(kind).Count);
        foreach (var d in GetDevicesFor(kind))
            items.Add(new TileRenderer.DeviceItem
            {
                Id = d.Id,
                DisplayName = d.DisplayName,
                Detail = d.Detail,
                Checked = false,
                Disabled = IsDeviceBound(kind, d.Id),
            });
        _globalDeviceState.PickerItems = items;
    }

    private void ClosePicker()
    {
        _globalDeviceState.ActivePickerKind = null;
        _globalDeviceState.PickerScrollIndex = 0;
        _globalDeviceState.PickerHoverIndex = -1;
        _globalDeviceState.PickerItems = Array.Empty<TileRenderer.DeviceItem>();
        _hoverAddButton = -1;
        _hoverPickerItem = -1;
    }

    /// <summary>
    /// Hit-test used by both HitTestOverride and WndProc. Returns a non-zero
    /// Win32 hit code (HTCLIENT) when the point lies over a grab handle, a gear,
    /// the footer gear, or an open settings pane; otherwise 0.
    /// </summary>
    private int ClaimedHitTest(Point p)
    {
        foreach (var h in HandleRects())
            if (h.Contains(p.X, p.Y)) return 1;
        foreach (var g in GearRects())
            if (g.Contains(p.X, p.Y)) return 1;
        if (FooterGearRect().Contains(p.X, p.Y)) return 1;
        if (FooterCloseRect().Contains(p.X, p.Y)) return 1;

        if (_openPaneTile != null)
        {
            int idx = _tiles.IndexOf(_openPaneTile);
            if (idx >= 0 && TilePaneContains(_openPaneTile, TileRects()[idx], p)) return 1;
        }
        if (_globalPaneOpen && GlobalPaneContains(p)) return 1;
        return 0;
    }

    /// <summary>True when <paramref name="p"/> lies inside the open tile settings
    /// pane OR its device dropdown popover (which can extend past the pane card).
    /// Needed so a click on a dropdown item below the pane bottom is claimed as
    /// HTCLIENT and reaches <see cref="HandlePaneClick"/> instead of being
    /// dismissed by the non-client outside-click path.</summary>
    private bool TilePaneContains(Tile tile, SKRect rect, Point p)
    {
        var layout = PaneLayoutFor(tile, rect);
        if (layout == null) return false;
        if (layout.Pane.Contains(p.X, p.Y)) return true;
        if (tile.IsDeviceSelectable && _devicePaneState.DropdownOpen
            && !layout.DeviceDropdown.IsEmpty && layout.DeviceDropdown.Contains(p.X, p.Y)) return true;
        return false;
    }

    /// <summary>True when <paramref name="p"/> lies inside the open global settings
    /// pane OR its device picker popover (which can extend past the pane card).
    /// Same purpose as <see cref="TilePaneContains"/> for the global pane.</summary>
    private bool GlobalPaneContains(Point p)
    {
        var g = GlobalPaneLayout();
        if (g == null) return false;
        if (g.Pane.Contains(p.X, p.Y)) return true;
        if (_globalDeviceState.ActivePickerKind.HasValue
            && !g.Picker.IsEmpty && g.Picker.Contains(p.X, p.Y)) return true;
        return false;
    }

    // ---- Mouse interaction ----
    private void OnSurfaceMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        // 1) Open tile pane takes precedence (pane OR its device dropdown popover).
        if (_openPaneTile != null)
        {
            int idx = _tiles.IndexOf(_openPaneTile);
            if (idx >= 0 && TilePaneContains(_openPaneTile, TileRects()[idx], e.Location))
            {
                var layout = PaneLayoutFor(_openPaneTile, TileRects()[idx]);
                if (layout != null)
                {
                    HandlePaneClick(_openPaneTile, layout, e.Location);
                    return;
                }
            }
        }

        // 2) Open global pane takes precedence (pane OR its device picker popover).
        if (_globalPaneOpen && GlobalPaneContains(e.Location))
        {
            var g = GlobalPaneLayout();
            if (g != null)
            {
                HandleGlobalPaneClick(g, e.Location);
                return;
            }
        }

        // 3a) Footer close button exits the app.
        if (FooterCloseRect().Contains(e.Location.X, e.Location.Y))
        {
            Close();
            return;
        }

        // 3) Footer gear toggles the global pane.
        if (FooterGearRect().Contains(e.Location.X, e.Location.Y))
        {
            ToggleGlobalPane();
            return;
        }

        // 4) Tile gear toggles that tile's pane (closes the other pane).
        var gears = GearRects();
        for (int i = 0; i < gears.Count; i++)
        {
            if (gears[i].Contains(e.Location.X, e.Location.Y))
            {
                TogglePane(_tiles[i]);
                return;
            }
        }

        // 5) Grab handle begins a unified drag gesture (reorder inside / pop out
        //    outside). We do NOT detach on press — that happens on MouseUp-without-
        //    -drag (click-to-detach) or when the cursor leaves the window.
        var handles = HandleRects();
        for (int i = 0; i < handles.Count; i++)
        {
            if (handles[i].Contains(e.Location.X, e.Location.Y)) { BeginDrag(_tiles[i], e.Location); return; }
        }
    }

    // ---- Unified tile drag gesture ----

    private void BeginDrag(Tile tile, Point clientPoint)
    {
        int idx = _tiles.IndexOf(tile);
        if (idx < 0) return;
        var rect = TileRects()[idx];
        _dragState = DragState.Armed;
        _dragTile = tile;
        _dragStart = clientPoint;
        _dragGrabOffset = new Point(clientPoint.X - (int)rect.Left, clientPoint.Y - (int)rect.Top);
        _dragInsertAt = idx;
        _surface.Capture = true; // keep receiving Move/Up even outside the handle
        _hoverHandle = -1;
        _surface.Invalidate();
    }

    private void CancelDrag()
    {
        if (_dragState == DragState.None) return;
        _dragState = DragState.None;
        _dragTile = null;
        _dragInsertAt = -1;
        if (_surface.Capture) _surface.Capture = false;
        _surface.Invalidate();
    }

    /// <summary>True when the client point has left the window's client area.</summary>
    private bool IsOutsideClient(Point p) =>
        p.X < 0 || p.Y < 0 || p.X > ClientSize.Width || p.Y > ClientSize.Height;

    private void OnSurfaceMouseMove(object? sender, MouseEventArgs e)
    {
        // Drag gesture owns the mouse while a button is held from a grab handle.
        if (_dragState != DragState.None && _dragTile != null)
        {
            if (IsOutsideClient(e.Location))
            {
                // Cursor left the window -> pop the tile out into its own window.
                var tile = _dragTile;
                CancelDrag();
                Detach(tile);
                return;
            }
            int dx = e.Location.X - _dragStart.X;
            int dy = e.Location.Y - _dragStart.Y;
            bool past = (dx * dx + dy * dy) > DragThreshold * DragThreshold;
            if (_dragState == DragState.Armed && past)
                _dragState = DragState.Reorder;
            if (_dragState == DragState.Reorder)
            {
                _dragCursor = e.Location;
                _dragInsertAt = ComputeInsertionIndex(e.Location);
                _surface.Invalidate();
            }
            return;
        }

        int gearHit = -1;
        var gears = GearRects();
        for (int i = 0; i < gears.Count; i++)
            if (gears[i].Contains(e.Location.X, e.Location.Y)) { gearHit = i; break; }

        bool closeHit = false, globalCloseHit = false;
        int swatchHit = -1, globalRow = -1;
        bool deviceRowHit = false, removeHit = false;
        int dropdownHit = -1;
        if (_openPaneTile != null)
        {
            int idx = _tiles.IndexOf(_openPaneTile);
            if (idx >= 0)
            {
                var layout = PaneLayoutFor(_openPaneTile, TileRects()[idx]);
                if (layout != null)
                {
                    closeHit = layout.Close.Contains(e.Location.X, e.Location.Y);
                    for (int i = 0; i < layout.Swatches.Count; i++)
                        if (layout.Swatches[i].Contains(e.Location.X, e.Location.Y)) { swatchHit = i; break; }

                    if (_openPaneTile.IsDeviceSelectable)
                    {
                        deviceRowHit = layout.DeviceRow.Contains(e.Location.X, e.Location.Y);
                        removeHit = layout.RemoveTile.Contains(e.Location.X, e.Location.Y);
                        dropdownHit = -1;
                        if (_devicePaneState.DropdownOpen)
                        {
                            int total = _devicePaneState.Items.Count;
                            int maxScroll = Math.Max(0, total - DeviceDropdownMax);
                            int scroll = Math.Max(0, Math.Min(_devicePaneState.ScrollIndex, maxScroll));
                            for (int i = 0; i < layout.DeviceDropdownItems.Count; i++)
                            {
                                if (layout.DeviceDropdownItems[i].Contains(e.Location.X, e.Location.Y))
                                {
                                    int idx2 = scroll + i;
                                    dropdownHit = (idx2 < total && !_devicePaneState.Items[idx2].Disabled) ? idx2 : -2;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }
        bool footerGearHit = FooterGearRect().Contains(e.Location.X, e.Location.Y);
        bool footerCloseHit = FooterCloseRect().Contains(e.Location.X, e.Location.Y);
        int addBtnHit = -1, pickerHit = -1;
        if (_globalPaneOpen)
        {
            var g = GlobalPaneLayout();
            if (g != null && g.Pane.Contains(e.Location.X, e.Location.Y))
            {
                globalCloseHit = g.Close.Contains(e.Location.X, e.Location.Y);
                globalRow = GlobalRowAt(g, e.Location);
                for (int i = 0; i < g.AddButtons.Count; i++)
                    if (g.AddButtons[i].Contains(e.Location.X, e.Location.Y)) { addBtnHit = i; break; }
                if (_globalDeviceState.ActivePickerKind.HasValue)
                {
                    int total = _globalDeviceState.PickerItems.Count;
                    int maxScroll = Math.Max(0, total - 6);
                    int scroll = Math.Max(0, Math.Min(_globalDeviceState.PickerScrollIndex, maxScroll));
                    for (int i = 0; i < g.PickerItems.Count; i++)
                    {
                        if (g.PickerItems[i].Contains(e.Location.X, e.Location.Y))
                        {
                            int idx2 = scroll + i;
                            pickerHit = (idx2 < total && !_globalDeviceState.PickerItems[idx2].Disabled) ? idx2 : -2;
                            break;
                        }
                    }
                }
            }
        }

        int handleHit = -1;
        var handles = HandleRects();
        for (int i = 0; i < handles.Count; i++)
            if (handles[i].Contains(e.Location.X, e.Location.Y)) { handleHit = i; break; }

        bool changed = false;
        if (gearHit != _hoverGear) { _hoverGear = gearHit; changed = true; }
        if (handleHit != _hoverHandle) { _hoverHandle = handleHit; changed = true; }
        if (closeHit != _hoverPaneClose) { _hoverPaneClose = closeHit; changed = true; }
        if (swatchHit != _hoverSwatch) { _hoverSwatch = swatchHit; changed = true; }
        if (footerGearHit != _hoverFooterGear) { _hoverFooterGear = footerGearHit; changed = true; }
        if (footerCloseHit != _hoverFooterClose) { _hoverFooterClose = footerCloseHit; changed = true; }
        if (globalCloseHit != _hoverGlobalClose) { _hoverGlobalClose = globalCloseHit; changed = true; }
        if (globalRow != _globalHoverRow) { _globalHoverRow = globalRow; changed = true; }
        if (deviceRowHit != _hoverDeviceRow) { _hoverDeviceRow = deviceRowHit; changed = true; }
        if (removeHit != _hoverRemove) { _hoverRemove = removeHit; changed = true; }
        if (dropdownHit != _devicePaneState.HoverIndex) { _devicePaneState.HoverIndex = dropdownHit < 0 ? -1 : dropdownHit; changed = true; }
        if (addBtnHit != _hoverAddButton) { _hoverAddButton = addBtnHit; changed = true; }
        if (pickerHit != _hoverPickerItem) { _hoverPickerItem = pickerHit < 0 ? -1 : pickerHit; changed = true; }

        if (changed)
        {
            bool hand = gearHit >= 0 || _hoverPaneClose || _hoverSwatch >= 0 || footerGearHit
                || footerCloseHit || _hoverGlobalClose || _globalHoverRow >= 0
                || _hoverDeviceRow || _hoverRemove || _devicePaneState.HoverIndex >= 0
                || _hoverAddButton >= 0 || _hoverPickerItem >= 0;
            _surface.Cursor = (_hoverHandle >= 0) ? Cursors.SizeAll : (hand ? Cursors.Hand : Cursors.Default);
            _surface.Invalidate();
        }
    }

    private void OnSurfaceMouseUp(object? sender, MouseEventArgs e)
    {
        if (_dragState == DragState.None || _dragTile == null) return;

        // Snapshot the drag values BEFORE CancelDrag() — CancelDrag resets
        // _dragInsertAt (and _dragTile/_dragState) to neutral, so reading them
        // after it would always see -1/None and silently skip the commit.
        var tile = _dragTile;
        int from = _tiles.IndexOf(tile);
        var state = _dragState;
        int target = _dragInsertAt;
        CancelDrag();

        if (state == DragState.Armed)
        {
            // Never moved past threshold -> treat as a click -> detach (preserves
            // the original click-to-detach affordance).
            if (from >= 0) Detach(tile);
            return;
        }

        // Reorder mode: compute the destination FIRST and only touch the list
        // when the move is real. (Previously RemoveAt ran before the no-op
        // check, so dropping a tile on its own right half — target = from+1,
        // insertAt = from — removed it and never re-inserted: the tile
        // disappeared from the grid.)
        if (from >= 0 && target >= 0)
        {
            int insertAt = target > from ? target - 1 : target;
            if (insertAt < 0) insertAt = 0;
            if (insertAt != from)
            {
                _tiles.RemoveAt(from);
                if (insertAt > _tiles.Count) insertAt = _tiles.Count;
                _tiles.Insert(insertAt, tile);
                SaveTileOrder();
            }
        }
        _surface.Invalidate();
    }

    /// <summary>Computes the insertion slot for the dragged tile from the cursor
    /// position against the current grid cells. Uses a nearest-cell rule with a
    /// midpoint split: the cell under the cursor decides before/after by which
    /// half the cursor is in; if the cursor is outside every cell, the nearest
    /// cell (by center) is used.</summary>
    private int ComputeInsertionIndex(Point cursor)
    {
        var rects = TileRects();
        if (rects.Count == 0) return 0;

        int best = 0;
        float bestDist = float.MaxValue;
        int underCell = -1;
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            float cx = r.MidX, cy = r.MidY;
            float d = (cursor.X - cx) * (cursor.X - cx) + (cursor.Y - cy) * (cursor.Y - cy);
            if (d < bestDist) { bestDist = d; best = i; }
            if (r.Contains(cursor.X, cursor.Y)) underCell = i;
        }
        int cell = underCell >= 0 ? underCell : best;
        var cr = rects[cell];
        // Determine before/after using the dominant axis of the cell.
        bool after = cursor.X > cr.MidX; // left-to-right reading order
        int idx = cell + (after ? 1 : 0);
        return Math.Clamp(idx, 0, rects.Count);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            if (_dragState != DragState.None)
            {
                CancelDrag();
                e.Handled = true;
                return;
            }
            // Close any open dropdown / picker first, then the panes.
            if (_devicePaneState.DropdownOpen) { _devicePaneState.DropdownOpen = false; _devicePaneState.HoverIndex = -1; _surface.Invalidate(); e.Handled = true; return; }
            if (_globalDeviceState.ActivePickerKind.HasValue) { ClosePicker(); _surface.Invalidate(); e.Handled = true; return; }
            if (_openPaneTile != null) { _openPaneTile = null; ResetDevicePaneState(); _surface.Invalidate(); e.Handled = true; return; }
            if (_globalPaneOpen) { _globalPaneOpen = false; ClosePicker(); _surface.Invalidate(); e.Handled = true; return; }
        }
        base.OnKeyDown(e);
    }

    private void TogglePane(Tile tile)
    {
        if (_globalPaneOpen) { _globalPaneOpen = false; _globalHoverRow = -1; }
        if (_openPaneTile == tile)
        {
            _openPaneTile = null;
            ResetDevicePaneState();
        }
        else
        {
            _openPaneTile = tile;
            ResetDevicePaneState();
        }
        _hoverSwatch = -1;
        _hoverPaneClose = false;
        _surface.Invalidate();
    }

    private void ToggleGlobalPane()
    {
        if (_openPaneTile != null) { _openPaneTile = null; ResetDevicePaneState(); }
        _globalPaneOpen = !_globalPaneOpen;
        _hoverGlobalClose = false;
        _globalHoverRow = -1;
        if (!_globalPaneOpen) ClosePicker();
        _surface.Invalidate();
    }

    private void HandlePaneClick(Tile tile, TileRenderer.PaneLayout layout, Point p)
    {
        if (layout.Close.Contains(p.X, p.Y)) { _openPaneTile = null; ResetDevicePaneState(); _surface.Invalidate(); return; }

        // ---- Multi-instance device section (Disk/GPU/Network tiles) ----
        if (tile.IsDeviceSelectable)
        {
            if (layout.DeviceRow.Contains(p.X, p.Y))
            {
                bool willOpen = !_devicePaneState.DropdownOpen;
                _devicePaneState.DropdownOpen = willOpen;
                if (willOpen)
                {
                    RefreshDevicePaneItems(tile);
                    _devicePaneState.ScrollIndex = 0;
                    if (_devicePaneState.Items.Count == 0) _devicePaneState.DropdownOpen = false;
                }
                else { _devicePaneState.HoverIndex = -1; }
                _surface.Invalidate();
                return;
            }

            if (_devicePaneState.DropdownOpen)
            {
                int total = _devicePaneState.Items.Count;
                int maxScroll = Math.Max(0, total - DeviceDropdownMax);
                int scroll = Math.Max(0, Math.Min(_devicePaneState.ScrollIndex, maxScroll));

                if (layout.DeviceDropdownUp.Contains(p.X, p.Y) && scroll > 0)
                {
                    _devicePaneState.ScrollIndex = scroll - 1;
                    _surface.Invalidate();
                    return;
                }
                if (layout.DeviceDropdownDown.Contains(p.X, p.Y) && scroll < maxScroll)
                {
                    _devicePaneState.ScrollIndex = scroll + 1;
                    _surface.Invalidate();
                    return;
                }

                for (int i = 0; i < layout.DeviceDropdownItems.Count; i++)
                {
                    if (layout.DeviceDropdownItems[i].Contains(p.X, p.Y))
                    {
                        int idx = scroll + i;
                        if (idx < total)
                        {
                            var item = _devicePaneState.Items[idx];
                            if (!item.Disabled && tile.DevicePicked != null)
                            {
                                tile.DevicePicked(item.Id);
                                _devicePaneState.DropdownOpen = false;
                                _devicePaneState.HoverIndex = -1;
                            }
                        }
                        _surface.Invalidate();
                        return;
                    }
                }

                // Click anywhere else in the pane while the dropdown is open:
                // close just the dropdown (existing toggle/swatch behavior unchanged).
                _devicePaneState.DropdownOpen = false;
                _devicePaneState.HoverIndex = -1;
                _surface.Invalidate();
                return;
            }

            if (layout.RemoveTile.Contains(p.X, p.Y))
            {
                RemoveInstanceTile(tile);
                ResetDevicePaneState();
                return;
            }
        }

        for (int i = 0; i < layout.Toggles.Count; i++)
        {
            if (layout.Toggles[i].Contains(p.X, p.Y))
            {
                switch (i)
                {
                    case 0: tile.Settings.ShowTitle = !tile.Settings.ShowTitle; break;
                    case 1: tile.Settings.ShowBigValue = !tile.Settings.ShowBigValue; break;
                    case 2: tile.Settings.ShowUsageBar = !tile.Settings.ShowUsageBar; break;
                    case 3: tile.Settings.ShowSparkline = !tile.Settings.ShowSparkline; break;
                    case 4: tile.Settings.ShowSecondaryLine = !tile.Settings.ShowSecondaryLine; break;
                }
                // Sync the flipped flag into config WITHOUT clobbering it back:
                // TileConfig.ApplyTo copies config -> settings, so calling it here
                // would revert the just-toggled flag. Write the field directly.
                var tc = _config.Tile(tile.ConfigKey);
                tc.ShowTitle = tile.Settings.ShowTitle;
                tc.ShowBigValue = tile.Settings.ShowBigValue;
                tc.ShowUsageBar = tile.Settings.ShowUsageBar;
                tc.ShowSparkline = tile.Settings.ShowSparkline;
                tc.ShowSecondaryLine = tile.Settings.ShowSecondaryLine;
                tc.AccentArgb = tile.Settings.AccentColor.HasValue
                    ? (uint)((tile.Settings.AccentColor.Value.Alpha << 24) | (tile.Settings.AccentColor.Value.Red << 16) | (tile.Settings.AccentColor.Value.Green << 8) | tile.Settings.AccentColor.Value.Blue)
                    : null;
                _config.Save();
                _surface.Invalidate();
                return;
            }
        }

        for (int i = 0; i < layout.UnitSegments.Count; i++)
        {
            if (layout.UnitSegments[i].Contains(p.X, p.Y))
            {
                tile.Settings.UnitMode = (TileUnitMode)i;
                var tc = _config.Tile(tile.ConfigKey);
                tc.UnitMode = tile.Settings.UnitMode;
                tc.AccentArgb = tile.Settings.AccentColor.HasValue
                    ? (uint)((tile.Settings.AccentColor.Value.Alpha << 24) | (tile.Settings.AccentColor.Value.Red << 16) | (tile.Settings.AccentColor.Value.Green << 8) | tile.Settings.AccentColor.Value.Blue)
                    : null;
                _config.Save();
                _surface.Invalidate();
                return;
            }
        }

        for (int i = 0; i < layout.Swatches.Count; i++)
        {
            if (layout.Swatches[i].Contains(p.X, p.Y))
            {
                tile.Settings.AccentColor = TileRenderer.SwatchPalette()[i];
                _config.Tile(tile.ConfigKey).AccentArgb = (uint)((tile.Settings.AccentColor.Value.Alpha << 24) | (tile.Settings.AccentColor.Value.Red << 16) | (tile.Settings.AccentColor.Value.Green << 8) | tile.Settings.AccentColor.Value.Blue);
                _config.Save();
                _surface.Invalidate();
                return;
            }
        }

        if (layout.Custom.Contains(p.X, p.Y))
        {
            var current = TilePalette.Resolve(tile.Kind, tile.Settings);
            using var dlg = new ColorDialog
            {
                Color = Color.FromArgb(current.Alpha, current.Red, current.Green, current.Blue),
                FullOpen = true,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                var c = dlg.Color;
                tile.Settings.AccentColor = new SKColor(c.R, c.G, c.B);
                _config.Tile(tile.ConfigKey).AccentArgb = (uint)((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);
                _config.Save();
                _surface.Invalidate();
            }
            return;
        }

        if (layout.Reset.Contains(p.X, p.Y))
        {
            tile.Settings.AccentColor = null;
            _config.Tile(tile.ConfigKey).AccentArgb = null;
            _config.Save();
            _surface.Invalidate();
            return;
        }
    }

    private int GlobalRowAt(TileRenderer.GlobalPaneLayout g, Point p)
    {
        if (g.LaunchToggle.Contains(p.X, p.Y)) return 0;
        if (g.KioskToggle.Contains(p.X, p.Y)) return 2;
        if (g.AlwaysOnTopToggle.Contains(p.X, p.Y)) return 3;
        for (int i = 0; i < g.ThemeSegments.Count; i++) if (g.ThemeSegments[i].Contains(p.X, p.Y)) return 4;
        for (int i = 0; i < g.GraphSpanSegments.Count; i++) if (g.GraphSpanSegments[i].Contains(p.X, p.Y)) return 8 + i;
        for (int i = 0; i < g.DecimalsSegments.Count; i++) if (g.DecimalsSegments[i].Contains(p.X, p.Y)) return 12 + i;
        if (g.ThresholdToggle.Contains(p.X, p.Y)) return 5;
        if (g.ThresholdMinus.Contains(p.X, p.Y)) return 6;
        if (g.ThresholdPlus.Contains(p.X, p.Y)) return 7;
        for (int i = 0; i < g.TileChips.Count; i++) if (g.TileChips[i].Contains(p.X, p.Y)) return 100 + i;
        return -1;
    }

    private void HandleGlobalPaneClick(TileRenderer.GlobalPaneLayout g, Point p)
    {
        if (g.Close.Contains(p.X, p.Y)) { _globalPaneOpen = false; _surface.Invalidate(); return; }

        if (g.LaunchToggle.Contains(p.X, p.Y))
        {
            SetStartup(!GetStartup());
            _config.LaunchAtStartup = GetStartup();
            _config.Save();
            _surface.Invalidate();
            return;
        }
        if (g.KioskToggle.Contains(p.X, p.Y))
        {
            SetKiosk(!_config.KioskMode);
            _surface.Invalidate();
            return;
        }
        if (g.AlwaysOnTopToggle.Contains(p.X, p.Y))
        {
            _config.AlwaysOnTop = !_config.AlwaysOnTop;
            ApplyAlwaysOnTop();
            _config.Save();
            _surface.Invalidate();
            return;
        }
        for (int i = 0; i < g.ThemeSegments.Count; i++)
        {
            if (g.ThemeSegments[i].Contains(p.X, p.Y))
            {
                ApplyTheme(Theme.All[i]);
                _config.Save();
                _surface.Invalidate();
                return;
            }
        }
        for (int i = 0; i < g.GraphSpanSegments.Count; i++)
        {
            if (g.GraphSpanSegments[i].Contains(p.X, p.Y))
            {
                int secs = TileRenderer.GraphSpanSeconds(i);
                _config.GraphWindowSeconds = secs;
                ApplyGraphWindow(secs);
                _config.Save();
                _surface.Invalidate();
                return;
            }
        }
        for (int i = 0; i < g.DecimalsSegments.Count; i++)
        {
            if (g.DecimalsSegments[i].Contains(p.X, p.Y))
            {
                _config.ValueDecimals = i;
                Format.ValueDecimals = i;
                _config.Save();
                _surface.Invalidate();
                return;
            }
        }
        if (g.ThresholdToggle.Contains(p.X, p.Y))
        {
            _config.ThresholdEnabled = !_config.ThresholdEnabled;
            TileRenderer.ThresholdEnabled = _config.ThresholdEnabled;
            _config.Save();
            _surface.Invalidate();
            return;
        }
        if (g.ThresholdMinus.Contains(p.X, p.Y))
        {
            _config.ThresholdPercent = Math.Max(50, _config.ThresholdPercent - 5);
            TileRenderer.ThresholdPercent = _config.ThresholdPercent;
            _config.Save();
            _surface.Invalidate();
            return;
        }
        if (g.ThresholdPlus.Contains(p.X, p.Y))
        {
            _config.ThresholdPercent = Math.Min(100, _config.ThresholdPercent + 5);
            TileRenderer.ThresholdPercent = _config.ThresholdPercent;
            _config.Save();
            _surface.Invalidate();
            return;
        }

        // Tile enable/disable chips (canonical order Cpu, Ram only — Disk/Gpu/
        // Network are managed via the instance rows, so clicks there are no-ops).
        var chipKinds = new[] { TileKind.Cpu, TileKind.Ram };
        for (int i = 0; i < g.TileChips.Count; i++)
        {
            if (g.TileChips[i].Contains(p.X, p.Y))
            {
                SetTileEnabled(chipKinds[i], !_config.Tile(chipKinds[i].ToString()).Enabled);
                return;
            }
        }

        // ---- Instance-aware Tiles section: Disk/GPU/Network management rows ----
        // AddButtons are in canonical order Disk, Gpu, Network (see renderer).
        var addKinds = new[] { TileKind.Disk, TileKind.Gpu, TileKind.Network };
        for (int i = 0; i < g.AddButtons.Count; i++)
        {
            if (g.AddButtons[i].Contains(p.X, p.Y))
            {
                OpenPicker(addKinds[i]);
                _surface.Invalidate();
                return;
            }
        }

        // Open device picker for the active kind.
        if (_globalDeviceState.ActivePickerKind.HasValue)
        {
            var kind = _globalDeviceState.ActivePickerKind.Value;

            if (g.PickerClose.Contains(p.X, p.Y)) { ClosePicker(); _surface.Invalidate(); return; }

            if (g.PickerUp.Contains(p.X, p.Y))
            {
                int maxScroll = Math.Max(0, _globalDeviceState.PickerItems.Count - 6);
                _globalDeviceState.PickerScrollIndex = Math.Max(0, Math.Min(_globalDeviceState.PickerScrollIndex, maxScroll) - 1);
                _surface.Invalidate();
                return;
            }
            if (g.PickerDown.Contains(p.X, p.Y))
            {
                int maxScroll = Math.Max(0, _globalDeviceState.PickerItems.Count - 6);
                int cur = Math.Min(_globalDeviceState.PickerScrollIndex, maxScroll);
                if (cur < maxScroll) { _globalDeviceState.PickerScrollIndex = cur + 1; _surface.Invalidate(); }
                return;
            }

            int total = _globalDeviceState.PickerItems.Count;
            if (total > 0)
            {
                int maxScroll2 = Math.Max(0, total - 6);
                int scroll = Math.Max(0, Math.Min(_globalDeviceState.PickerScrollIndex, maxScroll2));
                for (int i = 0; i < g.PickerItems.Count; i++)
                {
                    if (g.PickerItems[i].Contains(p.X, p.Y))
                    {
                        int idx = scroll + i;
                        if (idx < total)
                        {
                            var item = _globalDeviceState.PickerItems[idx];
                            if (!item.Disabled)
                            {
                                AddInstanceTile(kind, item.Id);
                                // Refresh the picker: if every device is now bound,
                                // keep it open showing the "All added" empty state.
                                RefreshPickerItems(kind);
                                _globalDeviceState.PickerScrollIndex = 0;
                            }
                        }
                        _surface.Invalidate();
                        return;
                    }
                }
            }

            // Click inside the picker card but not on an item/control: consume
            // (don't close). Click elsewhere in the pane: close the picker.
            if (g.Picker.Contains(p.X, p.Y)) { _surface.Invalidate(); return; }
            ClosePicker();
            _surface.Invalidate();
            return;
        }
    }

    /// <summary>
    /// Enables or disables a singleton tile kind (Cpu/Ram only). Disabling closes
    /// its detached window (if open) and removes it from the visible set;
    /// enabling re-inserts it. Visible tiles always derive from _allTiles filtered
    /// by the persisted Enabled flag, so the grid reflows automatically.
    /// </summary>
    private void SetTileEnabled(TileKind kind, bool enabled)
    {
        var key = kind.ToString();
        _config.Tile(key).Enabled = enabled;
        _config.Save();

        if (!enabled)
        {
            // Close any detached window for this kind (its reattach is a no-op
            // because the tile is now disabled). Also drop an open settings pane.
            foreach (var d in _detached.ToArray())
                if (d.Tile.ConfigKey == key) d.Close();
            if (_openPaneTile != null && _openPaneTile.ConfigKey == key)
                _openPaneTile = null;
        }

        RebuildVisibleTiles();
        _surface.Invalidate();
    }

    /// <summary>Recomputes _tiles from _allTiles using persisted Enabled flags,
    /// then applies any custom TileOrder so detaching/reattaching and enabling/
    /// disabling never scramble a saved layout.</summary>
    private void RebuildVisibleTiles()
    {
        _tiles.Clear();
        foreach (var t in _allTiles)
            if (_config.Tile(t.ConfigKey).Enabled)
                _tiles.Add(t);
        ApplyTileOrder();
    }

    /// <summary>Reorders the current _tiles to match the persisted TileOrder
    /// (ConfigKeys). When TileOrder is empty (no custom order yet) the canonical
    /// _allTiles order is kept. Enabled kinds missing from the list are appended;
    /// kinds in the list that are disabled are skipped.</summary>
    private void ApplyTileOrder()
    {
        if (_config.TileOrder.Count == 0) return;
        var byKey = _tiles.ToDictionary(t => t.ConfigKey);
        var ordered = new List<Tile>(_tiles.Count);
        foreach (var key in _config.TileOrder)
        {
            if (byKey.TryGetValue(key, out var t))
            {
                ordered.Add(t);
                byKey.Remove(key);
            }
        }
        // Append any enabled kinds not present in the saved order (e.g. newly
        // enabled, or added after the order was saved).
        ordered.AddRange(byKey.Values);
        _tiles.Clear();
        _tiles.AddRange(ordered);
    }

    private void SaveTileOrder()
    {
        _config.TileOrder = _tiles.Select(t => t.ConfigKey).ToList();
        _config.Save();
    }

    private void ApplyTheme(Theme t)
    {
        _theme = t;
        _config.ThemeName = t.Name;
        _renderer.Theme = t;
        BackColor = Color.FromArgb(t.Background.Red, t.Background.Green, t.Background.Blue);
        _surface.BackColor = BackColor;
        WindowComposition.Apply(Handle, t);
        foreach (var d in _detached) d.SetTheme(t);
        _surface.Invalidate();
    }

    private void ApplyAlwaysOnTop()
    {
        TopMost = _config.AlwaysOnTop;
        foreach (var d in _detached) d.TopMost = _config.AlwaysOnTop;
        _surface.Invalidate();
    }

    private void ApplyGraphWindow(int seconds)
    {
        var w = TimeSpan.FromSeconds(Math.Max(5, seconds));
        foreach (var rt in _runtimes)
        {
            rt.HistA.SetWindow(w);
            rt.HistB?.SetWindow(w);
        }
        _surface.Invalidate();
    }

    private void ClearHandleHover()
    {
        if (_hoverHandle == -1 && _hoverGear == -1 && !_hoverPaneClose && _hoverSwatch == -1
            && !_hoverFooterGear && !_hoverFooterClose && !_hoverGlobalClose && _globalHoverRow == -1) return;
        _hoverHandle = -1;
        _hoverGear = -1;
        _hoverPaneClose = false;
        _hoverSwatch = -1;
        _hoverFooterGear = false;
        _hoverFooterClose = false;
        _hoverGlobalClose = false;
        _globalHoverRow = -1;
        _surface.Cursor = Cursors.Default;
        _surface.Invalidate();
    }

    private void OnSurfaceMouseLeave(object? sender, EventArgs e) => ClearHandleHover();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void Detach(Tile tile)
    {
        _tiles.Remove(tile);
        if (_openPaneTile == tile) _openPaneTile = null;
        _hoverHandle = -1;
        _hoverGear = -1;
        _surface.Cursor = Cursors.Default;

        Point pos = _config.TryGetDetachedPosition(tile.ConfigKey, out var saved)
            ? saved
            : new Point(Cursor.Position.X - 150, Cursor.Position.Y - 20);

        DetachedTileForm form = null!;
        form = new DetachedTileForm(
            tile, _theme, pos, _config.AlwaysOnTop,
            () => Reattach(tile, form),
            getDevices: GetDevicesFor,
            isDeviceBound: IsDeviceBound,
            removeTile: RemoveInstanceTile);
        _detached.Add(form);
        form.Show();
        _surface.Invalidate();
        _surface.Capture = false;
        SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    private void Reattach(Tile tile, DetachedTileForm form)
    {
        _detached.Remove(form);
        _config.SetDetachedPosition(tile.ConfigKey, form.Location);
        _config.Save();
        // Rebuild from the master list so a tile disabled while detached does not
        // reappear, and ordering stays canonical.
        RebuildVisibleTiles();
        if (_openPaneTile == tile) _openPaneTile = null;
        _surface.Invalidate();
    }

    // --- Borderless drag + resize ---
    private const int ResizeMargin = 8;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCMOUSEMOVE = 0x00A0;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int HTCAPTION = 2;
    private const int HTCLIENT = 1;
    private const int WM_DWMCOMPOSITIONCHANGED = 0x031E;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            int lp = m.LParam.ToInt32();
            var pt = PointToClient(new Point(
                (short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));

            if (ClaimedHitTest(pt) != 0) { m.Result = (IntPtr)HTCLIENT; return; }

            int w = ClientSize.Width;
            int h = ClientSize.Height;
            bool left = pt.X <= ResizeMargin;
            bool right = pt.X >= w - ResizeMargin;
            bool top = pt.Y <= ResizeMargin;
            bool bottom = pt.Y >= h - ResizeMargin;

            int result = HTCAPTION;
            if (top && left) result = HTTOPLEFT;
            else if (top && right) result = HTTOPRIGHT;
            else if (bottom && left) result = HTBOTTOMLEFT;
            else if (bottom && right) result = HTBOTTOMRIGHT;
            else if (top) result = HTTOP;
            else if (bottom) result = HTBOTTOM;
            else if (left) result = HTLEFT;
            else if (right) result = HTRIGHT;

            m.Result = (IntPtr)result;
            return;
        }
        // Click-outside dismissal for either open pane (side-effect free on
        // WM_NCHITTEST; this only runs on an actual non-client left press).
        if (m.Msg == WM_NCLBUTTONDOWN)
        {
            if (_openPaneTile != null) { _openPaneTile = null; ResetDevicePaneState(); _surface.Invalidate(); }
            if (_globalPaneOpen) { _globalPaneOpen = false; ClosePicker(); _surface.Invalidate(); }
        }
        if (m.Msg == WM_NCMOUSEMOVE) ClearHandleHover();
        // Re-apply accent when DWM composition changes (e.g. after
        // a theme toggle or user32 policy reset).
        if (m.Msg == WM_DWMCOMPOSITIONCHANGED)
        {
            WindowComposition.Apply(Handle, _theme);
        }
        base.WndProc(ref m);
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        int w = e.Info.Width;
        int h = e.Info.Height;

        if (_theme.Backdrop == WindowBackdrop.Opaque)
        {
            var bg = _theme.BackgroundPaint(); // cached — do NOT dispose
            canvas.DrawRect(0, 0, w, h, bg);
        }
        else
        {
            canvas.Clear(SKColors.Transparent);
        }

        var rects = TileRects();
        for (int i = 0; i < _tiles.Count; i++)
            _tiles[i].Draw(canvas, rects[i]);
        for (int i = 0; i < _tiles.Count; i++)
        {
            var accent = TilePalette.Resolve(_tiles[i].Kind, _tiles[i].Settings);
            _renderer.DrawGrabHandle(canvas, rects[i], i == _hoverHandle, accent);
            _renderer.DrawGear(canvas, rects[i], i == _hoverGear, accent);
        }

        // Drag-reorder overlay: a semi-transparent ghost of the tile following
        // the cursor, plus a clear insertion indicator at the target slot.
        if (_dragState == DragState.Reorder && _dragTile != null)
        {
            int from = _tiles.IndexOf(_dragTile);
            if (from >= 0)
            {
                var src = rects[from];
                float gw = src.Width;
                float gh = src.Height;
                float gx = _dragCursor.X - _dragGrabOffset.X;
                float gy = _dragCursor.Y - _dragGrabOffset.Y;
                var ghost = new SKRect(gx, gy, gx + gw, gy + gh);
                _renderer.DrawDragGhost(canvas, ghost, _dragTile, _theme);
                _renderer.DrawInsertionIndicator(canvas, rects, _dragInsertAt, _theme.Accent);
            }
        }

        DrawFooter(canvas, w, h);

        if (_openPaneTile != null)
        {
            int idx = _tiles.IndexOf(_openPaneTile);
            if (idx >= 0)
            {
                var tile = _tiles[idx];
                var v = new TileVisual(tile.Kind, tile.Settings, TilePalette.Resolve(tile.Kind, tile.Settings), _theme, false);
                var layout = _renderer.ComputePaneLayout(rects[idx], ClientRect(), v, tile, _devicePaneState);
                if (layout != null)
                    _renderer.DrawSettingsPane(canvas, layout, v, _hoverPaneClose, tile, _devicePaneState, _hoverDeviceRow, _hoverRemove);
            }
        }

        if (_globalPaneOpen)
        {
            var g = GlobalPaneLayout();
            if (g != null)
                _renderer.DrawGlobalPane(canvas, g, _config, _theme, _hoverGlobalClose, _globalHoverRow, _globalDeviceState, _hoverAddButton, _hoverPickerItem);
        }
    }

    // Footer segment text/layout cache. The top-process snapshot changes at
    // ~1 Hz (the poller stores a fresh array each tick), so composing strings,
    // measuring them, and fitting the truncated values is redone only when the
    // snapshot reference or the available width changes — not every frame.
    private sealed class FooterCache
    {
        public IReadOnlyList<Metric>? Snapshot;
        public float MaxRight = -1;
        public float TotalWidth;
        public readonly List<(string Label, string Value, SKColor Color, float X, float LabelW, float ValueW)> Segments = new(4);
    }
    private readonly FooterCache _footerCache = new();

    private void DrawFooter(SKCanvas canvas, int w, int h)
    {
        float bandTop = h - FooterHeight;
        using var band = new SKPaint { Color = _theme.FooterBand, Style = SKPaintStyle.Fill };
        canvas.DrawRect(0, bandTop, w, FooterHeight, band);
        using var sep = new SKPaint { Color = _theme.TileBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawLine(0, bandTop, w, bandTop, sep);

        var topSnap = _poller.GetSnapshot(_top);

        // Hard right boundary: leave a gap before the footer gear so the
        // Top text can never collide with the gear/close buttons.
        float maxRight = FooterGearRect().Left - 10;
        const float FadeWidth = 40; // px of smooth dissolve before the boundary

        if (!ReferenceEquals(_footerCache.Snapshot, topSnap) || _footerCache.MaxRight != maxRight)
            RebuildFooterSegments(topSnap, maxRight);

        var labelFont = TileRenderer.CachedFont("Segoe UI", 14);
        var valueFont = TileRenderer.CachedFont("Segoe UI Semibold", 14);
        using var labelPaint = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
        float cy = bandTop + FooterHeight / 2f + 4;

        // Safety net: clip the status text to the region left of the gear so a
        // segment can never reach the buttons even on an extreme portrait window.
        canvas.Save();
        canvas.ClipRect(new SKRect(0, bandTop, maxRight, bandTop + FooterHeight));
        foreach (var seg in _footerCache.Segments)
        {
            canvas.DrawText(seg.Label, seg.X, cy, labelFont, labelPaint);
            using var cp = new SKPaint { Color = seg.Color, IsAntialias = true };
            canvas.DrawText(seg.Value, seg.X + seg.LabelW, cy, valueFont, cp);
        }
        canvas.Restore();

        // Smooth fade: dissolve any text approaching the boundary into the band
        // color so it never hard-clips against the gear/close buttons.
        if (_footerCache.TotalWidth > maxRight - FadeWidth)
        {
            float fadeStart = Math.Max(maxRight - FadeWidth, 0);
            using var fade = new SKPaint();
            fade.Shader = SKShader.CreateLinearGradient(
                new SKPoint(fadeStart, bandTop),
                new SKPoint(maxRight, bandTop),
                new[] { _theme.FooterBand.WithAlpha(0), _theme.FooterBand },
                new[] { 0f, 1f },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(fadeStart, bandTop, maxRight - fadeStart, FooterHeight, fade);
        }

        // Footer gear at far right.
        _renderer.DrawGearAt(canvas, FooterGearRect(), _hoverFooterGear, _theme.Accent);
        _renderer.DrawCloseAt(canvas, FooterCloseRect(), _hoverFooterClose);
    }

    /// <summary>Recomposes + measures the footer segments (labels, fitted values,
    /// accent colors, absolute X positions). Runs only when the top-process
    /// snapshot or the right boundary changes (~1 Hz or on resize).</summary>
    private void RebuildFooterSegments(IReadOnlyList<Metric> topSnap, float maxRight)
    {
        var c = _footerCache;
        c.Segments.Clear();
        c.Snapshot = topSnap;
        c.MaxRight = maxRight;

        string cpuName = TextValue(topSnap, "proc.topcpu.name");
        double cpuPct = MetricValue(topSnap, "proc.topcpu.pct");
        string ramName = TextValue(topSnap, "proc.topram.name");
        ulong ramBytes = (ulong)MetricValue(topSnap, "proc.topram.bytes");
        string gpuName = TextValue(topSnap, "proc.topgpu.name");
        double gpuPct = MetricValue(topSnap, "proc.topgpu.pct");
        string diskName = TextValue(topSnap, "proc.topdisk.name");
        double diskBps = MetricValue(topSnap, "proc.topdisk.bps");

        // Effective accent per kind (honors per-tile color overrides).
        SKColor AccentOf(TileKind kind)
        {
            var tile = _allTiles.Find(t => t.Kind == kind);
            return tile != null ? TilePalette.Resolve(kind, tile.Settings) : TilePalette.DefaultFor(kind);
        }

        var labelFont = TileRenderer.CachedFont("Segoe UI", 14);
        var valueFont = TileRenderer.CachedFont("Segoe UI Semibold", 14);
        float x = 14;
        const float gap = 22;

        // "CPU Top: chrome 12.5%" — muted label, bold measurement in the tile's
        // color. Skip a segment entirely when there isn't room for its label
        // (graceful degradation on narrow windows); fit the value with an
        // ellipsis via binary search instead of re-measuring per dropped char.
        void Segment(string label, string value, SKColor color)
        {
            float avail = maxRight - x;
            float labelW = labelFont.MeasureText(label);
            if (avail <= 0 || labelW >= avail) return;

            avail -= labelW;
            if (avail <= 0) return;
            string drawn = TileRenderer.FitToWidth(valueFont, value, avail);
            float valueW = valueFont.MeasureText(drawn);

            c.Segments.Add((label, drawn, color, x, labelW, valueW));
            x += labelW + valueW + gap;
        }

        Segment("CPU Top: ", $"{cpuName} {Format.Percent(cpuPct)}", AccentOf(TileKind.Cpu));
        Segment("RAM Top: ", $"{ramName} {Format.Size(ramBytes, TileUnitMode.Auto, TileKind.Ram)}", AccentOf(TileKind.Ram));
        // BUG FIX (was: cpuPct) — the GPU segment rendered the CPU percentage.
        Segment("GPU Top: ", $"{gpuName} {Format.Percent(gpuPct)}", AccentOf(TileKind.Gpu));
        Segment("Disk Top: ", $"{diskName} {Format.Rate((ulong)diskBps, TileUnitMode.Auto, TileKind.Disk)}", AccentOf(TileKind.Disk));

        c.TotalWidth = x;
    }

    private static double MetricValue(IReadOnlyList<Metric> snap, string key)
    {
        foreach (var m in snap)
            if (m.Key == key) return m.Value;
        return 0;
    }

    private static string TextValue(IReadOnlyList<Metric> snap, string key)
    {
        foreach (var m in snap)
            if (m.Key == key) return m.Unit ?? "-";
        return "-";
    }

    // --- Startup registry ---
    private static readonly string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static bool GetStartup()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            var v = key?.GetValue("PCGauger") as string;
            return !string.IsNullOrEmpty(v);
        }
        catch { return false; }
    }
    private static void SetStartup(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            if (enable)
                key.SetValue("PCGauger", "\"" + Application.ExecutablePath + "\"");
            else
                key.DeleteValue("PCGauger", false);
        }
        catch { /* registry access can fail; ignore */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_config.KioskMode) _floatingBounds = Bounds;
        _config.WindowX = _floatingBounds.X;
        _config.WindowY = _floatingBounds.Y;
        _config.WindowW = _floatingBounds.Width;
        _config.WindowH = _floatingBounds.Height;
        _config.Save();
        base.OnFormClosing(e);
    }

    // ===================================================================
    //  v2 instance model: factory, migration, and public wiring API
    // ===================================================================

    /// <summary>Splits a ConfigKey ("Kind" or "Kind:DeviceId") into its parts.
    /// Returns false for a malformed key (defensive — never throws).</summary>
    private static bool TryParseConfigKey(string configKey, out TileKind kind, out string deviceId)
    {
        kind = TileKind.Cpu;
        deviceId = "";
        int colon = configKey.IndexOf(':');
        string kindName = colon < 0 ? configKey : configKey.Substring(0, colon);
        if (!Enum.TryParse<TileKind>(kindName, out kind)) return false;
        deviceId = colon < 0 ? "" : configKey.Substring(colon + 1);
        return true;
    }

    private static bool IsMultiInstance(TileKind kind) =>
        kind is TileKind.Disk or TileKind.Gpu or TileKind.Network;

    private IDeviceCatalog? CatalogFor(TileKind kind) => kind switch
    {
        TileKind.Disk => _diskCatalog,
        TileKind.Gpu => _gpuCatalog,
        TileKind.Network => _netCatalog,
        _ => null,
    };

    /// <summary>Resolves a friendly display name for a device id from its catalog
    /// (falls back to the raw id when not currently present).</summary>
    private string DisplayNameFor(TileKind kind, string deviceId)
    {
        if (deviceId.Length == 0) return "";
        var cat = CatalogFor(kind);
        var dev = cat?.GetDevices().FirstOrDefault(d => d.Id == deviceId);
        return dev?.DisplayName ?? deviceId;
    }

    /// <summary>
    /// Builds a runtime + tile for one instance (singleton or device-bound) and
    /// registers the provider with the appropriate poller. The draw lambda closes
    /// over the runtime (looked up by ConfigKey at draw time), so every tile
    /// reads its own provider + histories. Does NOT add the tile to _allTiles /
    /// _tiles or to TileInstances — callers decide membership.
    /// </summary>
    private TileRuntime? CreateInstanceTile(TileKind kind, string deviceId)
    {
        IMetricProvider provider;
        RollingHistory histA = new(TimeSpan.FromSeconds(60));
        RollingHistory? histB = null;
        IDeviceCatalog? catalog = null;
        Tile tile = null!;

        switch (kind)
        {
            case TileKind.Cpu:
                provider = _cpu;
                tile = new Tile(TileKind.Cpu, "CPU", (c, r) => DrawCpu(c, r, tile));
                break;
            case TileKind.Ram:
                provider = _mem;
                tile = new Tile(TileKind.Ram, "RAM", (c, r) => DrawRam(c, r, tile));
                break;
            case TileKind.Gpu:
                catalog = _gpuCatalog;
                if (!int.TryParse(deviceId, out int gpuIdx)) gpuIdx = 0;
                provider = new GpuProvider(gpuIdx, deferResolution: true);
                _poller.Add(provider);
                tile = new Tile(TileKind.Gpu, "GPU", (c, r) => DrawGpu(c, r, tile), deviceId);
                break;
            case TileKind.Disk:
                catalog = _diskCatalog;
                provider = new StorageProvider(deviceId, deferResolution: true);
                _poller.Add(provider);
                tile = new Tile(TileKind.Disk, "DISK", (c, r) => DrawDisk(c, r, tile), deviceId);
                histB = new RollingHistory(TimeSpan.FromSeconds(60));
                break;
            case TileKind.Network:
                catalog = _netCatalog;
                provider = new NetworkProvider(deviceId, deferResolution: true);
                _poller.Add(provider);
                tile = new Tile(TileKind.Network, "NET", (c, r) => DrawNet(c, r, tile), deviceId);
                histB = new RollingHistory(TimeSpan.FromSeconds(60));
                break;
            default:
                return null;
        }

        if (catalog != null)
        {
            tile.DeviceSource = () => catalog.GetDevices();
            tile.DevicePicked = id => RebindTileDevice(tile, id);
            // Placeholder display name at construction time. Resolving it via
            // DisplayNameFor → GetDevices() would do a synchronous DXGI/NIC/
            // DriveInfo enumeration on the UI thread and stall startup, so we
            // defer it: the picker resolves the friendly name lazily from
            // DeviceSource when it builds its list.
            tile.DeviceDisplayName = deviceId;
            tile.DeviceAvailable = provider switch
            {
                GpuProvider g => g.DeviceAvailable,
                StorageProvider d => d.DeviceAvailable,
                NetworkProvider n => n.DeviceAvailable,
                _ => true,
            };
        }

        var rt = new TileRuntime
        {
            Tile = tile,
            Provider = provider,
            HistA = histA,
            HistB = histB,
            Catalog = catalog,
        };
        _runtimes.Add(rt);
        _runtimeByKey[tile.ConfigKey] = rt;
        return rt;
    }

    /// <summary>
    /// v1 -> v2 migration. Idempotent: once TileInstances is non-empty the file
    /// is treated as v2 and this is a no-op. For a v1 file (TileInstances empty
    /// but Tiles present) it resolves the default device id for each
    /// multi-instance kind, moves any existing "Disk"/"Gpu"/"Network" Tiles entry
    /// to the device-qualified key, remaps TileOrder + DetachedPositions, and
    /// seeds TileInstances. Never throws — any oddity falls back to fresh
    /// defaults (treat the file as a new v2 config).
    /// </summary>
    private void MigrateConfigV1ToV2()
    {
        try
        {
            if (_config.TileInstances.Count > 0) return; // already v2

            // Resolve default device ids WITHOUT live enumeration — migration must
            // not block on device I/O. These placeholders are resolved later by
            // each provider's asynchronous BeginResolve (and by the catalogs when
            // a picker is opened). v1 implicitly showed GPU adapter 0.
            string diskId = "unknown";   // resolved later by StorageProvider
            string gpuId  = "0";         // v1 implicitly showed adapter 0
            string netId  = "unknown";   // resolved later by NetworkProvider

            var resolved = new List<string>
            {
                TileKind.Cpu.ToString(),
                TileKind.Ram.ToString(),
                $"{TileKind.Gpu}:{gpuId}",
                $"{TileKind.Disk}:{diskId}",
                $"{TileKind.Network}:{netId}",
            };

            // Move any existing v1 Tiles entries to their device-qualified keys.
            MoveTileEntry(TileKind.Gpu.ToString(), $"{TileKind.Gpu}:{gpuId}");
            MoveTileEntry(TileKind.Disk.ToString(), $"{TileKind.Disk}:{diskId}");
            MoveTileEntry(TileKind.Network.ToString(), $"{TileKind.Network}:{netId}");

            // Remap TileOrder (kind names -> device-qualified keys).
            var orderMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TileKind.Gpu.ToString()] = $"{TileKind.Gpu}:{gpuId}",
                [TileKind.Disk.ToString()] = $"{TileKind.Disk}:{diskId}",
                [TileKind.Network.ToString()] = $"{TileKind.Network}:{netId}",
            };
            _config.TileOrder = _config.TileOrder.Select(name =>
                orderMap.TryGetValue(name, out var mapped) ? mapped : name).ToList();

            // Remap DetachedPositions keys.
            RemapDetachedPositions(orderMap);

            _config.TileInstances = resolved;
            _config.Save();
        }
        catch
        {
            // Any oddity: treat as a fresh v2 config with the 5 canonical keys.
            _config.TileInstances = new List<string>
            {
                TileKind.Cpu.ToString(),
                TileKind.Ram.ToString(),
                $"{TileKind.Gpu}:0",
                $"{TileKind.Disk}:unknown",
                $"{TileKind.Network}:unknown",
            };
        }
    }

    private void MoveTileEntry(string oldKey, string newKey)
    {
        if (_config.Tiles.TryGetValue(oldKey, out var tc) && !_config.Tiles.ContainsKey(newKey))
        {
            _config.Tiles.Remove(oldKey);
            _config.Tiles[newKey] = tc;
        }
    }

    private void RemapDetachedPositions(Dictionary<string, string> orderMap)
    {
        foreach (var kvp in orderMap)
        {
            if (_config.DetachedPositions.TryGetValue(kvp.Key, out var pos) && !_config.DetachedPositions.ContainsKey(kvp.Value))
            {
                _config.DetachedPositions.Remove(kvp.Key);
                _config.DetachedPositions[kvp.Value] = pos;
            }
        }
    }

    // ---- Public wiring API (consumed by the final UI pass) ----

    /// <summary>Adds a new device-bound instance tile for a multi-instance kind.
    /// No-op for singleton kinds or when the device is already bound.</summary>
    internal void AddInstanceTile(TileKind kind, string deviceId)
    {
        if (!IsMultiInstance(kind)) return;
        if (IsDeviceBound(kind, deviceId)) return;

        var rt = CreateInstanceTile(kind, deviceId);
        if (rt == null) return;

        string key = rt.Tile.ConfigKey;
        _config.Tile(key); // ensure a config entry (defaults)
        if (!_config.TileInstances.Contains(key))
            _config.TileInstances.Add(key);
        _allTiles.Add(rt.Tile);
        if (_config.Tile(key).Enabled)
            _tiles.Add(rt.Tile);

        RebuildVisibleTiles();
        _config.Save();
        _surface.Invalidate();
    }

    /// <summary>Removes an instance tile. No-op for singleton kinds or tiles not
    /// in _allTiles. Keeps the Tiles config entry so re-adding restores settings.</summary>
    internal void RemoveInstanceTile(Tile tile)
    {
        if (!IsMultiInstance(tile.Kind)) return;
        var rt = _runtimes.FirstOrDefault(r => r.Tile == tile);
        if (rt == null) return;

        // Close its detached window if open.
        foreach (var d in _detached.ToArray())
            if (d.Tile == tile) d.Close();
        if (_openPaneTile == tile) _openPaneTile = null;

        _poller.Remove(rt.Provider);
        if (rt.Provider is IDisposable disp) disp.Dispose();

        _runtimes.Remove(rt);
        _runtimeByKey.Remove(tile.ConfigKey);
        _allTiles.Remove(tile);
        _config.TileInstances.Remove(tile.ConfigKey);
        _config.TileOrder.Remove(tile.ConfigKey);
        // Keep Tiles[configKey] so re-adding restores settings.

        RebuildVisibleTiles();
        _config.Save();
        _surface.Invalidate();
    }

    /// <summary>Re-points a tile at a different device. No-op if the id is
    /// unchanged or already bound to another tile of that kind.</summary>
    internal void RebindTileDevice(Tile tile, string deviceId)
    {
        if (!IsMultiInstance(tile.Kind)) return;
        if (tile.InstanceId == deviceId) return;
        if (IsDeviceBound(tile.Kind, deviceId)) return;
        var rt = _runtimes.FirstOrDefault(r => r.Tile == tile);
        if (rt == null) return;

        string oldKey = tile.ConfigKey;
        string newKey = $"{tile.Kind}:{deviceId}";

        // Build the new provider BEFORE touching the old one, so a construction
        // failure (e.g. a garbage GPU deviceId) leaves the tile bound to its
        // existing, still-valid provider instead of a dead one.
        IMetricProvider newProvider = tile.Kind switch
        {
            TileKind.Gpu => new GpuProvider(int.TryParse(deviceId, out var gpuIdx) ? gpuIdx : 0),
            TileKind.Disk => new StorageProvider(deviceId),
            TileKind.Network => new NetworkProvider(deviceId),
            _ => rt.Provider,
        };

        // Now swap: remove + dispose the old provider, then register the new one.
        // The poller's lock guarantees no Tick is executing the old provider once
        // Remove returns, so disposing it here is race-free.
        _poller.Remove(rt.Provider);
        if (rt.Provider is IDisposable disp) disp.Dispose();
        _poller.Add(newProvider);
        rt.Provider = newProvider;

        // Clear the runtime's histories (the new device starts fresh).
        rt.HistA = new RollingHistory(TimeSpan.FromSeconds(60));
        rt.HistB = tile.Kind == TileKind.Gpu ? null : new RollingHistory(TimeSpan.FromSeconds(60));

        // Update the tile's binding + display name.
        string display = DisplayNameFor(tile.Kind, deviceId);
        bool available = newProvider is GpuProvider g ? g.DeviceAvailable
            : newProvider is StorageProvider d ? d.DeviceAvailable
            : ((NetworkProvider)newProvider).DeviceAvailable;
        tile.UpdateDeviceBinding(deviceId, display, available);

        // Rename the config key everywhere.
        if (_config.Tiles.TryGetValue(oldKey, out var tc))
        {
            _config.Tiles.Remove(oldKey);
            _config.Tiles[newKey] = tc;
        }
        int idx = _config.TileInstances.IndexOf(oldKey);
        if (idx >= 0) _config.TileInstances[idx] = newKey;
        for (int i = 0; i < _config.TileOrder.Count; i++)
            if (_config.TileOrder[i] == oldKey) _config.TileOrder[i] = newKey;
        if (_config.DetachedPositions.TryGetValue(oldKey, out var pos))
        {
            _config.DetachedPositions.Remove(oldKey);
            _config.DetachedPositions[newKey] = pos;
        }
        _runtimeByKey.Remove(oldKey);
        _runtimeByKey[newKey] = rt;

        _config.Save();
        _surface.Invalidate();
    }

    /// <summary>Devices currently selectable for a kind (empty for Cpu/Ram).</summary>
    internal IReadOnlyList<DeviceDescriptor> GetDevicesFor(TileKind kind)
        => CatalogFor(kind)?.GetDevices() ?? Array.Empty<DeviceDescriptor>();

    /// <summary>True when a device id is already bound to a tile of that kind.</summary>
    internal bool IsDeviceBound(TileKind kind, string deviceId)
        => _allTiles.Any(t => t.Kind == kind && t.InstanceId == deviceId);

    /// <summary>Count of tiles (enabled or not) with the given kind.</summary>
    internal int CountInstances(TileKind kind)
        => _allTiles.Count(t => t.Kind == kind);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderTimer.Dispose();
            _poller.Dispose();
            _memPoller.Dispose();
            foreach (var d in _detached) d.Dispose();
            _surface.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// A standalone window hosting a single detached tile. Its grab handle
/// re-attaches it to the main window on click; closing the window does the
/// same. The handle is the only control, matching the main grid's UX.
/// </summary>
internal sealed class DetachedTileForm : Form
{
    private readonly HitTestSurface _surface;
    private readonly Tile _tile;
    private readonly TileRenderer _renderer;
    private Theme _theme;
    private readonly Action _onReattach;
    private readonly Func<TileKind, IReadOnlyList<DeviceDescriptor>> _getDevices;
    private readonly Func<TileKind, string, bool> _isDeviceBound;
    private readonly Action<Tile> _removeTile;
    private bool _hoverHandle;
    private bool _hoverGear;
    private bool _paneOpen;
    private bool _hoverPaneClose;
    private int _hoverSwatch = -1;
    private readonly System.Windows.Forms.Timer _renderTimer;

    // Per-tile device dropdown state (mirrors MainForm's wiring).
    private readonly TileRenderer.DevicePaneState _deviceState = new();
    private bool _hoverDeviceRow;
    private bool _hoverRemove;

    public TileKind TileKind => _tile.Kind;

    public Tile Tile => _tile;

    public DetachedTileForm(Tile tile, Theme theme, Point location, bool topMost, Action onReattach,
        Func<TileKind, IReadOnlyList<DeviceDescriptor>> getDevices,
        Func<TileKind, string, bool> isDeviceBound,
        Action<Tile> removeTile)
    {
        _tile = tile;
        _renderer = new TileRenderer(theme);
        _theme = theme;
        _onReattach = onReattach;
        _getDevices = getDevices;
        _isDeviceBound = isDeviceBound;
        _removeTile = removeTile;

        Text = "PCGauger";
        ClientSize = new Size(340, 360);
        BackColor = Color.FromArgb(theme.Background.Red, theme.Background.Green, theme.Background.Blue);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = topMost;
        StartPosition = FormStartPosition.Manual;
        Location = location;

        _surface = new HitTestSurface
        {
            Dock = DockStyle.Fill,
            BackColor = BackColor,
        };
        _surface.HitTestOverride = p => ClaimedHitTest(p) != 0 ? 1 : 0;
        _surface.PaintSurface += OnPaintSurface;
        _surface.MouseMove += OnSurfaceMouseMove;
        _surface.MouseDown += OnSurfaceMouseDown;
        _surface.MouseLeave += OnSurfaceMouseLeave;
        Controls.Add(_surface);

        _renderTimer = new System.Windows.Forms.Timer { Interval = 33 };
        _renderTimer.Tick += (_, _) => _surface.Invalidate();
        _renderTimer.Start();

        // Apply accent policy once the window handle is visible.
        Shown += (_, _) => WindowComposition.Apply(Handle, _theme);
    }

    public void SetTheme(Theme t)
    {
        _theme = t;
        _renderer.Theme = t;
        BackColor = Color.FromArgb(t.Background.Red, t.Background.Green, t.Background.Blue);
        _surface.BackColor = BackColor;
        WindowComposition.Apply(Handle, t);
        _surface.Invalidate();
    }

    private SKRect TileRect()
    {
        float gap = 12;
        return new SKRect(gap, gap, ClientSize.Width - gap, ClientSize.Height - gap);
    }

    private TileVisual TileVisual() => new(_tile.Kind, _tile.Settings, TilePalette.Resolve(_tile.Kind, _tile.Settings), _theme, false);

    private SKRect ClientRect() => new(0, 0, ClientSize.Width, ClientSize.Height);

    private int ClaimedHitTest(Point p)
    {
        var rect = TileRect();
        if (TileRenderer.GrabHandleRect(rect).Contains(p.X, p.Y)) return 1;
        if (TileRenderer.GearRect(rect).Contains(p.X, p.Y)) return 1;
        if (_paneOpen && PaneContains(p)) return 1;
        return 0;
    }

    /// <summary>True when <paramref name="p"/> lies inside the open settings pane
    /// OR its device dropdown popover (which can extend past the pane card), so a
    /// click on a dropdown item below the pane bottom is claimed as HTCLIENT and
    /// reaches <see cref="HandlePaneClick"/> instead of being dismissed.</summary>
    private bool PaneContains(Point p)
    {
        var rect = TileRect();
        var layout = _renderer.ComputePaneLayout(rect, ClientRect(), TileVisual(), _tile, _deviceState);
        if (layout == null) return false;
        if (layout.Pane.Contains(p.X, p.Y)) return true;
        if (_tile.IsDeviceSelectable && _deviceState.DropdownOpen
            && !layout.DeviceDropdown.IsEmpty && layout.DeviceDropdown.Contains(p.X, p.Y)) return true;
        return false;
    }

    private const int ResizeMargin = 8;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCMOUSEMOVE = 0x00A0;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int HTCAPTION = 2;
    private const int HTCLIENT = 1;
    private const int WM_DWMCOMPOSITIONCHANGED = 0x031E;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            int lp = m.LParam.ToInt32();
            var pt = PointToClient(new Point(
                (short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));

            if (ClaimedHitTest(pt) != 0) { m.Result = (IntPtr)HTCLIENT; return; }

            int w = ClientSize.Width;
            int h = ClientSize.Height;
            bool left = pt.X <= ResizeMargin;
            bool right = pt.X >= w - ResizeMargin;
            bool top = pt.Y <= ResizeMargin;
            bool bottom = pt.Y >= h - ResizeMargin;

            int result = HTCAPTION;
            if (top && left) result = HTTOPLEFT;
            else if (top && right) result = HTTOPRIGHT;
            else if (bottom && left) result = HTBOTTOMLEFT;
            else if (bottom && right) result = HTBOTTOMRIGHT;
            else if (top) result = HTTOP;
            else if (bottom) result = HTBOTTOM;
            else if (left) result = HTLEFT;
            else if (right) result = HTRIGHT;

            m.Result = (IntPtr)result;
            return;
        }
        if (m.Msg == WM_NCLBUTTONDOWN && _paneOpen)
        {
            _paneOpen = false;
            ResetDeviceState();
            _surface.Invalidate();
        }
        if (m.Msg == WM_NCMOUSEMOVE) ClearHandleHover();
        if (m.Msg == WM_DWMCOMPOSITIONCHANGED)
        {
            WindowComposition.Apply(Handle, _theme);
        }
        base.WndProc(ref m);
    }

    private void ClearHandleHover()
    {
        if (!_hoverHandle && !_hoverGear && !_hoverPaneClose && _hoverSwatch < 0) return;
        _hoverHandle = false;
        _hoverGear = false;
        _hoverPaneClose = false;
        _hoverSwatch = -1;
        _surface.Cursor = Cursors.Default;
        _surface.Invalidate();
    }

    private void ResetDeviceState()
    {
        _deviceState.DropdownOpen = false;
        _deviceState.ScrollIndex = 0;
        _deviceState.HoverIndex = -1;
        _deviceState.Items = Array.Empty<TileRenderer.DeviceItem>();
        _hoverDeviceRow = false;
        _hoverRemove = false;
    }

    private void RefreshDeviceItems()
    {
        var items = new List<TileRenderer.DeviceItem>(_getDevices(_tile.Kind).Count);
        foreach (var d in _getDevices(_tile.Kind))
            items.Add(new TileRenderer.DeviceItem
            {
                Id = d.Id,
                DisplayName = d.DisplayName,
                Detail = d.Detail,
                Checked = d.Id == _tile.InstanceId,
                Disabled = _isDeviceBound(_tile.Kind, d.Id) && d.Id != _tile.InstanceId,
            });
        _deviceState.Items = items;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            if (_deviceState.DropdownOpen) { _deviceState.DropdownOpen = false; _deviceState.HoverIndex = -1; _surface.Invalidate(); e.Handled = true; return; }
            if (_paneOpen) { _paneOpen = false; ResetDeviceState(); _surface.Invalidate(); e.Handled = true; return; }
        }
        base.OnKeyDown(e);
    }

    private void OnSurfaceMouseLeave(object? sender, EventArgs e) => ClearHandleHover();

    private void OnSurfaceMouseMove(object? sender, MouseEventArgs e)
    {
        var rect = TileRect();
        bool handleOver = TileRenderer.GrabHandleRect(rect).Contains(e.Location.X, e.Location.Y);
        bool gearOver = TileRenderer.GearRect(rect).Contains(e.Location.X, e.Location.Y);
        bool closeOver = false;
        int swatchOver = -1;
        bool deviceRowOver = false, removeOver = false;
        int dropdownOver = -1;
        if (_paneOpen)
        {
            var layout = _renderer.ComputePaneLayout(rect, ClientRect(), TileVisual(), _tile, _deviceState);
            if (layout != null)
            {
                closeOver = layout.Close.Contains(e.Location.X, e.Location.Y);
                for (int i = 0; i < layout.Swatches.Count; i++)
                    if (layout.Swatches[i].Contains(e.Location.X, e.Location.Y)) { swatchOver = i; break; }

                if (_tile.IsDeviceSelectable)
                {
                    deviceRowOver = layout.DeviceRow.Contains(e.Location.X, e.Location.Y);
                    removeOver = layout.RemoveTile.Contains(e.Location.X, e.Location.Y);
                    dropdownOver = -1;
                    if (_deviceState.DropdownOpen)
                    {
                        int total = _deviceState.Items.Count;
                        int maxScroll = Math.Max(0, total - 5);
                        int scroll = Math.Max(0, Math.Min(_deviceState.ScrollIndex, maxScroll));
                        for (int i = 0; i < layout.DeviceDropdownItems.Count; i++)
                        {
                            if (layout.DeviceDropdownItems[i].Contains(e.Location.X, e.Location.Y))
                            {
                                int idx2 = scroll + i;
                                dropdownOver = (idx2 < total && !_deviceState.Items[idx2].Disabled) ? idx2 : -2;
                                break;
                            }
                        }
                    }
                }
            }
        }

        if (handleOver != _hoverHandle || gearOver != _hoverGear || closeOver != _hoverPaneClose || swatchOver != _hoverSwatch
            || deviceRowOver != _hoverDeviceRow || removeOver != _hoverRemove || dropdownOver != _deviceState.HoverIndex)
        {
            _hoverHandle = handleOver;
            _hoverGear = gearOver;
            _hoverPaneClose = closeOver;
            _hoverSwatch = swatchOver;
            _hoverDeviceRow = deviceRowOver;
            _hoverRemove = removeOver;
            _deviceState.HoverIndex = dropdownOver < 0 ? -1 : dropdownOver;
            _surface.Cursor = handleOver ? Cursors.SizeAll
                : (gearOver || closeOver || swatchOver >= 0 || _hoverDeviceRow || _hoverRemove || _deviceState.HoverIndex >= 0) ? Cursors.Hand
                : Cursors.Default;
            _surface.Invalidate();
        }
    }

    private void OnSurfaceMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var rect = TileRect();

        if (_paneOpen && PaneContains(e.Location))
        {
            var layout = _renderer.ComputePaneLayout(rect, ClientRect(), TileVisual(), _tile, _deviceState);
            if (layout != null)
            {
                HandlePaneClick(layout, e.Location);
                return;
            }
        }

        if (TileRenderer.GearRect(rect).Contains(e.Location.X, e.Location.Y))
        {
            _paneOpen = !_paneOpen;
            _hoverSwatch = -1;
            _hoverPaneClose = false;
            ResetDeviceState();
            _surface.Invalidate();
            return;
        }

        if (TileRenderer.GrabHandleRect(rect).Contains(e.Location.X, e.Location.Y))
        {
            Close();
        }
    }

    private void HandlePaneClick(TileRenderer.PaneLayout layout, Point p)
    {
        if (layout.Close.Contains(p.X, p.Y)) { _paneOpen = false; ResetDeviceState(); _surface.Invalidate(); return; }

        // ---- Multi-instance device section (Disk/GPU/Network tiles) ----
        if (_tile.IsDeviceSelectable)
        {
            if (layout.DeviceRow.Contains(p.X, p.Y))
            {
                bool willOpen = !_deviceState.DropdownOpen;
                _deviceState.DropdownOpen = willOpen;
                if (willOpen)
                {
                    RefreshDeviceItems();
                    _deviceState.ScrollIndex = 0;
                    if (_deviceState.Items.Count == 0) _deviceState.DropdownOpen = false;
                }
                else { _deviceState.HoverIndex = -1; }
                _surface.Invalidate();
                return;
            }

            if (_deviceState.DropdownOpen)
            {
                int total = _deviceState.Items.Count;
                int maxScroll = Math.Max(0, total - 5);
                int scroll = Math.Max(0, Math.Min(_deviceState.ScrollIndex, maxScroll));

                if (layout.DeviceDropdownUp.Contains(p.X, p.Y) && scroll > 0)
                {
                    _deviceState.ScrollIndex = scroll - 1;
                    _surface.Invalidate();
                    return;
                }
                if (layout.DeviceDropdownDown.Contains(p.X, p.Y) && scroll < maxScroll)
                {
                    _deviceState.ScrollIndex = scroll + 1;
                    _surface.Invalidate();
                    return;
                }

                for (int i = 0; i < layout.DeviceDropdownItems.Count; i++)
                {
                    if (layout.DeviceDropdownItems[i].Contains(p.X, p.Y))
                    {
                        int idx = scroll + i;
                        if (idx < total)
                        {
                            var item = _deviceState.Items[idx];
                            if (!item.Disabled && _tile.DevicePicked != null)
                            {
                                _tile.DevicePicked(item.Id);
                                _deviceState.DropdownOpen = false;
                                _deviceState.HoverIndex = -1;
                            }
                        }
                        _surface.Invalidate();
                        return;
                    }
                }

                // Click anywhere else in the pane while the dropdown is open:
                // close just the dropdown.
                _deviceState.DropdownOpen = false;
                _deviceState.HoverIndex = -1;
                _surface.Invalidate();
                return;
            }

            if (layout.RemoveTile.Contains(p.X, p.Y))
            {
                _removeTile(_tile);
                return;
            }
        }

        for (int i = 0; i < layout.Toggles.Count; i++)
        {
            if (layout.Toggles[i].Contains(p.X, p.Y))
            {
                switch (i)
                {
                    case 0: _tile.Settings.ShowTitle = !_tile.Settings.ShowTitle; break;
                    case 1: _tile.Settings.ShowBigValue = !_tile.Settings.ShowBigValue; break;
                    case 2: _tile.Settings.ShowUsageBar = !_tile.Settings.ShowUsageBar; break;
                    case 3: _tile.Settings.ShowSparkline = !_tile.Settings.ShowSparkline; break;
                    case 4: _tile.Settings.ShowSecondaryLine = !_tile.Settings.ShowSecondaryLine; break;
                }
                _surface.Invalidate();
                return;
            }
        }

        for (int i = 0; i < layout.Swatches.Count; i++)
        {
            if (layout.Swatches[i].Contains(p.X, p.Y))
            {
                _tile.Settings.AccentColor = TileRenderer.SwatchPalette()[i];
                _surface.Invalidate();
                return;
            }
        }

        if (layout.Custom.Contains(p.X, p.Y))
        {
            var current = TilePalette.Resolve(_tile.Kind, _tile.Settings);
            using var dlg = new ColorDialog
            {
                Color = Color.FromArgb(current.Alpha, current.Red, current.Green, current.Blue),
                FullOpen = true,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                var c = dlg.Color;
                _tile.Settings.AccentColor = new SKColor(c.R, c.G, c.B);
                _surface.Invalidate();
            }
            return;
        }

        if (layout.Reset.Contains(p.X, p.Y))
        {
            _tile.Settings.AccentColor = null;
            _surface.Invalidate();
            return;
        }
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        int w = e.Info.Width;
        int h = e.Info.Height;
        if (_theme.Backdrop == WindowBackdrop.Opaque)
        {
            using (var bg = _theme.BackgroundPaint())
                canvas.DrawRect(0, 0, w, h, bg);
        }
        else
        {
            canvas.Clear(SKColors.Transparent);
        }
        var rect = TileRect();
        var accent = TilePalette.Resolve(_tile.Kind, _tile.Settings);
        _tile.Draw(canvas, rect);
        _renderer.DrawGrabHandle(canvas, rect, _hoverHandle, accent);
        _renderer.DrawGear(canvas, rect, _hoverGear, accent);
        if (_paneOpen)
        {
            var v = TileVisual();
            var layout = _renderer.ComputePaneLayout(rect, ClientRect(), v, _tile, _deviceState);
            if (layout != null)
                _renderer.DrawSettingsPane(canvas, layout, v, _hoverPaneClose, _tile, _deviceState, _hoverDeviceRow, _hoverRemove);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _onReattach();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderTimer.Dispose();
            _surface.Dispose();
        }
        base.Dispose(disposing);
    }
}
