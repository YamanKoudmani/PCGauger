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
    private readonly TileRenderer _renderer;
    private readonly Theme _theme = new();
    private readonly RollingHistory _cpuHistory = new(TimeSpan.FromSeconds(60));
    private readonly RollingHistory _memHistory = new(TimeSpan.FromSeconds(60));
    private readonly RollingHistory _gpuHistory = new(TimeSpan.FromSeconds(60));
    private readonly RollingHistory _diskHistory = new(TimeSpan.FromSeconds(60));

    // Tiles currently shown in THIS window. Detaching removes one and opens a
    // DetachedTileForm; closing that form re-attaches it here.
    private readonly List<Tile> _tiles = new();
    private readonly List<DetachedTileForm> _detached = new();

    private readonly System.Threading.Timer _renderTimer;

    public MainForm()
    {
        Text = "PCGauger";
        ClientSize = new System.Drawing.Size(420, 560);
        BackColor = System.Drawing.Color.FromArgb(0x0E, 0x11, 0x16);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;

        _surface = new HitTestSurface
        {
            Dock = DockStyle.Fill,
            BackColor = System.Drawing.Color.FromArgb(0x0E, 0x11, 0x16),
        };
        // Claim the grab-handle regions so clicks there reach MouseDown
        // (detach) instead of starting a window drag. Everywhere else the
        // surface stays transparent to hits and the form handles drag/resize.
        _surface.HitTestOverride = p =>
        {
            foreach (var hr in HandleRects())
                if (hr.Contains(p.X, p.Y)) return 1; // HTCLIENT
            return 0;
        };
        _surface.PaintSurface += OnPaintSurface;
        _surface.MouseMove += OnSurfaceMouseMove;
        _surface.MouseDown += OnSurfaceMouseDown;
        Controls.Add(_surface);

        _cpu = new CpuProvider();
        _mem = new MemoryProvider();
        _top = new TopProcessProvider();
        _gpu = new GpuProvider();
        _disk = new StorageProvider();
        _renderer = new TileRenderer(_theme);

        _poller = new MetricPoller(new IMetricProvider[] { _cpu, _top, _gpu, _disk }, TimeSpan.FromMilliseconds(1000));
        _memPoller = new MetricPoller(new IMetricProvider[] { _mem }, TimeSpan.FromMilliseconds(1000));
        _poller.Start();
        _memPoller.Start();

        // Initial tile set. Order here is the default grid order; the grid
        // reflows automatically as tiles are added/removed.
        _tiles.Add(new Tile(TileKind.Cpu, "CPU", DrawCpu));
        _tiles.Add(new Tile(TileKind.Ram, "RAM", DrawRam));
        _tiles.Add(new Tile(TileKind.Gpu, "GPU", DrawGpu));
        _tiles.Add(new Tile(TileKind.Disk, "DISK", DrawDisk));

        _renderTimer = new System.Threading.Timer(_ => _surface.Invalidate(), null, 0, 33);
    }

    // ---- Tile draw callbacks (bound to current snapshots) ----
    private void DrawCpu(SKCanvas c, SKRect r)
    {
        var snap = _poller.GetSnapshot(typeof(CpuProvider));
        double usage = MetricValue(snap, "cpu.aggregate");
        _cpuHistory.Push(usage);
        _renderer.DrawCpuTile(c, r, usage, _cpu.CurrentMhz, Environment.ProcessorCount, _cpuHistory.Snapshot());
    }
    private void DrawRam(SKCanvas c, SKRect r)
    {
        var snap = _memPoller.GetSnapshot(typeof(MemoryProvider));
        double load = MetricValue(snap, "mem.load");
        _memHistory.Push(load);
        _renderer.SetRamHistory(_memHistory.Snapshot());
        _renderer.DrawRamTile(c, r, load, _mem.UsedPhys, _mem.TotalPhys);
    }
    private void DrawGpu(SKCanvas c, SKRect r)
    {
        var snap = _poller.GetSnapshot(typeof(GpuProvider));
        double util = MetricValue(snap, "gpu.util");
        _gpuHistory.Push(util);
        _renderer.SetGpuHistory(_gpuHistory.Snapshot());
        _renderer.DrawGpuTile(c, r, util, _gpu.VramUsed, _gpu.VramBudget);
    }
    private void DrawDisk(SKCanvas c, SKRect r)
    {
        var snap = _poller.GetSnapshot(typeof(StorageProvider));
        double pct = MetricValue(snap, "disk.load");
        _diskHistory.Push(pct);
        _renderer.SetDiskHistory(_diskHistory.Snapshot());
        _renderer.DrawDiskTile(c, r, pct, _disk.TotalBytes - _disk.FreeBytes, _disk.TotalBytes, _disk.BytesPerSec);
    }

    // ---- Detach / re-attach (grab handle only) ----
    // Index of the tile whose grab handle the cursor is currently over, or -1.
    // Drives the hover highlight and the SizeAll cursor affordance.
    private int _hoverHandle = -1;

    private List<SKRect> HandleRects()
    {
        float gap = 12;
        var area = new SKRect(gap, gap, ClientSize.Width - gap, ClientSize.Height - gap);
        var rects = GridLayout.Compute(area, _tiles.Count, gap);
        var handles = new List<SKRect>(_tiles.Count);
        foreach (var r in rects) handles.Add(TileRenderer.GrabHandleRect(r));
        return handles;
    }

    private void OnSurfaceMouseMove(object? sender, MouseEventArgs e)
    {
        var handles = HandleRects();
        int hit = -1;
        for (int i = 0; i < handles.Count; i++)
        {
            if (handles[i].Contains(e.Location.X, e.Location.Y)) { hit = i; break; }
        }
        if (hit != _hoverHandle)
        {
            _hoverHandle = hit;
            _surface.Cursor = hit >= 0 ? Cursors.SizeAll : Cursors.Default;
            _surface.Invalidate();
        }
    }

    private void OnSurfaceMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var handles = HandleRects();
        for (int i = 0; i < handles.Count; i++)
        {
            if (handles[i].Contains(e.Location.X, e.Location.Y)) { Detach(_tiles[i]); return; }
        }
    }

    private void Detach(Tile tile)
    {
        _tiles.Remove(tile);
        _hoverHandle = -1;
        DetachedTileForm form = null!;
        form = new DetachedTileForm(tile, _theme, () => Reattach(tile, form));
        _detached.Add(form);
        form.Show();
        _surface.Invalidate();
    }

    private void Reattach(Tile tile, DetachedTileForm form)
    {
        _detached.Remove(form);
        if (!_tiles.Contains(tile)) _tiles.Add(tile);
        _surface.Invalidate();
    }

    // --- Borderless drag + resize ---
    private const int ResizeMargin = 8;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int HTCAPTION = 2;
    private const int HTCLIENT = 1;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            var pt = PointToClient(new System.Drawing.Point(
                m.LParam.ToInt32() & 0xFFFF, m.LParam.ToInt32() >> 16));

            // Over a grab handle, return HTCLIENT so Windows queries the child
            // SKControl (which also returns HTCLIENT there via HitTestOverride),
            // delivering the click to MouseDown for detach. Without this the
            // form would answer HTCAPTION and start a window drag instead.
            foreach (var hr in HandleRects())
            {
                if (hr.Contains(pt.X, pt.Y)) { m.Result = (IntPtr)HTCLIENT; return; }
            }

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
        base.WndProc(ref m);
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        int w = e.Info.Width;
        int h = e.Info.Height;

        using (var bg = _theme.BackgroundPaint())
            canvas.DrawRect(0, 0, w, h, bg);

        float gap = 12;
        var area = new SKRect(gap, gap, w - gap, h - gap);
        var rects = GridLayout.Compute(area, _tiles.Count, gap);
        for (int i = 0; i < _tiles.Count; i++)
            _tiles[i].Draw(canvas, rects[i]);
        // Grab handles on top of each tile (hover-highlighted when active).
        for (int i = 0; i < _tiles.Count; i++)
            _renderer.DrawGrabHandle(canvas, rects[i], i == _hoverHandle);

        // Top-process footer line (only when there's room below the grid).
        var topSnap = _poller.GetSnapshot(typeof(TopProcessProvider));
        DrawTopProcessLine(canvas, w, h, topSnap);
    }

    private void DrawTopProcessLine(SKCanvas canvas, int w, int h, IReadOnlyList<Metric> topSnap)
    {
        string cpuName = TextValue(topSnap, "proc.topcpu.name");
        double cpuPct = MetricValue(topSnap, "proc.topcpu.pct");
        string ramName = TextValue(topSnap, "proc.topram.name");
        ulong ramBytes = (ulong)MetricValue(topSnap, "proc.topram.bytes");

        using var paint = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 12);
        string line = $"Top CPU: {cpuName} {cpuPct:0.#}%    Top RAM: {ramName} {Format.Bytes(ramBytes)}";
        canvas.DrawText(line, 14, h - 8, font, paint);
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
    private readonly Theme _theme;
    private readonly Action _onReattach;
    private bool _hoverHandle;
    private readonly System.Threading.Timer _renderTimer;

    public DetachedTileForm(Tile tile, Theme theme, Action onReattach)
    {
        _tile = tile;
        _renderer = new TileRenderer(theme);
        _theme = theme;
        _onReattach = onReattach;

        Text = "PCGauger";
        ClientSize = new System.Drawing.Size(300, 220);
        BackColor = System.Drawing.Color.FromArgb(0x0E, 0x11, 0x16);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;

        _surface = new HitTestSurface
        {
            Dock = DockStyle.Fill,
            BackColor = System.Drawing.Color.FromArgb(0x0E, 0x11, 0x16),
        };
        _surface.PaintSurface += OnPaintSurface;
        _surface.MouseMove += OnSurfaceMouseMove;
        _surface.MouseDown += OnSurfaceMouseDown;
        Controls.Add(_surface);

        _renderTimer = new System.Threading.Timer(_ => _surface.Invalidate(), null, 0, 33);
    }

    private SKRect TileRect()
    {
        float gap = 12;
        return new SKRect(gap, gap, ClientSize.Width - gap, ClientSize.Height - gap);
    }

    private void OnSurfaceMouseMove(object? sender, MouseEventArgs e)
    {
        bool over = TileRenderer.GrabHandleRect(TileRect()).Contains(e.Location.X, e.Location.Y);
        if (over != _hoverHandle)
        {
            _hoverHandle = over;
            _surface.Cursor = over ? Cursors.SizeAll : Cursors.Default;
            _surface.Invalidate();
        }
    }

    private void OnSurfaceMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left &&
            TileRenderer.GrabHandleRect(TileRect()).Contains(e.Location.X, e.Location.Y))
        {
            _onReattach();
            Close();
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
        _tile.Draw(canvas, rect);
        _renderer.DrawGrabHandle(canvas, rect, _hoverHandle);
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
