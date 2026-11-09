using System.Windows;
using System.Windows.Controls;
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

        _ocrCard.SetAvailableSize(ActualWidth, ActualHeight);
        _translationCard.SetAvailableSize(ActualWidth, ActualHeight);
        if (_recordingMode)
        {
            PositionRecordingPanel();
            return;
        }
        _modeBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var modeLeft = Math.Max(8, (ActualWidth - _modeBar.DesiredSize.Width) / 2);
        const double modeTop = 8;
        Canvas.SetLeft(_modeBar, modeLeft);
        Canvas.SetTop(_modeBar, modeTop);
        var physicalScaleX = _frame.Width / ActualWidth;
        var physicalScaleY = _frame.Height / ActualHeight;
        _surface.MagnifierAvoidanceArea = new PixelRect(
            (int)(modeLeft * physicalScaleX) - 10,
            (int)(modeTop * physicalScaleY) - 6,
            (int)(_modeBar.DesiredSize.Width * physicalScaleX) + 20,
            (int)(_modeBar.DesiredSize.Height * physicalScaleY) + 12);

        if (!_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            _modeBar.Visibility = Visibility.Visible;
            _toolbar.Visibility = Visibility.Collapsed;
            _stylePanel.Visibility = Visibility.Collapsed;
            return;
        }

        _modeBar.Visibility = Visibility.Collapsed;
        _toolbar.Visibility = Visibility.Visible;
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
        var left = Math.Clamp(selection.X * scaleX, 8, Math.Max(8, ActualWidth - _toolbar.DesiredSize.Width - 8));
        var desiredBelow = selection.Bottom * scaleY + 12;
        var top = desiredBelow + _toolbar.DesiredSize.Height <= ActualHeight - 8
            ? desiredBelow
            : Math.Max(8, selection.Y * scaleY - _toolbar.DesiredSize.Height - 12);
        Canvas.SetLeft(_toolbar, left);
        Canvas.SetTop(_toolbar, top);

        if (_stylePanel.Visibility == Visibility.Visible)
        {
            _stylePanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_stylePanel, left);
            var belowToolbar = top + _toolbar.DesiredSize.Height + 6;
            Canvas.SetTop(_stylePanel, belowToolbar + _stylePanel.DesiredSize.Height <= ActualHeight - 6
                ? belowToolbar
                : Math.Max(6, top - _stylePanel.DesiredSize.Height - 6));
        }

        if (_ocrCard.Visibility == Visibility.Visible)
        {
            if (!_ocrCard.IsUserPositioned)
            {
                PositionCallout(_ocrCard, _ocrCard.SetPointer, selection, scaleX, scaleY);
            }
        }
        if (_translationCard.Visibility == Visibility.Visible)
        {
            PositionCallout(_translationCard, _translationCard.SetPointer, selection, scaleX, scaleY);
        }

        var cards = new FrameworkElement[] { _ocrCard, _translationCard }
            .Where(card => card.Visibility == Visibility.Visible).ToArray();
        if (cards.Any(card => FloatingBounds(card).IntersectsWith(ToolbarBounds())))
        {
            // If neither side of the normal strip has enough vertical room,
            // reserve the bottom edge for actions and fit results above it.
            var styleHeight = _stylePanel.Visibility == Visibility.Visible ? _stylePanel.DesiredSize.Height + 6 : 0;
            var stripTop = Math.Max(8, ActualHeight - _toolbar.DesiredSize.Height - styleHeight - 8);
            Canvas.SetTop(_toolbar, stripTop);
            if (styleHeight > 0)
            {
                Canvas.SetTop(_stylePanel, stripTop + _toolbar.DesiredSize.Height + 6);
            }
            if (_ocrCard.Visibility == Visibility.Visible && !_ocrCard.IsUserPositioned)
            {
                _ocrCard.SetAvailableSize(ActualWidth, Math.Max(200, stripTop - 16));
                PositionCallout(_ocrCard, _ocrCard.SetPointer, selection, scaleX, scaleY);
            }
            if (_translationCard.Visibility == Visibility.Visible)
            {
                _translationCard.SetAvailableSize(ActualWidth, Math.Max(200, stripTop - 16));
                PositionCallout(_translationCard, _translationCard.SetPointer, selection, scaleX, scaleY);
            }
        }
        PositionInPlaceTranslation();
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
        _recordingPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
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

    private void PositionCallout(
        FrameworkElement card,
        Action<CalloutPointerSide, double> setPointer,
        PixelRect selection,
        double scaleX,
        double scaleY)
    {
        card.Measure(new Size(card.Width, Math.Max(200, ActualHeight - 16)));
        var cardHeight = card.DesiredSize.Height;
        var selectionDip = new Rect(selection.X * scaleX, selection.Y * scaleY, selection.Width * scaleX, selection.Height * scaleY);
        var candidates = new[]
        {
            new Point(selectionDip.Right + 12, selectionDip.Top + (selectionDip.Height - cardHeight) / 2),
            new Point(selectionDip.Left - card.Width - 12, selectionDip.Top + (selectionDip.Height - cardHeight) / 2),
            new Point(selectionDip.Left + (selectionDip.Width - card.Width) / 2, selectionDip.Top - cardHeight - 12),
            new Point(selectionDip.Left + (selectionDip.Width - card.Width) / 2, selectionDip.Bottom + 12)
        };
        var best = candidates
            .Select((point, index) =>
            {
                var left = Math.Clamp(point.X, 8, Math.Max(8, ActualWidth - card.Width - 8));
                var top = Math.Clamp(point.Y, 8, Math.Max(8, ActualHeight - cardHeight - 8));
                var rect = new Rect(left, top, card.Width, cardHeight);
                var toolbar = ToolbarBounds();
                if (rect.IntersectsWith(toolbar))
                {
                    var above = toolbar.Top - cardHeight - 8;
                    var below = toolbar.Bottom + 8;
                    if (above >= 8)
                    {
                        top = above;
                    }
                    else if (below + cardHeight <= ActualHeight - 8)
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
        var cardLeft = best.Point.X;
        var cardTop = best.Point.Y;
        var pointerSide = best.Index switch
        {
            0 => CalloutPointerSide.Left,
            1 => CalloutPointerSide.Right,
            2 => CalloutPointerSide.Bottom,
            _ => CalloutPointerSide.Top
        };
        var anchorOffset = pointerSide is CalloutPointerSide.Left or CalloutPointerSide.Right
            ? Math.Clamp(selectionDip.Top + selectionDip.Height / 2 - cardTop, 24, Math.Max(24, cardHeight - 24))
            : Math.Clamp(selectionDip.Left + selectionDip.Width / 2 - cardLeft, 24, Math.Max(24, card.Width - 24));
        setPointer(pointerSide, anchorOffset);
        Canvas.SetLeft(card, cardLeft);
        Canvas.SetTop(card, Math.Max(8, cardTop));
    }
}
