using PCGauger.Infrastructure;
using PCGauger.Metrics;
using SkiaSharp;

namespace PCGauger.Rendering;

/// <summary>
/// Multi-instance device support for the owner-drawn tiles: per-tile device
/// selector + remove button in the settings pane, an in-canvas device dropdown,
/// a tile "device unavailable" state, and the instance-aware Tiles section in
/// the global pane (add-row management + device picker).
///
/// Everything here is ADDITIVE: the original <see cref="TileRenderer"/> entry
/// points keep their signatures and behavior. The wiring pass opts into the new
/// visuals by calling the new overloads and supplying the caller-owned state
/// structs (<see cref="DevicePaneState"/> / <see cref="GlobalPaneDeviceState"/>).
/// Rendering stays stateless — open/closed, scroll, and hover live in the caller.
/// </summary>
public sealed partial class TileRenderer
{
    // ---- per-tile device section geometry ----
    private const float PaneDeviceRowH = 36;       // glyph + name + chevron row
    private const float PaneDeviceDropdownItemH = 30;
    private const float PaneDeviceDropdownMax = 5; // visible items before scroll arrows
    private const float PaneRemoveH = 28;
    private const float PaneRemoveGap = 10;        // breathing room above remove (hard to hit by accident)

    // Device glyph sizes.
    private const float GlyphSize = 16;

    // Danger tint for the remove action (theme-aware via alpha; this is the hue).
    private static readonly SKColor Danger = new(0xE5, 0x4B, 0x4B);
    // Calm "unavailable" accent — never alarm-red.
    private static readonly SKColor Unavailable = new(0x9A, 0xA2, 0xB0);

    // ---- global pane device section geometry ----
    private const float PaneAddRowH = 30;
    private const float PaneAddBtnW = 64;
    private const float PanePickerItemH = 32;
    private const float PanePickerMax = 6;
    public const float GlobalPaneExtendedHeight = 392;

    // ---- full-window global pane geometry (BUG 2) ----
    // The extended pane is now derived from the WHOLE client rect (with margins)
    // instead of a fixed 392px centered card, so it fills the active window and
    // scales. Row heights / spacing scale with the available body height; text
    // sizes stay fixed so everything remains legible from a ~420x560 floating
    // window up to the 800x480 kiosk. GlobalPaneBottomReserve keeps the card
    // clear of the 28px footer strip.
    private const float GlobalPaneMargin = 16;
    private const float GlobalPaneBottomReserve = 36; // footer (28) + breathing room
    private const float GlobalPanePad = 16;
    private const float GlobalPaneHeaderH = 34;
    private const float GlobalPaneNominalBodyH = 360; // body height at which scale == 1
    private const float GlobalPaneMinScale = 0.8f;
    private const float GlobalPaneMaxScale = 1.2f;

    /// <summary>
    /// One selectable device as presented in a dropdown/picker. Built by the
    /// caller from <see cref="DeviceDescriptor"/>s (plus the tile's current
    /// binding + which devices are already bound to other tiles).
    /// </summary>
    public sealed class DeviceItem
    {
        public string Id = "";
        public string DisplayName = "";
        public string Detail = "";
        /// <summary>True when this is the tile's currently-bound device.</summary>
        public bool Checked;
        /// <summary>True when already bound to another tile — rendered dimmed/disabled.</summary>
        public bool Disabled;
    }

    /// <summary>
    /// Caller-owned state for a tile's device dropdown. Rendering never mutates
    /// these; the wiring pass owns open/scroll/hover and passes them each frame.
    /// </summary>
    public sealed class DevicePaneState
    {
        public bool DropdownOpen;
        /// <summary>Index of the first visible item (scroll offset). Clamped by the layout math.</summary>
        public int ScrollIndex;
        /// <summary>Index (in the full <see cref="Items"/> list) under the cursor, or -1.</summary>
        public int HoverIndex = -1;
        public IReadOnlyList<DeviceItem> Items = Array.Empty<DeviceItem>();
    }

    /// <summary>
    /// Caller-owned state for the global pane's Tiles section: how many tiles
    /// exist per multi-instance kind (for the "Disk · 2" label) and which kind's
    /// add-picker is open (with its scroll/hover).
    /// </summary>
    public sealed class GlobalPaneDeviceState
    {
        public int CpuCount;
        public int RamCount;
        public int GpuCount;
        public int DiskCount;
        public int NetworkCount;

        public int CountFor(TileKind kind) => kind switch
        {
            TileKind.Cpu => CpuCount,
            TileKind.Ram => RamCount,
            TileKind.Gpu => GpuCount,
            TileKind.Disk => DiskCount,
            TileKind.Network => NetworkCount,
            _ => 0,
        };

        /// <summary>The kind whose add-picker is open, or null when none.</summary>
        public TileKind? ActivePickerKind;
        public int PickerScrollIndex;
        public int PickerHoverIndex = -1;
        public IReadOnlyList<DeviceItem> PickerItems = Array.Empty<DeviceItem>();
    }

    // ===================================================================
    //  PER-TILE SETTINGS PANE — device row, dropdown, remove button
    // ===================================================================

    /// <summary>
    /// Computes the settings-pane hit rects for a tile, including the device
    /// selector row, its dropdown (when open), and the remove-tile button — but
    /// ONLY when <paramref name="tile"/> is device-selectable. For singleton
    /// tiles this returns the exact same layout as the original
    /// <see cref="ComputePaneLayout(SKRect, SKRect, TileVisual)"/>.
    /// </summary>
    public PaneLayout? ComputePaneLayout(SKRect tile, SKRect client, TileVisual v, Tile tileData, DevicePaneState? deviceState = null)
    {
        if (tileData is null || !tileData.IsDeviceSelectable)
            return ComputePaneLayout(tile, client, v);

        // Device-selectable tiles need extra height for the device row (top) and
        // the remove button (bottom). The extra must cover exactly:
        //   device row (PaneDeviceRowH) + gap (4) + remove gap (PaneRemoveGap)
        //   + remove button (PaneRemoveH).
        // The open dropdown is a PURE OVERLAY sub-card (it draws last, opaque,
        // on top of the rows beneath) and therefore does NOT consume pane
        // height — matching the draw routine, which never advances past it.
        var extraHeight = PaneDeviceRowH + 4 + PaneRemoveH + PaneRemoveGap + 4; // +4 = bottom breathing room
        var pane = SettingsPaneRect(tile, client, extraHeight);
        if (pane is null) return null;
        var p = pane.Value;

        float x = p.Left + PanePad;
        float right = p.Right - PanePad;
        float y = p.Top + PanePad + PaneHeaderGap;

        // --- Device row (top of the pane, under the header) ---
        var deviceRow = new SKRect(x, y, right, y + PaneDeviceRowH);
        y += PaneDeviceRowH + 4;

        // --- Dropdown (overlay sub-card, only when open; does NOT advance y) ---
        SKRect dropdown = SKRect.Empty;
        var dropdownItems = new List<SKRect>();
        SKRect dropdownUp = SKRect.Empty;
        SKRect dropdownDown = SKRect.Empty;
        if (deviceState?.DropdownOpen == true && deviceState.Items.Count > 0)
        {
            int total = deviceState.Items.Count;
            int scroll = Math.Max(0, Math.Min(deviceState.ScrollIndex, Math.Max(0, total - (int)PaneDeviceDropdownMax)));
            int visible = Math.Min((int)PaneDeviceDropdownMax, total - scroll);
            float listTop = y;
            float listH = 16 + visible * PaneDeviceDropdownItemH + (total > (int)PaneDeviceDropdownMax ? 22 : 0);
            dropdown = new SKRect(x - 2, listTop, right + 2, listTop + listH);
            // header strip ("Device")
            float iy = listTop + 16;
            for (int i = 0; i < visible; i++)
            {
                dropdownItems.Add(new SKRect(x, iy, right, iy + PaneDeviceDropdownItemH));
                iy += PaneDeviceDropdownItemH;
            }
            if (total > (int)PaneDeviceDropdownMax)
            {
                dropdownUp = new SKRect(x, iy, right, iy + 11);
                dropdownDown = new SKRect(x, dropdown.Bottom - 11, right, dropdown.Bottom);
            }
        }

        // --- The rest of the pane (toggles, units, accent, custom/reset) ---
        var toggles = new List<SKRect>(5);
        for (int i = 0; i < 5; i++)
        {
            toggles.Add(new SKRect(x, y, right, y + PaneToggleH));
            y += PaneToggleH;
        }
        y += 4;
        float unitLabelH = PaneAccentLabelH;
        var unitSegs = SegmentRects(x, right, y + unitLabelH, PaneToggleH);
        y += unitLabelH + PaneToggleH;
        y += 10;
        y += PaneAccentLabelH;

        var swatches = new List<SKRect>(12);
        var palette = SwatchPalette();
        int swatchRows = (int)Math.Ceiling((double)palette.Count / SwatchPerRow);
        for (int i = 0; i < palette.Count; i++)
        {
            int col = i % SwatchPerRow;
            int row = i / SwatchPerRow;
            float sx = x + col * (SwatchW + SwatchGap);
            float sy = y + row * (SwatchH + SwatchGap);
            swatches.Add(new SKRect(sx, sy, sx + SwatchW, sy + SwatchH));
        }
        y += swatchRows * (SwatchH + SwatchGap) + 6;

        float midX = (x + right) / 2;
        var custom = new SKRect(x, y, midX - 4, y + PaneButtonH);
        var reset = new SKRect(midX + 4, y, right, y + PaneButtonH);
        y += PaneButtonH + PaneRemoveGap;

        // --- Remove tile button (bottom, separated) ---
        var remove = new SKRect(x, y, right, y + PaneRemoveH);

        var close = new SKRect(p.Right - 28, p.Top + 6, p.Right - 8, p.Top + 26);

        return new PaneLayout
        {
            Pane = p,
            Toggles = toggles,
            UnitSegments = unitSegs,
            Swatches = swatches,
            Custom = custom,
            Reset = reset,
            Close = close,
            // new members:
            DeviceRow = deviceRow,
            DeviceDropdown = dropdown,
            DeviceDropdownItems = dropdownItems,
            DeviceDropdownUp = dropdownUp,
            DeviceDropdownDown = dropdownDown,
            RemoveTile = remove,
        };
    }

    /// <summary>
    /// Draws the settings pane including the device selector row, the open
    /// dropdown, and the remove-tile button (multi-instance tiles only). For
    /// singleton tiles this delegates to the original draw routine unchanged.
    /// </summary>
    public void DrawSettingsPane(SKCanvas canvas, PaneLayout layout, TileVisual v, bool hoverClose, Tile tileData, DevicePaneState? deviceState = null, bool hoverDeviceRow = false, bool hoverRemove = false)
    {
        if (tileData is null || !tileData.IsDeviceSelectable)
        {
            DrawSettingsPane(canvas, layout, v, hoverClose);
            return;
        }

        var pane = layout.Pane;
        using var round = new SKRoundRect(pane, 14);
        using var bg = new SKPaint { Color = _theme.PaneBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(round, bg);
        using var border = new SKPaint { Color = _theme.TileBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawRoundRect(round, border);

        float x = pane.Left + PanePad;
        float right = pane.Right - PanePad;
        float y = pane.Top + PanePad;

        using var headerPaint = new SKPaint { Color = _theme.TextPrimary, IsAntialias = true };
        var headerFont = TileRenderer.CachedFont("Segoe UI Semibold", 13);
        canvas.DrawText("Customize", x, y + 12, headerFont, headerPaint);
        y += PaneHeaderGap;

        DrawCloseGlyph(canvas, layout.Close, hoverClose);

        // --- Device row ---
        DrawDeviceRow(canvas, layout.DeviceRow, tileData, hoverDeviceRow, v.Accent, deviceState?.DropdownOpen ?? false);
        y = layout.DeviceRow.Bottom + 4;

        // --- Toggles ---
        var labels = new[] { ("Title", v.Settings.ShowTitle), ("Usage %", v.Settings.ShowBigValue),
            ("Bar", v.Settings.ShowUsageBar), ("Graph", v.Settings.ShowSparkline), ("Details", v.Settings.ShowSecondaryLine) };
        for (int i = 0; i < labels.Length; i++)
            DrawToggleRow(canvas, layout.Toggles[i], labels[i].Item1, labels[i].Item2, v.Accent);
        y = layout.Toggles[^1].Bottom + 4;

        // Units segmented control.
        using var unitLabelPaint = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
        var unitLabelFont = TileRenderer.CachedFont("Segoe UI", 12);
        canvas.DrawText("Units", x, y + 11, unitLabelFont, unitLabelPaint);
        string[] unitLabels = { "Auto", "Bits", "Bytes" };
        int unitIdx = (int)v.Settings.UnitMode;
        DrawSegments(canvas, layout.UnitSegments, unitLabels, unitIdx, v.Accent);
        y = layout.UnitSegments[^1].Bottom + 4;

        using var div = new SKPaint { Color = _theme.TileBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawLine(x, y, right, y, div);
        y += 10;

        using var subPaint = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
        var subFont = TileRenderer.CachedFont("Segoe UI", 12);
        canvas.DrawText("Accent", x, y + 11, subFont, subPaint);
        y += PaneAccentLabelH;

        var palette = SwatchPalette();
        for (int i = 0; i < palette.Count; i++)
        {
            bool selected = v.Settings.AccentColor is null
                ? palette[i].Equals(TilePalette.DefaultFor(v.Kind))
                : palette[i].Equals(v.Settings.AccentColor.Value);
            DrawSwatch(canvas, layout.Swatches[i], palette[i], selected);
        }
        y = layout.Swatches[^1].Bottom + 6;

        DrawButton(canvas, layout.Custom, "Custom…", v.Accent, false);
        DrawButton(canvas, layout.Reset, "Reset to default", v.Accent, true);

        // --- Remove tile (danger-tinted, separated) ---
        DrawRemoveButton(canvas, layout.RemoveTile, hoverRemove);

        // --- Dropdown overlay (drawn LAST so it cleanly covers the rows beneath) ---
        if (deviceState?.DropdownOpen == true && deviceState.Items.Count > 0)
            DrawDeviceDropdown(canvas, layout, deviceState, v.Accent);
    }

    // ---- device row / dropdown / remove primitives ----

    private void DrawDeviceRow(SKCanvas canvas, SKRect row, Tile tile, bool hover, SKColor accent, bool open = false)
    {
        // Hover highlight (same soft accent as the swatch/toggle rows).
        using var bg = new SKRoundRect(row, 8);
        using var bgp = new SKPaint { Color = hover ? TilePalette.Soft(accent) : _theme.TileBorder.WithAlpha(60), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(bg, bgp);

        // Kind glyph on the left.
        float gx = row.Left + 8;
        float gy = row.MidY - GlyphSize / 2;
        DrawDeviceGlyph(canvas, new SKRect(gx, gy, gx + GlyphSize, gy + GlyphSize), tile.Kind, accent);

        float tx = gx + GlyphSize + 8;
        string name = tile.DeviceDisplayName ?? "—";
        using var namePaint = new SKPaint { Color = _theme.TextPrimary, IsAntialias = true };
        var nameFont = TileRenderer.CachedFont("Segoe UI Semibold", 12);
        string shown = Truncate(canvas, name, row.Right - tx - 26, "Segoe UI Semibold", 12);
        canvas.DrawText(shown, tx, row.MidY + 4, nameFont, namePaint);

        // Chevron on the right: ▾ when closed, ▴ when open.
        float cx = row.Right - 14;
        using var chev = new SKPaint { Color = _theme.TextSecondary, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        if (open)
        {
            canvas.DrawLine(cx - 4, row.MidY + 2, cx, row.MidY - 3, chev);
            canvas.DrawLine(cx, row.MidY - 3, cx + 4, row.MidY + 2, chev);
        }
        else
        {
            canvas.DrawLine(cx - 4, row.MidY - 2, cx, row.MidY + 3, chev);
            canvas.DrawLine(cx, row.MidY + 3, cx + 4, row.MidY - 2, chev);
        }
    }

    private void DrawDeviceDropdown(SKCanvas canvas, PaneLayout layout, DevicePaneState state, SKColor accent)
    {
        var d = layout.DeviceDropdown;
        if (d.IsEmpty) return;

        // Opaque sub-card so it cleanly covers the rows beneath.
        using var rr = new SKRoundRect(d, 10);
        using var p = new SKPaint { Color = _theme.PaneBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(rr, p);
        using var b = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawRoundRect(rr, b);

        float x = d.Left + 4;
        float right = d.Right - 4;
        using var hdr = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
        var hdrF = TileRenderer.CachedFont("Segoe UI", 11);
        canvas.DrawText("Device", x, d.Top + 12, hdrF, hdr);

        int total = state.Items.Count;
        int scroll = Math.Max(0, Math.Min(state.ScrollIndex, Math.Max(0, total - (int)PaneDeviceDropdownMax)));
        int visible = Math.Min((int)PaneDeviceDropdownMax, total - scroll);

        for (int i = 0; i < visible; i++)
        {
            int idx = scroll + i;
            var item = state.Items[idx];
            var r = layout.DeviceDropdownItems[i];
            bool hover = idx == state.HoverIndex;
            DrawDeviceDropdownItem(canvas, r, item, hover, accent, x, right);
        }

        if (total > (int)PaneDeviceDropdownMax)
        {
            // Overflow arrows. Up enabled when scroll > 0; down when more below.
            bool upOn = scroll > 0;
            bool downOn = scroll + visible < total;
            using var upP = new SKPaint { Color = upOn ? _theme.TextSecondary : _theme.TextSecondary.WithAlpha(70), Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
            float ux = (layout.DeviceDropdownUp.Left + layout.DeviceDropdownUp.Right) / 2;
            float uy = layout.DeviceDropdownUp.MidY;
            canvas.DrawLine(ux - 4, uy + 2, ux, uy - 3, upP);
            canvas.DrawLine(ux, uy - 3, ux + 4, uy + 2, upP);
            using var downP = new SKPaint { Color = downOn ? _theme.TextSecondary : _theme.TextSecondary.WithAlpha(70), Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
            float dy = layout.DeviceDropdownDown.MidY;
            canvas.DrawLine(ux - 4, dy - 2, ux, dy + 3, downP);
            canvas.DrawLine(ux, dy + 3, ux + 4, dy - 2, downP);
        }
    }

    private void DrawDeviceDropdownItem(SKCanvas canvas, SKRect r, DeviceItem item, bool hover, SKColor accent, float x, float right)
    {
        if (hover && !item.Disabled)
        {
            using var h = new SKRoundRect(r, 6);
            using var hp = new SKPaint { Color = TilePalette.Soft(accent), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(h, hp);
        }

        SKColor primary = item.Disabled ? _theme.TextSecondary.WithAlpha(110) : _theme.TextPrimary;
        SKColor dim = item.Disabled ? _theme.TextSecondary.WithAlpha(80) : _theme.TextSecondary;

        using var nameP = new SKPaint { Color = primary, IsAntialias = true };
        var nameF = TileRenderer.CachedFont("Segoe UI Semibold", 12);
        string shown = Truncate(canvas, item.DisplayName, right - x - 22, "Segoe UI Semibold", 12);
        canvas.DrawText(shown, x, r.MidY - 1, nameF, nameP);

        if (!string.IsNullOrEmpty(item.Detail))
        {
            using var detP = new SKPaint { Color = dim, IsAntialias = true };
            var detF = TileRenderer.CachedFont("Segoe UI", 10);
            string det = Truncate(canvas, item.Detail, right - x - 22, "Segoe UI", 10);
            canvas.DrawText(det, x, r.MidY + 11, detF, detP);
        }

        // Checked = accent dot (current device).
        if (item.Checked)
        {
            using var dot = new SKPaint { Color = accent, Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawCircle(right - 9, r.MidY, 3.2f, dot);
        }
    }

    private void DrawRemoveButton(SKCanvas canvas, SKRect r, bool hover)
    {
        using var rr = new SKRoundRect(r, 8);
        using var p = new SKPaint
        {
            Color = hover ? Danger.WithAlpha(40) : Danger.WithAlpha(18),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        canvas.DrawRoundRect(rr, p);
        using var b = new SKPaint { Color = Danger.WithAlpha((byte)(hover ? 220 : 150)), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawRoundRect(rr, b);

        using var tPaint = new SKPaint { Color = hover ? Danger : Danger.WithAlpha(210), IsAntialias = true };
        var tFont = TileRenderer.CachedFont("Segoe UI Semibold", 12);
        float tw = tFont.MeasureText("Remove tile");
        canvas.DrawText("Remove tile", r.MidX - tw / 2, r.MidY + 4, tFont, tPaint);
    }

    // ===================================================================
    //  TILE BODY — device unavailable state
    // ===================================================================

    /// <summary>
    /// Draws a tile whose bound device is currently missing: the normal card +
    /// kind title (with optional device subtitle) but with the body dimmed and a
    /// calm, centered "Device unavailable" state instead of charts/values. The
    /// gear stays operable (drawn by the host after this), so the user can
    /// re-pick a device. Theme-aware — never alarm-red.
    /// </summary>
    public void DrawUnavailableTile(SKCanvas canvas, SKRect rect, TileSettings s, SKColor accent, TileKind kind, string kindTitle, string? subtitle, string? deviceName)
    {
        DrawCard(canvas, rect);
        var v = new TileVisual(kind, s, accent, _theme, false) { Rect = rect, Y = rect.Top + TilePad };
        DrawTitle(canvas, v, kindTitle, subtitle);

        // Dim the body region below the title.
        float dimTop = v.Y + 4;
        using var dim = new SKPaint { Color = _theme.TileBackground.WithAlpha(150), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRect(new SKRect(rect.Left + TilePad, dimTop, rect.Right - TilePad, rect.Bottom - TilePad), dim);

        float cx = rect.MidX;
        float cy = (dimTop + rect.Bottom - TilePad) / 2;

        // Small device glyph in a soft circle.
        float gr = 18;
        using var gbg = new SKPaint { Color = Unavailable.WithAlpha(40), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(cx, cy - 14, gr, gbg);
        DrawDeviceGlyph(canvas, new SKRect(cx - GlyphSize / 2, cy - 14 - GlyphSize / 2, cx + GlyphSize / 2, cy - 14 + GlyphSize / 2),
            kind, Unavailable);

        using var head = new SKPaint { Color = _theme.TextPrimary, IsAntialias = true };
        var headF = TileRenderer.CachedFont("Segoe UI Semibold", 13);
        string headText = "Device unavailable";
        float hw = headF.MeasureText(headText);
        canvas.DrawText(headText, cx - hw / 2, cy + 16, headF, head);

        if (!string.IsNullOrEmpty(deviceName))
        {
            using var nameP = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
            var nameF = TileRenderer.CachedFont("Segoe UI", 11);
            string nm = Truncate(canvas, deviceName, rect.Width - 2 * TilePad, "Segoe UI", 11);
            float nw = nameF.MeasureText(nm);
            canvas.DrawText(nm, cx - nw / 2, cy + 34, nameF, nameP);
        }
    }

    // ===================================================================
    //  GLOBAL PANE — instance-aware Tiles section
    // ===================================================================

    /// <summary>
    /// Computes the global pane hit rects with the redesigned Tiles section:
    /// CPU/RAM keep toggle chips; Disk/GPU/Network become management rows
    /// (label + count + an "Add" button) and an optional device picker. The original
    /// rects (toggles, theme, threshold, etc.) are preserved so the rest of the
    /// pane draws and hit-tests identically. When <paramref name="state"/> is
    /// null this returns the exact original layout.
    /// </summary>
    public GlobalPaneLayout? ComputeGlobalPaneLayout(SKRect client, GlobalPaneDeviceState? state = null)
    {
        if (state is null)
            return ComputeGlobalPaneLayout(client);

        // Derive the pane from the whole client rect (with margins). This makes
        // the pane fill the active window — from a small floating window up to
        // the 800x480 kiosk — instead of a fixed 392px centered card.
        float margin = GlobalPaneMargin;
        float x = client.Left + margin;
        float y = client.Top + margin;
        float width = client.Width - 2 * margin;
        float height = client.Height - margin - GlobalPaneBottomReserve;
        if (width < 300) width = 300;
        if (height < 320) height = 320;
        // Clamp inside the client so it can never spill off-window.
        if (x < client.Left + 8) x = client.Left + 8;
        if (y < client.Top + 8) y = client.Top + 8;
        if (x + width > client.Right - 8) width = client.Right - 8 - x;
        if (y + height > client.Bottom - 8) height = client.Bottom - 8 - y;
        if (width < 300 || height < 300) return null;

        var p = new SKRect(x, y, x + width, y + height);
        float pad = GlobalPanePad;
        float lx = p.Left + pad;
        float rx = p.Right - pad;
        float colGap = 16;
        float leftW = (rx - lx - colGap) / 2;
        float rightX = lx + leftW + colGap;

        var close = new SKRect(p.Right - 30, p.Top + 8, p.Right - 8, p.Top + 30);

        // Scale row heights / spacing with the available body height so the
        // layout breathes on large panes and compresses on small ones. Text
        // sizes stay fixed (legibility).
        float bodyTop = p.Top + pad + GlobalPaneHeaderH;
        float bodyBottom = p.Bottom - pad;
        float bodyH = Math.Max(1, bodyBottom - bodyTop);
        float scale = Math.Clamp(bodyH / GlobalPaneNominalBodyH, GlobalPaneMinScale, GlobalPaneMaxScale);

        float rowH = 30 * scale;
        float segH = 26 * scale;
        float segLabelH = 16;
        float smallGap = 4 * scale;
        float toggleGap = 4 * scale;
        float sectionGap = 16 * scale;
        float chipH = 26 * scale;

        // ---- left column: toggles + threshold stepper ----
        float ly = bodyTop;
        var launchToggle = new SKRect(lx, ly, lx + leftW, ly + rowH); ly += rowH + toggleGap;
        var kioskToggle = new SKRect(lx, ly, lx + leftW, ly + rowH); ly += rowH + toggleGap;
        var alwaysOnTopToggle = new SKRect(lx, ly, lx + leftW, ly + rowH); ly += rowH + toggleGap;
        var thresholdToggle = new SKRect(lx, ly, lx + leftW, ly + rowH); ly += rowH + toggleGap;

        float minusW = 26 * scale, valW = 44 * scale, sgap = 6 * scale;
        float plusX = lx + leftW - minusW;
        float valX = plusX - sgap - valW;
        float minusX = valX - sgap - minusW;
        ly += segLabelH + smallGap;
        var thresholdMinus = new SKRect(minusX, ly, minusX + minusW, ly + rowH);
        var thresholdValue = new SKRect(valX, ly, valX + valW, ly + rowH);
        var thresholdPlus = new SKRect(plusX, ly, plusX + minusW, ly + rowH);
        float leftBottom = ly + rowH;

        // ---- right column: theme / graph span / decimals segments ----
        float ry = bodyTop;
        // Theme: 6 themes in 2 rows of 3.
        float themeRow1Y = ry + segLabelH + smallGap;
        var themeRow1 = SegmentRects(rightX, rx, themeRow1Y, segH, 3);
        float themeRow2Y = themeRow1Y + segH + smallGap;
        var themeRow2 = SegmentRects(rightX, rx, themeRow2Y, segH, 3);
        var themeSegs = new List<SKRect>(6);
        themeSegs.AddRange(themeRow1);
        themeSegs.AddRange(themeRow2);
        ry += segLabelH + smallGap + segH + smallGap + segH + toggleGap;

        var graphSpanSegs = SegmentRects(rightX, rx, ry + segLabelH, segH, 4); ry += segLabelH + smallGap + segH + toggleGap;
        var decimalsSegs = SegmentRects(rightX, rx, ry + segLabelH, segH); ry += segLabelH + smallGap + segH + toggleGap;
        float rightBottom = ry;

        // ---- Tiles section (full width below both columns) ----
        float tilesTop = Math.Max(leftBottom, rightBottom) + sectionGap;

        // CPU + RAM toggle chips (two side-by-side).
        float chipY = tilesTop + segLabelH + smallGap;
        float chipGap = 6 * scale;
        float chipW = (rx - lx - chipGap) / 2;
        var chips = new List<SKRect>(2)
        {
            new SKRect(lx, chipY, lx + chipW, chipY + chipH),
            new SKRect(lx + chipW + chipGap, chipY, lx + 2 * chipW + chipGap, chipY + chipH),
        };

        // Disk / GPU / Network management rows.
        float rowY = chipY + chipH + 8 * scale;
        float addRowH = PaneAddRowH * scale;
        var addRows = new List<SKRect>(3);
        var addButtons = new List<SKRect>(3);
        var addKinds = new[] { TileKind.Disk, TileKind.Gpu, TileKind.Network };
        for (int i = 0; i < addKinds.Length; i++)
        {
            addRows.Add(new SKRect(lx, rowY, rx, rowY + addRowH));
            addButtons.Add(new SKRect(rx - PaneAddBtnW * scale, rowY + 2 * scale, rx, rowY + addRowH - 2 * scale));
            rowY += addRowH + 4 * scale;
        }

        // ---- Picker (lays out INSIDE the pane; previously it could spill past
        // the pane bottom, which caused a real click bug). It is positioned just
        // below the add rows and clamped to stay within the card. ----
        SKRect picker = SKRect.Empty;
        var pickerItems = new List<SKRect>();
        SKRect pickerUp = SKRect.Empty, pickerDown = SKRect.Empty, pickerClose = SKRect.Empty, pickerEmpty = SKRect.Empty;
        if (state.ActivePickerKind.HasValue)
        {
            int total = state.PickerItems.Count;
            float maxBottom = p.Bottom - pad;
            // Highest the picker may rise (just below the header) when the pane
            // is too short to fit it below the Tiles section — it then overlays
            // the columns rather than spilling past the card.
            float minTop = p.Top + pad + GlobalPaneHeaderH + 4;

            // Local helper: popover height for a given visible count. Scroll
            // arrows appear whenever not every item fits in the viewport.
            float PkH(int vis, bool scroll) => 22 + vis * PanePickerItemH + (scroll ? 22 : 0) + (total == 0 ? 26 : 0);

            int visible = Math.Min((int)PanePickerMax, Math.Max(total, 1));
            bool scrollNeeded = total > visible;
            float pkH = PkH(visible, scrollNeeded);
            float pkTop = rowY + 4 * scale;

            // Clamp inside the card: never below the bottom edge, and prefer
            // sitting below the Tiles section.
            pkTop = Math.Min(pkTop, maxBottom - pkH);
            pkTop = Math.Max(pkTop, tilesTop);

            // Short pane + tall picker: shrink the visible count (adding scroll
            // arrows) so the whole popover fits. If it still can't sit below the
            // Tiles section, let it rise to overlay the columns — but it must
            // NEVER extend past the card's bottom edge.
            if (pkTop + pkH > maxBottom)
            {
                float chrome = 22 /*header*/ + 22 /*overflow arrows*/ + (total == 0 ? 26 : 0);
                float avail = maxBottom - minTop - chrome;
                int fit = (int)Math.Floor(avail / PanePickerItemH);
                if (fit < 1) fit = 1;
                visible = Math.Min(visible, fit);
                scrollNeeded = total > visible;
                pkH = PkH(visible, scrollNeeded);
                pkTop = Math.Max(minTop, maxBottom - pkH);
            }

            picker = new SKRect(lx - 2, pkTop, rx + 2, pkTop + pkH);
            float ix = lx;
            float iRight = rx;
            float iy = pkTop + 22;
            if (total == 0)
            {
                pickerEmpty = new SKRect(ix, iy, iRight, iy + 22);
            }
            else
            {
                int scroll = Math.Max(0, Math.Min(state.PickerScrollIndex, Math.Max(0, total - (int)PanePickerMax)));
                int vis = Math.Min(visible, total - scroll);
                for (int i = 0; i < vis; i++)
                    pickerItems.Add(new SKRect(ix, iy + i * PanePickerItemH, iRight, iy + i * PanePickerItemH + PanePickerItemH));
                if (scrollNeeded)
                {
                    pickerUp = new SKRect(ix, iy + vis * PanePickerItemH, iRight, iy + vis * PanePickerItemH + 11);
                    pickerDown = new SKRect(ix, picker.Bottom - 11, iRight, picker.Bottom);
                }
            }
            pickerClose = new SKRect(picker.Right - 24, picker.Top + 4, picker.Right - 6, picker.Top + 22);
        }

        return new GlobalPaneLayout
        {
            Pane = p,
            Close = close,
            LaunchToggle = launchToggle,
            KioskToggle = kioskToggle,
            AlwaysOnTopToggle = alwaysOnTopToggle,
            ThemeSegments = themeSegs,
            ThresholdToggle = thresholdToggle,
            ThresholdMinus = thresholdMinus,
            ThresholdValue = thresholdValue,
            ThresholdPlus = thresholdPlus,
            GraphSpanSegments = graphSpanSegs,
            DecimalsSegments = decimalsSegs,
            // CPU + RAM chips only (Disk/GPU/Net are now rows).
            TileChips = chips,
            // new members:
            AddRows = addRows,
            AddButtons = addButtons,
            Picker = picker,
            PickerItems = pickerItems,
            PickerUp = pickerUp,
            PickerDown = pickerDown,
            PickerClose = pickerClose,
            PickerEmpty = pickerEmpty,
        };
    }

    /// <summary>
    /// Draws the global pane with the redesigned Tiles section. For the
    /// non-device path (state == null) this delegates to the original draw
    /// routine unchanged.
    /// </summary>
    public void DrawGlobalPane(SKCanvas canvas, GlobalPaneLayout layout, AppConfig config, Theme currentTheme, bool hoverClose, int hoverRow, GlobalPaneDeviceState? state = null, int hoverAddButton = -1, int hoverPickerItem = -1)
    {
        if (state is null)
        {
            DrawGlobalPane(canvas, layout, config, currentTheme, hoverClose, hoverRow);
            return;
        }

        var p = layout.Pane;
        using var round = new SKRoundRect(p, 14);
        using var bg = new SKPaint { Color = _theme.PaneBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(round, bg);
        using var border = new SKPaint { Color = _theme.TileBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawRoundRect(round, border);

        float pad = GlobalPanePad;
        float lx = p.Left + pad;
        float rx = p.Right - pad;

        using var headerPaint = new SKPaint { Color = _theme.TextPrimary, IsAntialias = true };
        var headerFont = TileRenderer.CachedFont("Segoe UI Semibold", 15);
        canvas.DrawText("Settings", lx, p.Top + pad + 14, headerFont, headerPaint);
        DrawCloseGlyph(canvas, layout.Close, hoverClose);

        // Recover the same scale the layout pass used, so label baselines line
        // up with the (scaled) control rects.
        float bodyH = (p.Bottom - p.Top) - 2 * pad - GlobalPaneHeaderH;
        float scale = Math.Clamp(bodyH / GlobalPaneNominalBodyH, GlobalPaneMinScale, GlobalPaneMaxScale);
        float rowH = 30 * scale;
        float segLabelH = 16;

        DrawRowLabel(canvas, layout.LaunchToggle.Left, layout.LaunchToggle.Top, rowH, "Launch at startup");
        DrawSwitch(canvas, layout.LaunchToggle, config.LaunchAtStartup, _theme.Accent);
        DrawRowLabel(canvas, layout.KioskToggle.Left, layout.KioskToggle.Top, rowH, "Kiosk mode");
        DrawSwitch(canvas, layout.KioskToggle, config.KioskMode, _theme.Accent);
        DrawRowLabel(canvas, layout.AlwaysOnTopToggle.Left, layout.AlwaysOnTopToggle.Top, rowH, "Always on top");
        DrawSwitch(canvas, layout.AlwaysOnTopToggle, config.AlwaysOnTop, _theme.Accent);
        DrawRowLabel(canvas, layout.ThresholdToggle.Left, layout.ThresholdToggle.Top, rowH, "Threshold alert");
        DrawSwitch(canvas, layout.ThresholdToggle, config.ThresholdEnabled, _theme.Accent);

        DrawRowLabel(canvas, lx, layout.ThresholdMinus.Top - segLabelH, segLabelH, "Alert at");
        DrawStepper(canvas, layout.ThresholdMinus, layout.ThresholdValue, layout.ThresholdPlus,
            $"{config.ThresholdPercent:0}%", _theme.Accent);

        DrawRowLabel(canvas, layout.ThemeSegments[0].Left, layout.ThemeSegments[0].Top - segLabelH, segLabelH, "Theme");
        string[] themeLabels = { "Midnight", "Obsidian", "Daybreak", "Transparent", "Frost Light", "Frost Dark" };
        int themeIdx = IndexOfTheme(currentTheme.Name);
        DrawSegments(canvas, layout.ThemeSegments, themeLabels, themeIdx, _theme.Accent);

        DrawRowLabel(canvas, layout.GraphSpanSegments[0].Left, layout.GraphSpanSegments[0].Top - segLabelH, segLabelH, "Graph span");
        string[] spanLabels = { "30s", "1m", "5m", "10m" };
        int spanIdx = GraphSpanIndex(config.GraphWindowSeconds);
        DrawSegments(canvas, layout.GraphSpanSegments, spanLabels, spanIdx, _theme.Accent);

        DrawRowLabel(canvas, layout.DecimalsSegments[0].Left, layout.DecimalsSegments[0].Top - segLabelH, segLabelH, "Decimals");
        string[] decLabels = { "0", "1", "2" };
        DrawSegments(canvas, layout.DecimalsSegments, decLabels, config.ValueDecimals, _theme.Accent);

        // ---- Tiles section header ----
        using var secPaint = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
        var secFont = TileRenderer.CachedFont("Segoe UI Semibold", 12);
        float tilesHeaderY = layout.TileChips[0].Top - 4 - 13;
        canvas.DrawText("Tiles", lx, tilesHeaderY + 13, secFont, secPaint);

        // CPU + RAM toggle chips.
        DrawChip(canvas, layout.TileChips[0], TileKind.Cpu, config.Tile(TileKind.Cpu).Enabled);
        DrawChip(canvas, layout.TileChips[1], TileKind.Ram, config.Tile(TileKind.Ram).Enabled);

        // Disk / GPU / Network management rows.
        var addKinds = new[] { TileKind.Disk, TileKind.Gpu, TileKind.Network };
        for (int i = 0; i < addKinds.Length; i++)
        {
            TileKind kind = addKinds[i];
            DrawAddRow(canvas, layout.AddRows[i], layout.AddButtons[i], kind, state.CountFor(kind), hoverAddButton == i, _theme.Accent);
        }

        // Version label pinned to the bottom of the settings pane.
        // Application.ProductVersion is the AssemblyInformationalVersion, which
        // the SDK appends "+<commit-sha>" to — show just the version number.
        using var verPaint = new SKPaint { Color = _theme.TextSecondary.WithAlpha(160), IsAntialias = true };
        var verFont = TileRenderer.CachedFont("Segoe UI", 11);
        string ver = Application.ProductVersion;
        int plus = ver.IndexOf('+');
        if (plus > 0) ver = ver.Substring(0, plus);
        string verText = $"PCGauger v{ver}";
        float verW = verFont.MeasureText(verText);
        canvas.DrawText(verText, p.Right - pad - verW, p.Bottom - pad - 4, verFont, verPaint);

        // Picker overlay (clamped inside the pane by the layout pass).
        if (state.ActivePickerKind.HasValue)
            DrawPicker(canvas, layout, state, _theme.Accent, hoverPickerItem);
    }

    private void DrawAddRow(SKCanvas canvas, SKRect row, SKRect btn, TileKind kind, int count, bool hoverBtn, SKColor accent)
    {
        string label = kind switch
        {
            TileKind.Disk => "Disk",
            TileKind.Gpu => "GPU",
            TileKind.Network => "Network",
            _ => kind.ToString(),
        };
        using var labelPaint = new SKPaint { Color = _theme.TextPrimary, IsAntialias = true };
        var labelFont = TileRenderer.CachedFont("Segoe UI Semibold", 13);
        string full = $"{label} · {count}";
        canvas.DrawText(full, row.Left, row.MidY + 5, labelFont, labelPaint);

        // "Add" button (right-aligned). The plus is a DRAWN vector cross
        // (matching the hand-drawn glyphs elsewhere in this file) — the
        // fullwidth plus U+FF0B renders as tofu (a square) in Segoe UI.
        using var rr = new SKRoundRect(btn, 8);
        using var bp = new SKPaint { Color = hoverBtn ? TilePalette.Soft(accent) : _theme.TileBorder.WithAlpha(70), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(rr, bp);

        using var tp = new SKPaint { Color = hoverBtn ? accent : _theme.TextSecondary, IsAntialias = true };
        var tf = TileRenderer.CachedFont("Segoe UI Semibold", 12);
        float plus = 10;
        float gap = 4;
        const string addTxt = "Add";
        float tw = tf.MeasureText(addTxt);
        float totalW = plus + gap + tw;
        float startX = btn.MidX - totalW / 2;
        float plusCx = startX + plus / 2;
        float plusCy = btn.MidY;
        using var plusP = new SKPaint
        {
            Color = hoverBtn ? accent : _theme.TextSecondary,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.8f,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
        };
        canvas.DrawLine(plusCx - plus / 2, plusCy, plusCx + plus / 2, plusCy, plusP);
        canvas.DrawLine(plusCx, plusCy - plus / 2, plusCx, plusCy + plus / 2, plusP);
        canvas.DrawText(addTxt, startX + plus + gap, btn.MidY + 4, tf, tp);
    }

    private void DrawPicker(SKCanvas canvas, GlobalPaneLayout layout, GlobalPaneDeviceState state, SKColor accent, int hoverItem)
    {
        var d = layout.Picker;
        if (d.IsEmpty) return;
        using var rr = new SKRoundRect(d, 10);
        using var p = new SKPaint { Color = _theme.PaneBackground, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(rr, p);
        using var b = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawRoundRect(rr, b);

        string kindLabel = state.ActivePickerKind switch
        {
            TileKind.Disk => "Add disk",
            TileKind.Gpu => "Add GPU",
            TileKind.Network => "Add network",
            _ => "Add device",
        };
        using var hdr = new SKPaint { Color = _theme.TextPrimary, IsAntialias = true };
        var hdrF = TileRenderer.CachedFont("Segoe UI Semibold", 12);
        canvas.DrawText(kindLabel, d.Left + 8, d.Top + 14, hdrF, hdr);
        DrawCloseGlyph(canvas, layout.PickerClose, false);

        if (state.PickerItems.Count == 0)
        {
            using var empty = new SKPaint { Color = _theme.TextSecondary, IsAntialias = true };
            var emptyF = TileRenderer.CachedFont("Segoe UI", 12);
            string msg = state.ActivePickerKind switch
            {
                TileKind.Disk => "All disks added",
                TileKind.Gpu => "All GPUs added",
                TileKind.Network => "All networks added",
                _ => "No devices found",
            };
            float ew = emptyF.MeasureText(msg);
            canvas.DrawText(msg, d.MidX - ew / 2, layout.PickerEmpty.MidY + 4, emptyF, empty);
            return;
        }

        int total = state.PickerItems.Count;
        int scroll = Math.Max(0, Math.Min(state.PickerScrollIndex, Math.Max(0, total - (int)PanePickerMax)));
        int visible = Math.Min((int)PanePickerMax, total - scroll);
        float x = d.Left + 6;
        float right = d.Right - 6;
        for (int i = 0; i < visible; i++)
        {
            int idx = scroll + i;
            var item = state.PickerItems[idx];
            var r = layout.PickerItems[i];
            bool hover = idx == hoverItem;
            DrawDeviceDropdownItem(canvas, r, item, hover, accent, x, right);
        }

        if (total > (int)PanePickerMax)
        {
            bool upOn = scroll > 0;
            bool downOn = scroll + visible < total;
            float ux = (layout.PickerUp.Left + layout.PickerUp.Right) / 2;
            using var upP = new SKPaint { Color = upOn ? _theme.TextSecondary : _theme.TextSecondary.WithAlpha(70), Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
            float uy = layout.PickerUp.MidY;
            canvas.DrawLine(ux - 4, uy + 2, ux, uy - 3, upP);
            canvas.DrawLine(ux, uy - 3, ux + 4, uy + 2, upP);
            using var downP = new SKPaint { Color = downOn ? _theme.TextSecondary : _theme.TextSecondary.WithAlpha(70), Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
            float dy = layout.PickerDown.MidY;
            canvas.DrawLine(ux - 4, dy - 2, ux, dy + 3, downP);
            canvas.DrawLine(ux, dy + 3, ux + 4, dy - 2, downP);
        }
    }

    // ---- shared device glyph (kind-appropriate) ----

    /// <summary>Draws a small kind-appropriate glyph inside <paramref name="r"/>.</summary>
    private void DrawDeviceGlyph(SKCanvas canvas, SKRect r, TileKind kind, SKColor color)
    {
        using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        float pad = 2;
        float l = r.Left + pad, t = r.Top + pad, rt = r.Right - pad, b = r.Bottom - pad;
        float w = rt - l, h = b - t;
        switch (kind)
        {
            case TileKind.Disk:
                // Drive: rounded rectangle with a small notch (spindle) center.
                var disk = new SKRoundRect(new SKRect(l, t, rt, b), 2.5f);
                canvas.DrawRoundRect(disk, paint);
                canvas.DrawCircle(r.MidX, r.MidY, Math.Min(w, h) * 0.18f, paint);
                break;
            case TileKind.Gpu:
                // Card: rectangle + a row of "pins" along the bottom.
                canvas.DrawRect(new SKRect(l, t, rt, b - h * 0.32f), paint);
                float pinY = b - h * 0.18f;
                for (int i = 0; i < 3; i++)
                {
                    float px = l + w * (0.25f + i * 0.25f);
                    canvas.DrawLine(px, pinY, px, b, paint);
                }
                break;
            case TileKind.Network:
                // Globe: circle + equator + meridian.
                canvas.DrawCircle(r.MidX, r.MidY, Math.Min(w, h) * 0.42f, paint);
                canvas.DrawLine(l, r.MidY, rt, r.MidY, paint);
                canvas.DrawLine(r.MidX, t, r.MidX, b, paint);
                break;
            default:
                canvas.DrawCircle(r.MidX, r.MidY, Math.Min(w, h) * 0.3f, paint);
                break;
        }
    }
}
