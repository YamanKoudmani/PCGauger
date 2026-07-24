using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using PCGauger.Infrastructure;

namespace PCGauger;

/// <summary>
/// One-time welcome dialog shown on first launch. Lets the user configure
/// key preferences (launch at startup, always on top) before the dashboard
/// appears. After the user clicks "Get Started", the config is saved with
/// <c>IsFirstRun = false</c> so this never shows again.
/// </summary>
public sealed class WelcomeForm : Form
{
    // Colors matching the app dark theme (Frost Dark palette).
    private static readonly Color BackgroundColor = Color.FromArgb(0x12, 0x12, 0x14);
    private static readonly Color BorderColor = Color.FromArgb(0x2A, 0x32, 0x3E);
    private static readonly Color TextPrimary = Color.FromArgb(0xF2, 0xF5, 0xF8);
    private static readonly Color TextSecondary = Color.FromArgb(0x8B, 0x95, 0xA3);
    private static readonly Color Accent = Color.FromArgb(0x4C, 0x9A, 0xFF);
    private static readonly Color AccentHover = Color.FromArgb(0x6B, 0xAC, 0xFF);
    private static readonly Color RowHover = Color.FromArgb(0x1C, 0x1F, 0x27);

    private bool _launchAtStartup;
    private bool _alwaysOnTop;

    // Layout (computed in ComputeLayout from real font metrics so text can
    // never overlap, regardless of DPI scaling).
    private Rectangle _launchToggleRect;
    private Rectangle _alwaysOnTopRect;
    private Rectangle _buttonRect;
    private int _titleY;
    private int _subtitleY;
    private int _dividerY;
    private int _rowLabelOffsetY;
    private int _rowDescOffsetY;

    // Hover state.
    private bool _hoverLaunch;
    private bool _hoverAlwaysOnTop;
    private bool _hoverButton;

    public WelcomeForm(AppConfig config)
    {
        _launchAtStartup = config.LaunchAtStartup;
        _alwaysOnTop = config.AlwaysOnTop;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BackgroundColor;
        Size = new Size(520, 378);
        MinimumSize = Size;
        MaximumSize = Size;
        DoubleBuffered = true;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ComputeLayout();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ComputeLayout();
    }

    private void ComputeLayout()
    {
        int pad = 36;
        int contentX = pad;
        int contentW = ClientSize.Width - pad * 2;

        // Stack every element from real font line heights. Hardcoding offsets
        // alongside point-sized fonts caused overlapping/clipped text (and would
        // get worse under DPI scaling, where point fonts grow but pixels don't).
        using var titleFont = new Font("Segoe UI Semibold", 20f);
        using var subFont = new Font("Segoe UI", 12f);
        using var labelFont = new Font("Segoe UI", 13f);
        using var descFont = new Font("Segoe UI", 11f);

        _titleY = 30;
        _subtitleY = _titleY + titleFont.Height + 4;
        _dividerY = _subtitleY + subFont.Height + 14;

        // Each toggle row fits label + description plus breathing room.
        const int rowPadV = 8;
        _rowLabelOffsetY = rowPadV;
        _rowDescOffsetY = rowPadV + labelFont.Height + 2;
        int rowH = _rowDescOffsetY + descFont.Height + rowPadV;
        const int rowGap = 8;

        int rowY = _dividerY + 18;
        _launchToggleRect = new Rectangle(contentX, rowY, contentW, rowH);
        _alwaysOnTopRect = new Rectangle(contentX, rowY + rowH + rowGap, contentW, rowH);

        int btnW = 160;
        int btnH = 40;
        _buttonRect = new Rectangle(
            (ClientSize.Width - btnW) / 2,
            ClientSize.Height - pad - btnH,
            btnW, btnH);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool newLaunch = _launchToggleRect.Contains(e.Location);
        bool newAlwaysOnTop = _alwaysOnTopRect.Contains(e.Location);
        bool newButton = _buttonRect.Contains(e.Location);

        // Hand only while actually over something clickable (previously the
        // cursor was set to Hand on any hover-state *change*, so it stuck
        // everywhere after the first hover).
        Cursor = (newLaunch || newAlwaysOnTop || newButton) ? Cursors.Hand : Cursors.Default;

        if (_hoverLaunch != newLaunch || _hoverAlwaysOnTop != newAlwaysOnTop || _hoverButton != newButton)
        {
            _hoverLaunch = newLaunch;
            _hoverAlwaysOnTop = newAlwaysOnTop;
            _hoverButton = newButton;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        if (_buttonRect.Contains(e.Location))
        {
            var config = AppConfig.Load();
            config.LaunchAtStartup = _launchAtStartup;
            config.AlwaysOnTop = _alwaysOnTop;
            config.IsFirstRun = false;
            config.Save();
            SetStartup(_launchAtStartup);
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        if (_launchToggleRect.Contains(e.Location))
        {
            _launchAtStartup = !_launchAtStartup;
            Invalidate();
            return;
        }
        if (_alwaysOnTopRect.Contains(e.Location))
        {
            _alwaysOnTop = !_alwaysOnTop;
            Invalidate();
            return;
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverLaunch = false;
        _hoverAlwaysOnTop = false;
        _hoverButton = false;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        int pad = 36;

        // Title.
        using (var titleFont = new Font("Segoe UI Semibold", 20f))
        using (var titleBrush = new SolidBrush(Accent))
        {
            g.DrawString("Welcome to PCGauger", titleFont, titleBrush, pad, _titleY);
        }

        // Subtitle.
        using (var subFont = new Font("Segoe UI", 12f))
        using (var subBrush = new SolidBrush(TextSecondary))
        {
            g.DrawString("A lightweight hardware monitoring dashboard.", subFont, subBrush, pad, _subtitleY);
        }

        // Divider.
        using (var divPen = new Pen(BorderColor, 1f))
        {
            g.DrawLine(divPen, pad, _dividerY, ClientSize.Width - pad, _dividerY);
        }

        // Toggle rows.
        DrawToggleRow(g, _launchToggleRect, "Launch at startup",
            "Start PCGauger automatically when you log in.",
            _launchAtStartup, _hoverLaunch);

        DrawToggleRow(g, _alwaysOnTopRect, "Always on top",
            "Keep PCGauger visible over other windows.",
            _alwaysOnTop, _hoverAlwaysOnTop);

        // "Get Started" button.
        DrawButton(g);
    }

    private void DrawToggleRow(Graphics g, Rectangle rect, string label, string description, bool on, bool hover)
    {
        // Reserve space for the switch on the right.
        int sw = 34, sh = 18;
        int switchMargin = 4;

        // Subtle background highlight on hover. Inflated a little past the
        // text column so the text keeps its left alignment with the header.
        if (hover)
        {
            using (var bgBrush = new SolidBrush(RowHover))
            using (var bgPath = RoundedRect(rect.X - 8, rect.Y, rect.Width + 16, rect.Height, 8))
            {
                g.FillPath(bgBrush, bgPath);
            }
        }

        // Switch position (right-aligned, centered against the whole row), so
        // text width can be clamped to never run underneath it.
        int sx = rect.Right - sw - switchMargin;
        int sy = rect.Y + rect.Height / 2 - sh / 2;
        int textMaxW = sx - 12 - rect.X;

        // Label.
        using (var labelFont = new Font("Segoe UI", 13f))
        using (var labelBrush = new SolidBrush(TextPrimary))
        using (var format = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
        {
            g.DrawString(label, labelFont, labelBrush,
                new RectangleF(rect.X, rect.Y + _rowLabelOffsetY, textMaxW, labelFont.Height), format);
        }

        // Description.
        using (var descFont = new Font("Segoe UI", 11f))
        using (var descBrush = new SolidBrush(TextSecondary))
        using (var format = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
        {
            g.DrawString(description, descFont, descBrush,
                new RectangleF(rect.X, rect.Y + _rowDescOffsetY, textMaxW, descFont.Height), format);
        }

        // Track.
        using (var trackPath = RoundedRect(sx, sy, sw, sh, sh / 2))
        using (var trackBrush = new SolidBrush(on ? Accent : BorderColor))
        {
            g.FillPath(trackBrush, trackPath);
        }

        // Knob.
        int knobDiameter = sh - 4;
        int knobX = on ? sx + sw - knobDiameter - 2 : sx + 2;
        int knobCenterX = knobX + knobDiameter / 2;
        int knobCenterY = sy + sh / 2;

        using (var knobBrush = new SolidBrush(TextPrimary))
        {
            g.FillEllipse(knobBrush, knobCenterX - knobDiameter / 2, knobCenterY - knobDiameter / 2,
                knobDiameter, knobDiameter);
        }
    }

    private void DrawButton(Graphics g)
    {
        var r = _buttonRect;
        int radius = 10;

        using (var path = RoundedRect(r.X, r.Y, r.Width, r.Height, radius))
        using (var brush = new SolidBrush(_hoverButton ? AccentHover : Accent))
        {
            g.FillPath(brush, path);
        }

        using (var font = new Font("Segoe UI Semibold", 14f))
        using (var brush = new SolidBrush(Color.White))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.DrawString("Get Started", font, brush, r, format);
        }
    }

    private static GraphicsPath RoundedRect(int x, int y, int w, int h, int radius)
    {
        var path = new GraphicsPath();
        path.AddArc(x, y, radius * 2, radius * 2, 180, 90);
        path.AddArc(x + w - radius * 2, y, radius * 2, radius * 2, 270, 90);
        path.AddArc(x + w - radius * 2, y + h - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(x, y + h - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        return path;
    }

    // Startup registry helpers (mirrors MainForm).
    private static readonly string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

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
}