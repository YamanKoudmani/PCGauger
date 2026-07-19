using System.Drawing;
using System.Windows.Forms;
using PCGauger.Infrastructure;
using PCGauger.Metrics;
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
    private readonly GpuProvider _gpu;
    private readonly StorageProvider _disk;
    private readonly NetworkProvider _net;
    private readonly TileRenderer _renderer;
    private Theme _theme;
    private readonly AppConfig _config;
    private readonly RollingHistory _cpuHistory = new(TimeSpan.FromSeconds(60));
    private readonly RollingHistory _memHistory = new(TimeSpan.FromSeconds(60));
    private readonly RollingHistory _gpuHistory = new(TimeSpan.FromSeconds(60));
    private readonly RollingHistory _diskReadHistory = new(TimeSpan.FromSeconds(60));
    private readonly RollingHistory _diskWriteHistory = new(TimeSpan.FromSeconds(60));
    private readonly RollingHistory _netHistory = new(TimeSpan.FromSeconds(60));
    private readonly RollingHistory _netUpHistory = new(TimeSpan.FromSeconds(60));

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
    private bool _hoverGlobalClose;
    private int _globalHoverRow = -1;

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

    public MainForm()
    {
        _config = AppConfig.Load();

        Text = "PCGauger";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Color.Black;

        // Apply persisted theme + units + threshold before building anything.
        _theme = Theme.FromName(_config.ThemeName);
        Format.Units = _config.Units;
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
        _gpu = new GpuProvider();
        _disk = new StorageProvider();
        _net = new NetworkProvider();
        _renderer = new TileRenderer(_theme);

        _poller = new MetricPoller(new IMetricProvider[] { _cpu, _top, _gpu, _disk, _net }, TimeSpan.FromMilliseconds(1000));
        _memPoller = new MetricPoller(new IMetricProvider[] { _mem }, TimeSpan.FromMilliseconds(1000));
        _poller.Start();
        _memPoller.Start();

        // Master tile set, ordered by canonical TileKind slot. _tiles holds only
        // the *enabled* tiles; disabling a kind removes it from _tiles (and
        // closes its detached window) without dropping it from _allTiles.
        Tile cpuTile = null!, ramTile = null!, gpuTile = null!, diskTile = null!, netTile = null!;
        cpuTile = new Tile(TileKind.Cpu, "CPU", (c, r) => DrawCpu(c, r, cpuTile));
        ramTile = new Tile(TileKind.Ram, "RAM", (c, r) => DrawRam(c, r, ramTile));
        gpuTile = new Tile(TileKind.Gpu, "GPU", (c, r) => DrawGpu(c, r, gpuTile));
        diskTile = new Tile(TileKind.Disk, "DISK", (c, r) => DrawDisk(c, r, diskTile));
        netTile = new Tile(TileKind.Network, "NET", (c, r) => DrawNet(c, r, netTile));
        _allTiles = new List<Tile> { cpuTile, ramTile, gpuTile, diskTile, netTile };

        // Build the visible set from persisted enabled flags, then apply any
        // custom display order (TileOrder) so a saved layout is restored.
        foreach (var t in _allTiles)
        {
            _config.Tile(t.Kind).ApplyTo(t.Settings);
            if (_config.Tile(t.Kind).Enabled)
                _tiles.Add(t);
        }
        ApplyTileOrder();

        ApplyWindowBounds();

        // Apply persisted always-on-top to this window (detached forms read the
        // value when constructed later).
        TopMost = _config.AlwaysOnTop;

        _renderTimer = new System.Windows.Forms.Timer { Interval = 33 };
        _renderTimer.Tick += (_, _) => _surface.Invalidate();
        _renderTimer.Start();

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
            Bounds = screen.Bounds; // fill the mini panel
        }
        else
        {
            var b = _floatingBounds;
            if (b.X == 0 && b.Y == 0 && b.Width == 0 && b.Height == 0)
                StartPosition = FormStartPosition.CenterScreen;
            else
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = b;
            }
        }
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
        found = exact ?? fallback!;
        return found != null;
    }

    private void SetKiosk(bool on)
    {
        _config.KioskMode = on;
        if (on && TryFindMiniPanel(out var screen))
        {
            _floatingBounds = Bounds; // remember floating position for restore
            Bounds = screen.Bounds;
        }
        else
        {
            // Restore floating window at saved/centered bounds.
            var b = _floatingBounds;
            StartPosition = FormStartPosition.Manual;
            Bounds = b;
        }
        _config.Save();
        _surface.Invalidate();
    }

    // ---- Tile draw callbacks (bound to current snapshots) ----
    private void DrawCpu(SKCanvas c, SKRect r, Tile tile)
    {
        var snap = _poller.GetSnapshot(typeof(CpuProvider));
        double usage = MetricValue(snap, "cpu.aggregate");
        _cpuHistory.Push(usage);
        _renderer.DrawCpuTile(c, r, tile.Settings, TilePalette.Resolve(tile.Kind, tile.Settings),
            usage, _cpu.CurrentMhz, _cpu.PhysicalCores, _cpu.LogicalProcessors, _cpuHistory.DecimatedSnapshot(1024), _cpuHistory.Window);
    }
    private void DrawRam(SKCanvas c, SKRect r, Tile tile)
    {
        var snap = _memPoller.GetSnapshot(typeof(MemoryProvider));
        double load = MetricValue(snap, "mem.load");
        _memHistory.Push(load);
        _renderer.SetRamHistory(_memHistory.DecimatedSnapshot(1024), _memHistory.Window);
        _renderer.DrawRamTile(c, r, tile.Settings, TilePalette.Resolve(tile.Kind, tile.Settings),
            load, _mem.UsedPhys, _mem.TotalPhys);
    }
    private void DrawGpu(SKCanvas c, SKRect r, Tile tile)
    {
        var snap = _poller.GetSnapshot(typeof(GpuProvider));
        double util = MetricValue(snap, "gpu.util");
        _gpuHistory.Push(util);
        _renderer.SetGpuHistory(_gpuHistory.DecimatedSnapshot(1024), _gpuHistory.Window);
        _renderer.DrawGpuTile(c, r, tile.Settings, TilePalette.Resolve(tile.Kind, tile.Settings),
            util, _gpu.VramUsed, _gpu.VramBudget);
    }
    private void DrawDisk(SKCanvas c, SKRect r, Tile tile)
    {
        var snap = _poller.GetSnapshot(typeof(StorageProvider));
        double pct = MetricValue(snap, "disk.load");
        _diskReadHistory.Push(_disk.ReadBytesPerSec);
        _diskWriteHistory.Push(_disk.WriteBytesPerSec);
        _renderer.SetDiskHistory(_diskReadHistory.DecimatedSnapshot(1024), _diskWriteHistory.DecimatedSnapshot(1024), _diskReadHistory.Window);
        _renderer.DrawDiskTile(c, r, tile.Settings, TilePalette.Resolve(tile.Kind, tile.Settings),
            pct, _disk.TotalBytes - _disk.FreeBytes, _disk.TotalBytes, _disk.BytesPerSec);
    }
    private void DrawNet(SKCanvas c, SKRect r, Tile tile)
    {
        var snap = _poller.GetSnapshot(typeof(NetworkProvider));
        double down = MetricValue(snap, "net.down");
        double up = MetricValue(snap, "net.up");
        string iface = TextValue(snap, "net.name");
        _netHistory.Push(down);
        _netUpHistory.Push(up);
        _renderer.SetNetHistory(_netHistory.DecimatedSnapshot(1024), _netUpHistory.DecimatedSnapshot(1024), _netHistory.Window);
        _renderer.DrawNetworkTile(c, r, tile.Settings, TilePalette.Resolve(tile.Kind, tile.Settings),
            down, up, iface);
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

    private List<SKRect> TileRects()
    {
        float gap = 12;
        return (List<SKRect>)GridLayout.Compute(GridArea(), _tiles.Count, gap);
    }

    private List<SKRect> HandleRects() => TileRects().Select(TileRenderer.GrabHandleRect).ToList();
    private List<SKRect> GearRects() => TileRects().Select(TileRenderer.GearRect).ToList();

    /// <summary>The footer-gear hit rect: a 24px square at the far right of the status band.</summary>
    private SKRect FooterGearRect()
    {
        float size = 22;
        float y = ClientSize.Height - FooterHeight + (FooterHeight - size) / 2f;
        return new SKRect(ClientSize.Width - size - 8, y, ClientSize.Width - 8, y + size);
    }

    private TileRenderer.PaneLayout? PaneLayoutFor(Tile tile, SKRect rect)
    {
        if (_openPaneTile != tile) return null;
        var v = new TileVisual(tile.Kind, tile.Settings, TilePalette.Resolve(tile.Kind, tile.Settings), _theme, false);
        return _renderer.ComputePaneLayout(rect, ClientRect(), v);
    }

    private TileRenderer.GlobalPaneLayout? GlobalPaneLayout() => _renderer.ComputeGlobalPaneLayout(ClientRect());

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

        if (_openPaneTile != null)
        {
            int idx = _tiles.IndexOf(_openPaneTile);
            if (idx >= 0)
            {
                var layout = PaneLayoutFor(_openPaneTile, TileRects()[idx]);
                if (layout != null && layout.Pane.Contains(p.X, p.Y)) return 1;
            }
        }
        if (_globalPaneOpen)
        {
            var g = GlobalPaneLayout();
            if (g != null && g.Pane.Contains(p.X, p.Y)) return 1;
        }
        return 0;
    }

    // ---- Mouse interaction ----
    private void OnSurfaceMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        // 1) Open tile pane takes precedence.
        if (_openPaneTile != null)
        {
            int idx = _tiles.IndexOf(_openPaneTile);
            if (idx >= 0)
            {
                var layout = PaneLayoutFor(_openPaneTile, TileRects()[idx]);
                if (layout != null && layout.Pane.Contains(e.Location.X, e.Location.Y))
                {
                    HandlePaneClick(_openPaneTile, layout, e.Location);
                    return;
                }
            }
        }

        // 2) Open global pane takes precedence.
        if (_globalPaneOpen)
        {
            var g = GlobalPaneLayout();
            if (g != null && g.Pane.Contains(e.Location.X, e.Location.Y))
            {
                HandleGlobalPaneClick(g, e.Location);
                return;
            }
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
                }
            }
        }
        bool footerGearHit = FooterGearRect().Contains(e.Location.X, e.Location.Y);
        if (_globalPaneOpen)
        {
            var g = GlobalPaneLayout();
            if (g != null && g.Pane.Contains(e.Location.X, e.Location.Y))
            {
                globalCloseHit = g.Close.Contains(e.Location.X, e.Location.Y);
                globalRow = GlobalRowAt(g, e.Location);
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
        if (globalCloseHit != _hoverGlobalClose) { _hoverGlobalClose = globalCloseHit; changed = true; }
        if (globalRow != _globalHoverRow) { _globalHoverRow = globalRow; changed = true; }

        if (changed)
        {
            bool hand = gearHit >= 0 || _hoverPaneClose || _hoverSwatch >= 0 || footerGearHit
                || _hoverGlobalClose || _globalHoverRow >= 0;
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
        if (e.KeyCode == Keys.Escape && _dragState != DragState.None)
        {
            CancelDrag();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void TogglePane(Tile tile)
    {
        if (_globalPaneOpen) { _globalPaneOpen = false; _globalHoverRow = -1; }
        _openPaneTile = (_openPaneTile == tile) ? null : tile;
        _hoverSwatch = -1;
        _hoverPaneClose = false;
        _surface.Invalidate();
    }

    private void ToggleGlobalPane()
    {
        if (_openPaneTile != null) _openPaneTile = null;
        _globalPaneOpen = !_globalPaneOpen;
        _hoverGlobalClose = false;
        _globalHoverRow = -1;
        _surface.Invalidate();
    }

    private void HandlePaneClick(Tile tile, TileRenderer.PaneLayout layout, Point p)
    {
        if (layout.Close.Contains(p.X, p.Y)) { _openPaneTile = null; _surface.Invalidate(); return; }

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
                var tc = _config.Tile(tile.Kind);
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

        for (int i = 0; i < layout.Swatches.Count; i++)
        {
            if (layout.Swatches[i].Contains(p.X, p.Y))
            {
                tile.Settings.AccentColor = TileRenderer.SwatchPalette()[i];
                _config.Tile(tile.Kind).AccentArgb = (uint)((tile.Settings.AccentColor.Value.Alpha << 24) | (tile.Settings.AccentColor.Value.Red << 16) | (tile.Settings.AccentColor.Value.Green << 8) | tile.Settings.AccentColor.Value.Blue);
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
                _config.Tile(tile.Kind).AccentArgb = (uint)((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);
                _config.Save();
                _surface.Invalidate();
            }
            return;
        }

        if (layout.Reset.Contains(p.X, p.Y))
        {
            tile.Settings.AccentColor = null;
            _config.Tile(tile.Kind).AccentArgb = null;
            _config.Save();
            _surface.Invalidate();
            return;
        }
    }

    private int GlobalRowAt(TileRenderer.GlobalPaneLayout g, Point p)
    {
        if (g.LaunchToggle.Contains(p.X, p.Y)) return 0;
        for (int i = 0; i < g.UnitsSegments.Count; i++) if (g.UnitsSegments[i].Contains(p.X, p.Y)) return 1;
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
        for (int i = 0; i < g.UnitsSegments.Count; i++)
        {
            if (g.UnitsSegments[i].Contains(p.X, p.Y))
            {
                _config.Units = (UnitsMode)i;
        Format.Units = _config.Units;
        Format.ValueDecimals = _config.ValueDecimals;
                _config.Save();
                _surface.Invalidate();
                return;
            }
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

        // Tile enable/disable chips (canonical order Cpu, Ram, Gpu, Disk, Net).
        var chipKinds = new[] { TileKind.Cpu, TileKind.Ram, TileKind.Gpu, TileKind.Disk, TileKind.Network };
        for (int i = 0; i < g.TileChips.Count; i++)
        {
            if (g.TileChips[i].Contains(p.X, p.Y))
            {
                SetTileEnabled(chipKinds[i], !_config.Tile(chipKinds[i]).Enabled);
                return;
            }
        }
    }

    /// <summary>
    /// Enables or disables a tile kind. Disabling closes its detached window (if
    /// open) and removes it from the visible set; enabling re-inserts it at its
    /// canonical slot. Visible tiles always derive from _allTiles filtered by the
    /// persisted Enabled flag, so the grid reflows automatically.
    /// </summary>
    private void SetTileEnabled(TileKind kind, bool enabled)
    {
        _config.Tile(kind).Enabled = enabled;
        _config.Save();

        if (!enabled)
        {
            // Close any detached window for this kind (its reattach is a no-op
            // because the tile is now disabled). Also drop an open settings pane.
            foreach (var d in _detached.ToArray())
                if (d.TileKind == kind) d.Close();
            if (_openPaneTile != null && _openPaneTile.Kind == kind)
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
            if (_config.Tile(t.Kind).Enabled)
                _tiles.Add(t);
        ApplyTileOrder();
    }

    /// <summary>Reorders the current _tiles to match the persisted TileOrder. When
    /// TileOrder is empty (no custom order yet) the canonical _allTiles order is
    /// kept. Enabled kinds missing from the list are appended; kinds in the list
    /// that are disabled are skipped.</summary>
    private void ApplyTileOrder()
    {
        if (_config.TileOrder.Count == 0) return;
        var byKind = _tiles.ToDictionary(t => t.Kind);
        var ordered = new List<Tile>(_tiles.Count);
        foreach (var name in _config.TileOrder)
        {
            if (Enum.TryParse<TileKind>(name, out var kind) && byKind.TryGetValue(kind, out var t))
            {
                ordered.Add(t);
                byKind.Remove(kind);
            }
        }
        // Append any enabled kinds not present in the saved order (e.g. newly
        // enabled, or added after the order was saved).
        ordered.AddRange(byKind.Values);
        _tiles.Clear();
        _tiles.AddRange(ordered);
    }

    private void SaveTileOrder()
    {
        _config.TileOrder = _tiles.Select(t => t.Kind.ToString()).ToList();
        _config.Save();
    }

    private void ApplyTheme(Theme t)
    {
        _theme = t;
        _config.ThemeName = t.Name;
        _renderer.Theme = t;
        BackColor = Color.FromArgb(t.Background.Red, t.Background.Green, t.Background.Blue);
        _surface.BackColor = BackColor;
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
        _cpuHistory.SetWindow(w);
        _memHistory.SetWindow(w);
        _gpuHistory.SetWindow(w);
        _diskReadHistory.SetWindow(w);
        _diskWriteHistory.SetWindow(w);
        _netHistory.SetWindow(w);
        _netUpHistory.SetWindow(w);
        _surface.Invalidate();
    }

    private void ClearHandleHover()
    {
        if (_hoverHandle == -1 && _hoverGear == -1 && !_hoverPaneClose && _hoverSwatch == -1
            && !_hoverFooterGear && !_hoverGlobalClose && _globalHoverRow == -1) return;
        _hoverHandle = -1;
        _hoverGear = -1;
        _hoverPaneClose = false;
        _hoverSwatch = -1;
        _hoverFooterGear = false;
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

        Point pos = _config.TryGetDetachedPosition(tile.Kind, out var saved)
            ? saved
            : new Point(Cursor.Position.X - 150, Cursor.Position.Y - 20);

        DetachedTileForm form = null!;
        form = new DetachedTileForm(tile, _theme, pos, _config.AlwaysOnTop, () => Reattach(tile, form));
        _detached.Add(form);
        form.Show();
        _surface.Invalidate();
        _surface.Capture = false;
        SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    private void Reattach(Tile tile, DetachedTileForm form)
    {
        _detached.Remove(form);
        _config.SetDetachedPosition(tile.Kind, form.Location);
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
            if (_openPaneTile != null) { _openPaneTile = null; _surface.Invalidate(); }
            if (_globalPaneOpen) { _globalPaneOpen = false; _surface.Invalidate(); }
        }
        if (m.Msg == WM_NCMOUSEMOVE) ClearHandleHover();
        base.WndProc(ref m);
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        int w = e.Info.Width;
        int h = e.Info.Height;

        using (var bg = _theme.BackgroundPaint())
            canvas.DrawRect(0, 0, w, h, bg);

        var rects = GridLayout.Compute(GridArea(), _tiles.Count, 12);
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
                var layout = _renderer.ComputePaneLayout(rects[idx], ClientRect(), v);
                if (layout != null)
                    _renderer.DrawSettingsPane(canvas, layout, v, _hoverPaneClose);
            }
        }

        if (_globalPaneOpen)
        {
            var g = GlobalPaneLayout();
            if (g != null)
                _renderer.DrawGlobalPane(canvas, g, _config, _theme, _hoverGlobalClose, _globalHoverRow);
        }
    }

    private void DrawFooter(SKCanvas canvas, int w, int h)
    {
        float bandTop = h - FooterHeight;
        using var band = new SKPaint { Color = _theme.FooterBand, Style = SKPaintStyle.Fill };
        canvas.DrawRect(0, bandTop, w, FooterHeight, band);
        using var sep = new SKPaint { Color = _theme.TileBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawLine(0, bandTop, w, bandTop, sep);

        var topSnap = _poller.GetSnapshot(typeof(TopProcessProvider));
        string cpuName = TextValue(topSnap, "proc.topcpu.name");
        double cpuPct = MetricValue(topSnap, "proc.topcpu.pct");
        string ramName = TextValue(topSnap, "proc.topram.name");
        ulong ramBytes = (ulong)MetricValue(topSnap, "proc.topram.bytes");
        string gpuName = TextValue(topSnap, "proc.topgpu.name");
        double gpuPct = MetricValue(topSnap, "proc.topgpu.pct");

        using var labelPaint = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
        using var labelFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 12);
        using var valueFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI Semibold"), 12);
        float cy = bandTop + FooterHeight / 2f + 4;
        float x = 14;
        float gap = 22;

        // Effective accent per kind (honors per-tile color overrides).
        SKColor AccentOf(TileKind kind)
        {
            var tile = _allTiles.Find(t => t.Kind == kind);
            return tile != null ? TilePalette.Resolve(kind, tile.Settings) : TilePalette.DefaultFor(kind);
        }

        // "CPU Top: chrome 12.5%" — muted label, bold measurement in the tile's color.
        void Segment(string label, string value, SKColor color)
        {
            canvas.DrawText(label, x, cy, labelFont, labelPaint);
            x += labelFont.MeasureText(label);
            using var cp = new SKPaint { Color = color, IsAntialias = true };
            canvas.DrawText(value, x, cy, valueFont, cp);
            x += valueFont.MeasureText(value) + gap;
        }

        Segment("CPU Top: ", $"{cpuName} {Format.Percent(cpuPct)}", AccentOf(TileKind.Cpu));
        Segment("RAM Top: ", $"{ramName} {Format.Bytes(ramBytes)}", AccentOf(TileKind.Ram));
        Segment("GPU Top: ", $"{gpuName} {Format.Percent(gpuPct)}", AccentOf(TileKind.Gpu));

        // Footer gear at far right.
        _renderer.DrawGearAt(canvas, FooterGearRect(), _hoverFooterGear, _theme.Accent);
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
    private bool _hoverHandle;
    private bool _hoverGear;
    private bool _paneOpen;
    private bool _hoverPaneClose;
    private int _hoverSwatch = -1;
    private readonly System.Windows.Forms.Timer _renderTimer;

    public TileKind TileKind => _tile.Kind;

    public DetachedTileForm(Tile tile, Theme theme, Point location, bool topMost, Action onReattach)
    {
        _tile = tile;
        _renderer = new TileRenderer(theme);
        _theme = theme;
        _onReattach = onReattach;

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
    }

    public void SetTheme(Theme t)
    {
        _theme = t;
        _renderer.Theme = t;
        BackColor = Color.FromArgb(t.Background.Red, t.Background.Green, t.Background.Blue);
        _surface.BackColor = BackColor;
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
        if (_paneOpen)
        {
            var layout = _renderer.ComputePaneLayout(rect, ClientRect(), TileVisual());
            if (layout != null && layout.Pane.Contains(p.X, p.Y)) return 1;
        }
        return 0;
    }

    private const int ResizeMargin = 8;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCMOUSEMOVE = 0x00A0;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int HTCAPTION = 2;
    private const int HTCLIENT = 1;

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
            _surface.Invalidate();
        }
        if (m.Msg == WM_NCMOUSEMOVE) ClearHandleHover();
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

    private void OnSurfaceMouseLeave(object? sender, EventArgs e) => ClearHandleHover();

    private void OnSurfaceMouseMove(object? sender, MouseEventArgs e)
    {
        var rect = TileRect();
        bool handleOver = TileRenderer.GrabHandleRect(rect).Contains(e.Location.X, e.Location.Y);
        bool gearOver = TileRenderer.GearRect(rect).Contains(e.Location.X, e.Location.Y);
        bool closeOver = false;
        int swatchOver = -1;
        if (_paneOpen)
        {
            var layout = _renderer.ComputePaneLayout(rect, ClientRect(), TileVisual());
            if (layout != null)
            {
                closeOver = layout.Close.Contains(e.Location.X, e.Location.Y);
                for (int i = 0; i < layout.Swatches.Count; i++)
                    if (layout.Swatches[i].Contains(e.Location.X, e.Location.Y)) { swatchOver = i; break; }
            }
        }

        if (handleOver != _hoverHandle || gearOver != _hoverGear || closeOver != _hoverPaneClose || swatchOver != _hoverSwatch)
        {
            _hoverHandle = handleOver;
            _hoverGear = gearOver;
            _hoverPaneClose = closeOver;
            _hoverSwatch = swatchOver;
            _surface.Cursor = handleOver ? Cursors.SizeAll
                : (gearOver || closeOver || swatchOver >= 0) ? Cursors.Hand
                : Cursors.Default;
            _surface.Invalidate();
        }
    }

    private void OnSurfaceMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var rect = TileRect();

        if (_paneOpen)
        {
            var layout = _renderer.ComputePaneLayout(rect, ClientRect(), TileVisual());
            if (layout != null && layout.Pane.Contains(e.Location.X, e.Location.Y))
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
        if (layout.Close.Contains(p.X, p.Y)) { _paneOpen = false; _surface.Invalidate(); return; }

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
        using (var bg = _theme.BackgroundPaint())
            canvas.DrawRect(0, 0, w, h, bg);
        var rect = TileRect();
        var accent = TilePalette.Resolve(_tile.Kind, _tile.Settings);
        _tile.Draw(canvas, rect);
        _renderer.DrawGrabHandle(canvas, rect, _hoverHandle, accent);
        _renderer.DrawGear(canvas, rect, _hoverGear, accent);
        if (_paneOpen)
        {
            var v = TileVisual();
            var layout = _renderer.ComputePaneLayout(rect, ClientRect(), v);
            if (layout != null)
                _renderer.DrawSettingsPane(canvas, layout, v, _hoverPaneClose);
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
