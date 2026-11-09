using System.Drawing;
using System.Windows.Media;
using DrawingColor = System.Drawing.Color;
using Forms = System.Windows.Forms;

namespace Screen.App;

/// <summary>Brings the WinForms tray menu onto the same palette as the WPF windows.</summary>
internal sealed class GraphiteToolStripRenderer : Forms.ToolStripProfessionalRenderer
{
    private readonly GraphiteColors _colors;

    private GraphiteToolStripRenderer(GraphiteColors colors) : base(colors)
    {
        _colors = colors;
        RoundedEdges = false;
    }

    public static void ApplyTo(Forms.ToolStrip menu)
    {
        var colors = new GraphiteColors();
        menu.Renderer = new GraphiteToolStripRenderer(colors);
        menu.BackColor = colors.Background;
        menu.ForeColor = colors.Text;
    }

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(_colors.Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        var color = e.Item.Enabled && (e.Item.Selected || e.Item.Pressed)
            ? _colors.Hover
            : _colors.Background;
        using var brush = new SolidBrush(color);
        e.Graphics.FillRectangle(brush, new Rectangle(System.Drawing.Point.Empty, e.Item.Size));
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        // Render explicitly so the system's disabled gray/embossed text cannot
        // replace the readable theme color on this dark background.
        var color = e.Item.Enabled ? _colors.Text : _colors.DisabledText;
        Forms.TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, color, e.TextFormat);
    }

    protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled != false ? _colors.Text : _colors.DisabledText;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        var bounds = e.ToolStrip.ClientRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }
        using var pen = new System.Drawing.Pen(_colors.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, bounds.Width - 1, bounds.Height - 1);
    }

    private sealed class GraphiteColors : Forms.ProfessionalColorTable
    {
        public GraphiteColors() => UseSystemColors = false;

        public DrawingColor Background { get; } = FromTheme("Brush.SurfaceRaised", 0x24, 0x3D, 0x57);
        public DrawingColor Text { get; } = FromTheme("Brush.TextPrimary", 0xE8, 0xF1, 0xF7);
        public DrawingColor DisabledText { get; } = FromTheme("Brush.TextSecondary", 0xB5, 0xC9, 0xD9);
        public DrawingColor Hover { get; } = FromTheme("Brush.SurfaceHover", 0x30, 0x4E, 0x69);
        public DrawingColor Border { get; } = FromTheme("Brush.Border", 0x39, 0x55, 0x6F);

        public override DrawingColor ToolStripDropDownBackground => Background;
        public override DrawingColor ImageMarginGradientBegin => Background;
        public override DrawingColor ImageMarginGradientMiddle => Background;
        public override DrawingColor ImageMarginGradientEnd => Background;
        public override DrawingColor ImageMarginRevealedGradientBegin => Background;
        public override DrawingColor ImageMarginRevealedGradientMiddle => Background;
        public override DrawingColor ImageMarginRevealedGradientEnd => Background;
        public override DrawingColor MenuStripGradientBegin => Background;
        public override DrawingColor MenuStripGradientEnd => Background;
        public override DrawingColor MenuItemSelected => Hover;
        public override DrawingColor MenuItemSelectedGradientBegin => Hover;
        public override DrawingColor MenuItemSelectedGradientEnd => Hover;
        public override DrawingColor MenuItemPressedGradientBegin => Hover;
        public override DrawingColor MenuItemPressedGradientMiddle => Hover;
        public override DrawingColor MenuItemPressedGradientEnd => Hover;
        public override DrawingColor MenuItemBorder => Border;
        public override DrawingColor MenuBorder => Border;
        public override DrawingColor SeparatorDark => Border;
        public override DrawingColor SeparatorLight => Border;
        public override DrawingColor CheckBackground => Hover;
        public override DrawingColor CheckSelectedBackground => Hover;
        public override DrawingColor CheckPressedBackground => Hover;

        private static DrawingColor FromTheme(string key, byte red, byte green, byte blue)
        {
            if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
            {
                var color = brush.Color;
                return DrawingColor.FromArgb(color.A, color.R, color.G, color.B);
            }
            return DrawingColor.FromArgb(red, green, blue);
        }
    }
}
