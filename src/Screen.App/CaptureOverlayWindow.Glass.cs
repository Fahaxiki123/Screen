using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Screen.App;

internal sealed partial class CaptureOverlayWindow
{
    private readonly HashSet<GlassPanel> _glassPanels = [];
    private readonly CancellationTokenSource _glassLifetime = new();
    private BitmapSource? _blurredFrame;

    // WPF optimizes Loaded delivery for template children. An inherited scope
    // tracks both existing panels and ones inserted later (OCR, style panels),
    // without relying on a global class handler or walking the tree per frame.
    private static readonly DependencyProperty BackdropHostProperty = DependencyProperty.RegisterAttached(
        "BackdropHost", typeof(CaptureOverlayWindow), typeof(CaptureOverlayWindow),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, OnBackdropHostChanged));

    private static void OnBackdropHostChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not GlassPanel panel)
            return;
        if (e.OldValue is CaptureOverlayWindow previous)
        {
            previous._glassPanels.Remove(panel);
            panel.BackdropSource = null;
        }
        if (e.NewValue is CaptureOverlayWindow current)
            current._glassPanels.Add(panel);
    }

    private async void PrepareGlassBackdrop(object sender, RoutedEventArgs e)
    {
        Loaded -= PrepareGlassBackdrop;
        var token = _glassLifetime.Token;
        try
        {
            var source = await GlassBackdropProvider.CreateAsync(_frame, _frame.DpiX / 96.0, token);
            if (!token.IsCancellationRequested)
            {
                _blurredFrame = source;
                RefreshGlassPanels();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or OutOfMemoryException)
        {
            // A decorative background must never abort the capture.
            System.Diagnostics.Trace.TraceWarning("Glass backdrop unavailable: {0}", ex.Message);
        }
    }

    private void OnGlassLayoutUpdated(object? sender, EventArgs e) => RefreshGlassPanels();

    private void RefreshGlassPanels()
    {
        if (_blurredFrame is not null)
            foreach (var panel in _glassPanels)
                RefreshGlass(panel);
    }

    private void RefreshGlass(GlassPanel panel)
    {
        if (_blurredFrame is null || panel.Variant != GlassVariant.Glass || !panel.IsVisible || panel.ActualWidth <= 0 || panel.ActualHeight <= 0
            || ActualWidth <= 0 || ActualHeight <= 0 || !_root.IsAncestorOf(panel))
            return;
        var origin = panel.TranslatePoint(new Point(), _root);
        var sx = _frame.Width / ActualWidth;
        var sy = _frame.Height / ActualHeight;
        var bounds = new Rect(origin.X * sx, origin.Y * sy, panel.ActualWidth * sx, panel.ActualHeight * sy);
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var selected = _surface.Selection;
        var dim = selected.IsEmpty || center.X < selected.X || center.X >= selected.Right || center.Y < selected.Y || center.Y >= selected.Bottom;
        const double factor = GlassBackdropProvider.DownsampleFactor;
        var view = new Rect(bounds.X / factor, bounds.Y / factor, bounds.Width / factor, bounds.Height / factor);
        if (panel.BackdropViewbox != view)
            panel.BackdropViewbox = view;
        if (panel.IsDimmed != dim)
            panel.IsDimmed = dim;
        if (!ReferenceEquals(panel.BackdropSource, _blurredFrame))
            panel.BackdropSource = _blurredFrame;
    }

    private void ReleaseGlassBackdrop()
    {
        LayoutUpdated -= OnGlassLayoutUpdated;
        _glassLifetime.Cancel();
        _glassLifetime.Dispose();
        foreach (var panel in _glassPanels)
            panel.BackdropSource = null;
        _root.ClearValue(BackdropHostProperty);
        _glassPanels.Clear();
        _blurredFrame = null;
    }

    private GlassPanel BuildSelectionHint()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, text) in new[] { ("拖动", _recordingMode ? "选择录制区域" : "截取区域"), ("单击", _recordingMode ? "选择窗口" : "截取窗口"), ("Esc", "取消") })
        {
            var keycap = new Border { Child = new TextBlock { Text = key, VerticalAlignment = VerticalAlignment.Center } };
            keycap.SetResourceReference(StyleProperty, "KeycapStyle");
            row.Children.Add(keycap);
            row.Children.Add(new TextBlock { Text = text, Margin = new Thickness(5, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        return new GlassPanel { Content = row, Padding = new Thickness(9, 5, 0, 5), IsHitTestVisible = false };
    }
}
