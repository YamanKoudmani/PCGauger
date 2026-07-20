using System.Drawing;
using System.Windows.Forms;

namespace PCGauger;

/// <summary>
/// Minimal borderless loading splash with an animated spinner. Shown on the
/// UI thread (its own message loop) while the main SkiaSharp form is built
/// on a worker thread, so a single-file launch — which extracts the bundled
/// runtime on first run — keeps animating instead of looking dead. The main
/// form calls <see cref="SignalReady"/> once it is constructed; the splash
/// then hands off. A safety timer guarantees the splash can never stick
/// forever even if the signal is missed.
/// </summary>
public sealed class SplashForm : Form
{
    private readonly System.Windows.Forms.Timer _spin = new() { Interval = 80 };
    private readonly System.Windows.Forms.Timer _safety = new() { Interval = 20000 };
    private float _angle;
    private readonly string _text;
    private bool _handedOff;

    public SplashForm(string text = "PCGauger")
    {
        _text = text;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(0xFF, 0x12, 0x12, 0x14);
        ForeColor = Color.FromArgb(0xFF, 0xC8, 0xCC, 0xD4);
        Size = new Size(260, 120);
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 12, FontStyle.Regular);
        _spin.Tick += (_, _) => { _angle = (_angle + 30) % 360; Invalidate(); };
        // Last-resort: if the main form never signals (env quirk / hang),
        // close the splash so the user is never stuck on a frozen spinner.
        _safety.Tick += (_, _) => { if (!_handedOff) Close(); };
    }

    protected override void OnLoad(System.EventArgs e)
    {
        base.OnLoad(e);
        _spin.Start();
        _safety.Start();
    }

    /// <summary>Called by the main form once it is constructed and about to
    /// take over. Closes the splash on the UI thread.</summary>
    public void SignalReady()
    {
        if (_handedOff) return;
        _handedOff = true;
        _safety.Stop();
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        int cx = Width / 2, cy = 44, r = 16;
        using var track = new Pen(Color.FromArgb(0x33, 0x3A, 0x40), 3);
        g.DrawEllipse(track, cx - r, cy - r, r * 2, r * 2);
        using var arc = new Pen(Color.FromArgb(0xFF, 0x6C, 0xA8, 0xFF), 3);
        arc.StartCap = arc.EndCap = System.Drawing.Drawing2D.LineCap.Round;
        g.DrawArc(arc, cx - r, cy - r, r * 2, r * 2, _angle, 270);

        using var brush = new SolidBrush(ForeColor);
        var fmt = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString(_text, Font, brush, new PointF(Width / 2f, cy + r + 12), fmt);
    }

    protected override bool ShowWithoutActivation => true;
}
