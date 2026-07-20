using System.Drawing;
using System.Windows.Forms;

namespace PCGauger;

/// <summary>
/// Minimal borderless loading splash with an animated spinner. Shown the instant
/// the runtime is up (before the main SkiaSharp form finishes initializing) so a
/// single-file launch — which extracts the bundled runtime on first run — still
/// gives immediate "it heard the click" feedback instead of a dead-looking pause.
/// Closed by the main form once it is ready to paint.
/// </summary>
public sealed class SplashForm : Form
{
    private readonly System.Windows.Forms.Timer _spin = new() { Interval = 80 };
    private float _angle;
    private readonly string _text;

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
    }

    protected override void OnLoad(System.EventArgs e)
    {
        base.OnLoad(e);
        _spin.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Spinner: an arc that sweeps, leaving a soft gap.
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
