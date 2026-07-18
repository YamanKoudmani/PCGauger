using System.Windows.Forms;
using PCGauger.Infrastructure;
using PCGauger.Metrics;
using PCGauger.Metrics.Providers;
using PCGauger.Rendering;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace PCGauger;

/// <summary>
/// Host window. Owns the SkiaSharp surface, the metric poller, and the rolling
/// history buffers. The poller runs on a timer thread; rendering happens on the
/// UI thread via the SKControl Paint event, which reads the latest snapshots.
/// </summary>
public sealed class MainForm : Form
{
    private readonly HitTestSurface _surface;
    private readonly MetricPoller _poller;
    private readonly CpuProvider _cpu;
    private readonly MemoryProvider _mem;
    private readonly TopProcessProvider _top;
    private readonly TileRenderer _renderer;
    private readonly Theme _theme = new();
    private readonly RollingHistory _cpuHistory = new(TimeSpan.FromSeconds(60));
    private readonly RollingHistory _memHistory = new(TimeSpan.FromSeconds(60));

    private readonly System.Threading.Timer _renderTimer;

    public MainForm()
    {
        Text = "PCGauger";
        // Reasonably sized for a small display; user drags it into place (v1).
        ClientSize = new System.Drawing.Size(420, 520);
        BackColor = System.Drawing.Color.FromArgb(0x0E, 0x11, 0x16);
        // No title bar (user request) and not reachable via Alt-Tab (user request).
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;

        _surface = new HitTestSurface
        {
            Dock = DockStyle.Fill,
            BackColor = System.Drawing.Color.FromArgb(0x0E, 0x11, 0x16),
        };
        _surface.PaintSurface += OnPaintSurface;
        Controls.Add(_surface);

        _cpu = new CpuProvider();
        _mem = new MemoryProvider();
        _top = new TopProcessProvider();
        _renderer = new TileRenderer(_theme);

        // CPU polled at 1s (smooth enough to read; raw 250ms deltas are too
        // jumpy to be useful). RAM at 1s too. The provider applies an EMA so the
        // displayed value is stable rather than flickering between samples.
        _poller = new MetricPoller(new IMetricProvider[] { _cpu, _top }, TimeSpan.FromMilliseconds(1000));
        var memPoller = new MetricPoller(new IMetricProvider[] { _mem }, TimeSpan.FromMilliseconds(1000));

        _poller.Start();
        memPoller.Start();
        _memPollerRef = memPoller;

        // Render at ~30fps; cheap and smooth enough for bars + sparklines.
        _renderTimer = new System.Threading.Timer(_ => _surface.Invalidate(), null, 0, 33);
    }

    private MetricPoller _memPollerRef;

    // --- Borderless drag + resize ---
    // With FormBorderStyle.None there's no caption bar to drag or edge to grab,
    // so we handle WM_NCHITTEST ourselves: the window body drags, and a margin
    // around the edges resizes. Keeps it movable/resizable without a title bar.
    private const int ResizeMargin = 8;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int HTCAPTION = 2;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            // For a borderless form base.WndProc returns HTCLIENT everywhere, so
            // we compute the hit region ourselves from the cursor position.
            var pt = PointToClient(new System.Drawing.Point(
                m.LParam.ToInt32() & 0xFFFF, m.LParam.ToInt32() >> 16));
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            bool left = pt.X <= ResizeMargin;
            bool right = pt.X >= w - ResizeMargin;
            bool top = pt.Y <= ResizeMargin;
            bool bottom = pt.Y >= h - ResizeMargin;

            int result = HTCAPTION; // default: drag the window
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

        // Feed history buffers from current snapshots.
        var cpuSnap = _poller.GetSnapshot(typeof(CpuProvider));
        var memSnap = _memPollerRef.GetSnapshot(typeof(MemoryProvider));
        var topSnap = _poller.GetSnapshot(typeof(TopProcessProvider));

        double cpuUsage = MetricValue(cpuSnap, "cpu.aggregate");
        double memLoad = MetricValue(memSnap, "mem.load");

        _cpuHistory.Push(cpuUsage);
        _memHistory.Push(memLoad);
        _renderer.SetRamHistory(_memHistory.Snapshot());

        float pad = 12;
        float tileH = (h - pad * 3) / 2f;
        var cpuRect = new SKRect(pad, pad, w - pad, pad + tileH);
        var memRect = new SKRect(pad, pad * 2 + tileH, w - pad, h - pad);

        uint clock = _cpu.CurrentMhz;
        int cores = Environment.ProcessorCount;
        _renderer.DrawCpuTile(canvas, cpuRect, cpuUsage, clock, cores, _cpuHistory.Snapshot());
        _renderer.DrawRamTile(canvas, memRect, memLoad, _mem.UsedPhys, _mem.TotalPhys);

        // Top-process footer line.
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
            _memPollerRef?.Dispose();
            _surface.Dispose();
        }
        base.Dispose(disposing);
    }
}
