using System.Drawing.Drawing2D;

namespace WebPrint.Desktop;

static class Theme {
    public static readonly Color Ground = Color.FromArgb(245, 244, 242);
    public static readonly Color Surface = Color.White;
    public static readonly Color Raised = Color.FromArgb(247, 246, 244);
    public static readonly Color Ink = Color.FromArgb(23, 23, 21);
    public static readonly Color Muted = Color.FromArgb(98, 96, 91);
    public static readonly Color Border = Color.FromArgb(230, 227, 222);
    public static readonly Color Positive = Color.FromArgb(39, 98, 60);

    public static GraphicsPath Rounded(RectangleF bounds, float radius) {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

// Keep native controls, keyboard operation and accessibility; only paint their frame.
sealed class SurfacePanel : Panel {
    public SurfacePanel() {
        BackColor = Theme.Surface;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnPaintBackground(PaintEventArgs e) {
        if (Width < 2 || Height < 2) return;
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Ground);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = Theme.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), 20 * DeviceDpi / 96f);
        using var fill = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Window : BackColor);
        using var border = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Theme.Border);
        e.Graphics.FillPath(fill, outline);
        e.Graphics.DrawPath(border, outline);
    }
}

sealed class ThemeButton : Button {
    bool primary;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Primary { get => primary; set { if (primary == value) return; primary = value; Invalidate(); } }
    bool hovered, pressed;
    public ThemeButton(string title, bool primary = false) {
        this.primary = primary;
        Text = title;
        Size = new Size(112, 40);
        Margin = new Padding(0, 0, 10, 0);
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = primary ? Theme.Ink : Theme.Surface;
        ForeColor = primary ? Color.White : Theme.Ink;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e) {
        if (Width < 2 || Height < 2) return;
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Surface);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var fill = !Enabled ? Theme.Raised : primary ? (hovered ? Color.FromArgb(52, 51, 47) : Theme.Ink) : (hovered ? Theme.Raised : Theme.Surface);
        var ink = !Enabled ? Theme.Muted : primary ? Color.White : Theme.Ink;
        var edge = primary && Enabled ? fill : Theme.Border;
        if (SystemInformation.HighContrast) {
            fill = primary && Enabled ? SystemColors.Highlight : SystemColors.Control;
            ink = !Enabled ? SystemColors.GrayText : primary ? SystemColors.HighlightText : SystemColors.ControlText;
            edge = SystemColors.ControlText;
        }
        using var outline = Theme.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), Height / 2f);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(edge);
        e.Graphics.FillPath(brush, outline);
        e.Graphics.DrawPath(pen, outline);
        var textBounds = ClientRectangle;
        if (pressed && Enabled) textBounds.Offset(0, 1);
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, ink, flags);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -7, -7), ink, fill);
    }
}

sealed class PaperMark : Control {
    public PaperMark() { Size = new Size(32, 32); TabStop = false; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.ScaleTransform(Width / 28f, Height / 28f);
        using var pen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Theme.Ink, 1.5f) { LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(pen, new PointF[] { new(8, 4), new(21, 4), new(25, 8), new(25, 24), new(8, 24), new(8, 4) });
        e.Graphics.DrawLines(pen, new PointF[] { new(21, 4), new(21, 9), new(25, 9) });
        e.Graphics.DrawLines(pen, new PointF[] { new(4, 8), new(3, 8), new(3, 26), new(19, 26) });
        e.Graphics.DrawLine(pen, 12, 14, 21, 14); e.Graphics.DrawLine(pen, 12, 18, 19, 18);
    }
}
