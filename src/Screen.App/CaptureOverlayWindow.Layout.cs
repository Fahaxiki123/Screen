using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// Layout and positioning logic for CaptureOverlayWindow (floating panels, callouts).
/// </summary>
internal sealed partial class CaptureOverlayWindow
{
    private void PositionFloatingPanels()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        if (_recordingMode)
        {
            PositionRecordingPanel();
            return;
        }
        if (!_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            _modeBar.Visibility = Visibility.Visible;
            _modeBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double modeLeft = Math.Max(8, (ActualWidth - _modeBar.DesiredSize.Width) / 2);
            const double modeTop = 8;
            Canvas.SetLeft(_modeBar, modeLeft);
            Canvas.SetTop(_modeBar, modeTop);
            double physicalScaleX = _frame.Width / ActualWidth;
            double physicalScaleY = _frame.Height / ActualHeight;
            _surface.MagnifierAvoidanceArea = new PixelRect(
                (int)(modeLeft * physicalScaleX) - 10, (int)(modeTop * physicalScaleY) - 6,
                (int)(_modeBar.DesiredSize.Width * physicalScaleX) + 20, (int)(_modeBar.DesiredSize.Height * physicalScaleY) + 12);
            _toolbar.Visibility = Visibility.Collapsed;
            _stylePanel.Visibility = Visibility.Collapsed;
            return;
        }

        _surface.MagnifierAvoidanceArea = default;
        _modeBar.Visibility = Visibility.Collapsed;
        if (_toolbar.Visibility != Visibility.Visible)
        {
            _toolbar.Visibility = Visibility.Visible;
            AnimateToolbarEntry();
            // The target buttons receive their arranged coordinates after the first measure.
            Dispatcher.BeginInvoke(new Action(PositionFloatingPanels), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        var translationVisible = _translationCard.Visibility == Visibility.Visible || _inPlaceStatusBar.Visibility == Visibility.Visible;
        _translationButton.Background = translationVisible
            ? FindResourceBrush("Brush.AccentSubtleDark") : System.Windows.Media.Brushes.Transparent;
        _translationButton.Foreground = translationVisible
            ? FindResourceBrush("Brush.AccentText") : FindResourceBrush("Brush.OverlayIcon");
        // Keep every action visible. Only exceptionally narrow screens wrap
        // the strip; actions are never collapsed behind an overflow menu.
        _toolbar.MaxWidth = Math.Max(1, ActualWidth - 16);
        _toolbar.Measure(new Size(_toolbar.MaxWidth, double.PositiveInfinity));
        var scaleX = ActualWidth / _frame.Width;
        var scaleY = ActualHeight / _frame.Height;
        var selection = _surface.Selection;
        var selectionDip = new Rect(selection.X * scaleX, selection.Y * scaleY, selection.Width * scaleX, selection.Height * scaleY);
        bool showStyle = _stylePanel.Visibility == Visibility.Visible;
        if (showStyle)
        {
            _stylePanel.MaxWidth = _toolbar.DesiredSize.Width;
            _stylePanel.Measure(new Size(_stylePanel.MaxWidth, double.PositiveInfinity));
        }
        var (anchor, styleTop) = PlaceToolbarRows(selectionDip, new Size(ActualWidth, ActualHeight),
            _toolbar.DesiredSize, showStyle ? _stylePanel.DesiredSize : null);
        var left = anchor.X;
        var top = anchor.Y;
        bool moved = Canvas.GetLeft(_toolbar) != left || Canvas.GetTop(_toolbar) != top;
        Canvas.SetLeft(_toolbar, left);
        Canvas.SetTop(_toolbar, top);

        if (showStyle)
        {
            // Follow the active tool horizontally within the two-row group.
            var targetX = left;
            _styleToolCenterX = double.NaN;
            if (ActiveDrawingButton() is { IsVisible: true, ActualWidth: > 0 } activeButton)
            {
                var center = activeButton.TranslatePoint(new Point(activeButton.ActualWidth / 2, 0), _toolbar);
                _styleToolCenterX = left + center.X;
                targetX = _styleToolCenterX - _stylePanel.DesiredSize.Width / 2;
            }
            double styleWidth = _stylePanel.DesiredSize.Width;
            targetX = Math.Clamp(targetX, left, left + Math.Max(0, _toolbar.DesiredSize.Width - styleWidth));
            PositionStylePanelHorizontally(targetX, moved);
            Canvas.SetTop(_stylePanel, styleTop);
            _stylePanel.SetTopPointer(_styleToolCenterX - Canvas.GetLeft(_stylePanel));
        }

        void PlaceResultCard(GlassResultCard card, Action<double, double> setAvailableSize)
        {
            if (card.Visibility != Visibility.Visible)
                return;
            setAvailableSize(ActualWidth, ActualHeight);
            if (card.IsUserPositioned)
            {
                // A streaming translation or an OCR edit can grow after the user has dragged the card.
                card.Measure(new Size(card.Width, double.PositiveInfinity));
                Canvas.SetLeft(card, Math.Clamp(Canvas.GetLeft(card), 8, Math.Max(8, ActualWidth - card.DesiredSize.Width - 8)));
                Canvas.SetTop(card, Math.Clamp(Canvas.GetTop(card), 8, Math.Max(8, ActualHeight - card.DesiredSize.Height - 8)));
                return;
            }
            PositionCallout(card, selectionDip);
            var toolbar = ToolbarBounds();
            if (FloatingBounds(card).IntersectsWith(toolbar))
            {
                double freeHeight = Math.Max(toolbar.Top, ActualHeight - toolbar.Bottom) - 16;
                setAvailableSize(ActualWidth, Math.Max(1, freeHeight));
                PositionCallout(card, selectionDip);
            }
        }
        PlaceResultCard(_ocrCard, _ocrCard.SetAvailableSize);
        PlaceResultCard(_translationCard, _translationCard.SetAvailableSize);
        if (_inPlaceStatusBar.Visibility == Visibility.Visible)
        {
            _inPlaceStatusBar.MaxWidth = Math.Max(1, ActualWidth - 16);
            PositionCallout(_inPlaceStatusBar, selectionDip, preferAbove: true);
        }
    }

    internal static (Point Toolbar, double StyleTop) PlaceToolbarRows(Rect selection, Size viewport, Size toolbar, Size? style)
    {
        if (style is not Size styleSize)
            return (PlaceToolbarGroup(selection, viewport, toolbar), double.NaN);

        const double gap = 8;
        var group = new Size(Math.Max(toolbar.Width, styleSize.Width), toolbar.Height + gap + styleSize.Height);
        var anchor = PlaceToolbarGroup(selection, viewport, group);
        return (anchor, anchor.Y + toolbar.Height + gap);
    }

    internal static Point PlaceToolbarGroup(Rect selection, Size viewport, Size group)
    {
        const double gap = 12;
        double maxX = Math.Max(0, viewport.Width - group.Width);
        double maxY = Math.Max(0, viewport.Height - group.Height);
        Point[] candidates =
        [
            new(selection.Left, selection.Bottom + gap),
            new(selection.Left, selection.Top - group.Height - gap),
            new(selection.Right + gap, selection.Top),
            new(selection.Left - group.Width - gap, selection.Top),
            new(0, maxY), new(maxX, maxY), new(0, 0), new(maxX, 0)
        ];
        var best = new Point();
        double leastOverlap = double.PositiveInfinity;
        foreach (var candidate in candidates)
        {
            // Give up the decorative screen margin before covering the selection.
            foreach (var margin in new[] { 8d, 0d })
            {
                double insetX = Math.Min(margin, maxX / 2);
                double insetY = Math.Min(margin, maxY / 2);
                var point = new Point(Math.Clamp(candidate.X, insetX, maxX - insetX),
                    Math.Clamp(candidate.Y, insetY, maxY - insetY));
                var overlap = Rect.Intersect(selection, new Rect(point, group));
                if (overlap.IsEmpty || overlap.Width == 0 || overlap.Height == 0)
                    return point;
                double area = overlap.Width * overlap.Height;
                if (area < leastOverlap)
                {
                    leastOverlap = area;
                    best = point;
                }
            }
        }
        return best;
    }

    private void PositionStylePanelHorizontally(double targetX, bool instant)
    {
        if (instant)
        {
            _stylePanel.BeginAnimation(Canvas.LeftProperty, null);
            Canvas.SetLeft(_stylePanel, targetX);
            return;
        }
        var previous = (double)_stylePanel.GetAnimationBaseValue(Canvas.LeftProperty);
        if (double.IsFinite(previous) && Math.Abs(previous - targetX) < 0.01)
            return;

        double current = Canvas.GetLeft(_stylePanel);
        Canvas.SetLeft(_stylePanel, targetX);
        if (!double.IsFinite(current))
            return;

        // A wide-to-narrow switch can put the new tool outside the old strip.
        // Start the slide with its shoulder already underneath the selected button.
        double width = _stylePanel.DesiredSize.Width;
        double inset = Math.Min(width / 2, (1 + _stylePanel.Smoothing) * _stylePanel.CornerRadiusRadius + 1);
        if (double.IsFinite(_styleToolCenterX))
            current = Math.Clamp(current, _styleToolCenterX - width + inset, _styleToolCenterX - inset);
        var slide = new DoubleAnimation(current, targetX, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        _stylePanel.BeginAnimation(Canvas.LeftProperty, slide);
    }

    private static Rect FloatingBounds(FrameworkElement element) => new(
        Canvas.GetLeft(element), Canvas.GetTop(element), element.DesiredSize.Width, element.DesiredSize.Height);

    private Rect ToolbarBounds()
    {
        var bounds = FloatingBounds(_toolbar);
        if (_stylePanel.Visibility == Visibility.Visible)
        {
            bounds.Union(FloatingBounds(_stylePanel));
        }
        return bounds;
    }

    /// <summary>
    /// Recording mode replaces every annotation panel with the setup card:
    /// centered inside the committed region, falling back below/above it when
    /// the region is too small to host the card.
    /// </summary>
    private void PositionRecordingPanel()
    {
        if (_recordingPanel is null)
        {
            return;
        }
        _modeBar.Visibility = Visibility.Collapsed;
        _toolbar.Visibility = Visibility.Collapsed;
        _stylePanel.Visibility = Visibility.Collapsed;
        if (!_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            _recordingPanel.Visibility = Visibility.Collapsed;
            return;
        }
        _recordingPanel.Visibility = Visibility.Visible;
        _recordingPanel.MaxWidth = Math.Max(1, ActualWidth - 16);
        _recordingPanel.Measure(new Size(_recordingPanel.MaxWidth, double.PositiveInfinity));
        var scaleX = ActualWidth / _frame.Width;
        var scaleY = ActualHeight / _frame.Height;
        var selection = _surface.Selection;
        var panelWidth = _recordingPanel.DesiredSize.Width;
        var panelHeight = _recordingPanel.DesiredSize.Height;
        var selectionDip = new Rect(
            selection.X * scaleX,
            selection.Y * scaleY,
            selection.Width * scaleX,
            selection.Height * scaleY);

        double left;
        double top;
        if (selectionDip.Width >= panelWidth + 24 && selectionDip.Height >= panelHeight + 24)
        {
            // Feishu placement: inside the region, dead center.
            left = selectionDip.Left + (selectionDip.Width - panelWidth) / 2;
            top = selectionDip.Top + (selectionDip.Height - panelHeight) / 2;
        }
        else if (selectionDip.Bottom + 12 + panelHeight <= ActualHeight - 8)
        {
            left = selectionDip.Left + (selectionDip.Width - panelWidth) / 2;
            top = selectionDip.Bottom + 12;
        }
        else
        {
            left = selectionDip.Left + (selectionDip.Width - panelWidth) / 2;
            top = Math.Max(8, selectionDip.Top - panelHeight - 12);
        }
        Canvas.SetLeft(_recordingPanel, Math.Clamp(left, 8, Math.Max(8, ActualWidth - panelWidth - 8)));
        Canvas.SetTop(_recordingPanel, Math.Clamp(top, 8, Math.Max(8, ActualHeight - panelHeight - 8)));
    }

    private void PositionCallout(FrameworkElement card, Rect selectionDip, bool preferAbove = false)
    {
        card.Measure(new Size(Math.Max(1, ActualWidth - 16), Math.Max(200, ActualHeight - 16)));
        var point = PlaceCallout(selectionDip, ToolbarBounds(), new Size(ActualWidth, ActualHeight), card.DesiredSize, preferAbove);
        Canvas.SetLeft(card, point.X);
        Canvas.SetTop(card, point.Y);
    }

    internal static Point PlaceCallout(Rect selectionDip, Rect toolbar, Size viewport, Size card, bool preferAbove)
    {
        var cardHeight = card.Height;
        double centerX = selectionDip.Left + (selectionDip.Width - card.Width) / 2;
        if (preferAbove)
        {
            var above = new Point(Math.Clamp(centerX, 8, Math.Max(8, viewport.Width - card.Width - 8)), selectionDip.Top - cardHeight - 10);
            if (above.Y >= 8 && !new Rect(above, card).IntersectsWith(toolbar))
                return above;
        }
        var candidates = new[]
        {
            new Point(selectionDip.Right + 12, selectionDip.Top + (selectionDip.Height - cardHeight) / 2),
            new Point(selectionDip.Left - card.Width - 12, selectionDip.Top + (selectionDip.Height - cardHeight) / 2),
            new Point(centerX, selectionDip.Top - cardHeight - 12),
            new Point(centerX, selectionDip.Bottom + 12),
            new Point(toolbar.Right + 8, toolbar.Top),
            new Point(toolbar.Left - card.Width - 8, toolbar.Top)
        };
        var best = candidates
            .Select((point, index) =>
            {
                var left = Math.Clamp(point.X, 8, Math.Max(8, viewport.Width - card.Width - 8));
                var top = Math.Clamp(point.Y, 8, Math.Max(8, viewport.Height - cardHeight - 8));
                var rect = new Rect(left, top, card.Width, cardHeight);
                if (rect.IntersectsWith(toolbar))
                {
                    var above = toolbar.Top - cardHeight - 8;
                    var below = toolbar.Bottom + 8;
                    if (above >= 8)
                    {
                        top = above;
                    }
                    else if (below + cardHeight <= viewport.Height - 8)
                    {
                        top = below;
                    }
                    rect = new Rect(left, top, card.Width, cardHeight);
                }
                var overlap = Rect.Intersect(rect, selectionDip);
                var toolbarOverlap = Rect.Intersect(rect, toolbar);
                return new
                {
                    Point = new Point(left, top),
                    ToolbarOverlap = toolbarOverlap.IsEmpty ? 0 : toolbarOverlap.Width * toolbarOverlap.Height,
                    Overlap = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height,
                    Index = index
                };
            })
            .OrderBy(candidate => candidate.ToolbarOverlap)
            .ThenBy(candidate => candidate.Overlap)
            .ThenBy(candidate => candidate.Index)
            .First();
        return best.Point;
    }
}
