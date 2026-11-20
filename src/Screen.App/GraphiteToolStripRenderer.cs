using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Media;
using DrawingColor = System.Drawing.Color;
using Forms = System.Windows.Forms;

namespace Screen.App;

/// <summary>Draws the native tray menu using the same resources as the WPF shell.</summary>
internal sealed class GraphiteToolStripRenderer : Forms.ToolStripProfessionalRenderer
{
    private readonly GraphiteColors _colors;
    private readonly double _menuRadius;
    private readonly double _itemRadius;

    private GraphiteToolStripRenderer(GraphiteColors colors) : base(colors)
    {
        _colors = colors;
        _menuRadius = ((CornerRadius)Application.Current.FindResource("Radius.Button")).TopLeft;
        _itemRadius = ((CornerRadius)Application.Current.FindResource("Radius.Control")).TopLeft;
        RoundedEdges = false;
    }

    public static void ApplyTo(Forms.ContextMenuStrip menu)
    {
        var colors = new GraphiteColors();
        var renderer = new GraphiteToolStripRenderer(colors);
        menu.Renderer = renderer;
        // GDI controls require an opaque BackColor. Apply the glass alpha at the native popup.
        menu.BackColor = DrawingColor.FromArgb(colors.Background.R, colors.Background.G, colors.Background.B);
        menu.Opacity = colors.Background.A / (double)byte.MaxValue;
        menu.ForeColor = colors.Text;
        menu.ShowImageMargin = false;
        menu.Padding = new Forms.Padding(4);
        menu.ItemAdded += (_, e) =>
        {
            if (e.Item is Forms.ToolStripMenuItem item)
                item.Padding = new Forms.Padding(7, 4, 7, 4);
        };
        menu.SizeChanged += (_, _) => renderer.UpdateRegion(menu);
        menu.DpiChangedAfterParent += (_, _) => renderer.UpdateRegion(menu);
        renderer.UpdateRegion(menu);
    }

    private static GraphicsPath Outline(RectangleF bounds, double logicalRadius, int dpi)
    {
        var radius = Math.Min((float)(logicalRadius * dpi / 96d), Math.Min(bounds.Width, bounds.Height) / 2f);
        var path = new GraphicsPath();
        path.AddRoundedRectangle(bounds, new SizeF(radius, radius));
        return path;
    }

    private void UpdateRegion(Forms.ToolStrip menu)
    {
        if (menu.ClientSize.Width <= 0 || menu.ClientSize.Height <= 0)
            return;
        using var path = Outline(menu.ClientRectangle, _menuRadius, menu.DeviceDpi);
        menu.Region = new Region(path);
    }

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(e.ToolStrip.BackColor);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Enabled || !(e.Item.Selected || e.Item.Pressed) || e.Item.Width <= 0 || e.Item.Height <= 0)
            return;
        using var path = Outline(new RectangleF(0, 0, e.Item.Width, e.Item.Height), _itemRadius, e.ToolStrip.DeviceDpi);
        using var brush = new SolidBrush(_colors.Hover);
        var state = e.Graphics.Save();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, path);
        e.Graphics.Restore(state);
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        var color = e.Item.Enabled ? _colors.Text : _colors.DisabledText;
        Forms.TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, color, e.TextFormat);
    }

    protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled != false ? _colors.Text : _colors.DisabledText;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new System.Drawing.Pen(_colors.Divider);
        var inset = (int)Math.Round(6 * e.ToolStrip.DeviceDpi / 96d);
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, inset, y, Math.Max(inset, e.Item.Width - inset), y);
    }

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        var bounds = e.ToolStrip.ClientRectangle;
        if (bounds.Width <= 1 || bounds.Height <= 1)
            return;
        using var path = Outline(new RectangleF(0.5f, 0.5f, bounds.Width - 1f, bounds.Height - 1f), _menuRadius, e.ToolStrip.DeviceDpi);
        using var pen = new System.Drawing.Pen(_colors.Edge);
        var state = e.Graphics.Save();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
        e.Graphics.Restore(state);
    }

    private sealed class GraphiteColors : Forms.ProfessionalColorTable
    {
        public GraphiteColors() => UseSystemColors = false;
        public DrawingColor Background { get; } = FromTheme("Brush.DarkGlassSolid");
        public DrawingColor Hover { get; } = FromTheme("Brush.Accent");
        public DrawingColor Text { get; } = FromTheme("Brush.Label");
        public DrawingColor DisabledText { get; } = FromTheme("Brush.Neutral500");
        public DrawingColor Divider { get; } = FromTheme("Brush.Separator");
        public DrawingColor Edge { get; } = FromTheme("Brush.EdgeHighlight");

        public override DrawingColor ToolStripDropDownBackground => Background;
        public override DrawingColor ImageMarginGradientBegin => Background;
        public override DrawingColor ImageMarginGradientMiddle => Background;
        public override DrawingColor ImageMarginGradientEnd => Background;
        public override DrawingColor MenuItemSelected => Hover;
        public override DrawingColor MenuItemBorder => Edge;
        public override DrawingColor MenuBorder => Edge;
        public override DrawingColor SeparatorDark => Divider;
        public override DrawingColor SeparatorLight => Divider;
        public override DrawingColor CheckBackground => Hover;
        public override DrawingColor CheckSelectedBackground => Hover;
        public override DrawingColor CheckPressedBackground => Hover;

        private static DrawingColor FromTheme(string key)
        {
            var color = ((SolidColorBrush)Application.Current.FindResource(key)).Color;
            return DrawingColor.FromArgb(color.A, color.R, color.G, color.B);
        }
    }
}
