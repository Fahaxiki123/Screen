using System.Windows;
using System.Windows.Controls;
using Screen.Core;

namespace Screen.App;

/// <summary>A click-through frame around the captured region, on the physical virtual desktop.</summary>
internal sealed class RecordingBorderWindow : Window
{
    public RecordingBorderWindow(PixelRect region)
    {
        Title = "Screen 录制区域";
        var canvas = RecordingRegionWindow.CreateMask(this, region, "Recording.Mask", out var cutout);
        const double thickness = 1.5;
        var guide = new Border
        {
            BorderThickness = new Thickness(thickness),
            Width = cutout.Width + 2 * thickness,
            Height = cutout.Height + 2 * thickness
        };
        guide.SetResourceReference(Border.BorderBrushProperty, "Brush.Record");
        Canvas.SetLeft(guide, cutout.Left - thickness);
        Canvas.SetTop(guide, cutout.Top - thickness);
        canvas.Children.Add(guide);

        // Preserve the original resolution readout when there is space above the frame.
        if (cutout.Top >= 28)
        {
            var label = new TextBlock { Text = $"REC · {region.Width} × {region.Height}", FontSize = 11, FontWeight = FontWeights.SemiBold };
            var tag = new GlassPanel { Variant = GlassVariant.Solid, Padding = new Thickness(7, 2, 7, 2), Content = label };
            tag.SetResourceReference(GlassPanel.CornerRadiusProperty, "Radius.Key");
            Canvas.SetLeft(tag, cutout.Left);
            Canvas.SetTop(tag, cutout.Top - 25);
            canvas.Children.Add(tag);
        }
    }
}
