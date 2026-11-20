using System.Windows;
using System.Windows.Controls;

namespace Screen.App;

/// <summary>
/// Layout and positioning logic for CaptureOverlayWindow (floating panels, callouts).
/// </summary>
internal sealed partial class CaptureOverlayWindow
{
    private void PositionFloatingPanels()
    {
        if (_surface.ActualWidth <= 0 || _surface.ActualHeight <= 0)
        {
            return;
        }

        var viewport = _surface.IsSelectionCommitted ? _surface.SelectionViewport : _surface.CursorViewport;
        _ocrCard.SetAvailableSize(viewport.Width, viewport.Height);
        _translationCard.SetAvailableSize(viewport.Width, viewport.Height);
        _selectionHint.Visibility = _surface.IsSelectionCommitted ? Visibility.Collapsed : Visibility.Visible;
        if (_selectionHint.Visibility == Visibility.Visible)
        {
            PositionSelectionHint(viewport);
        }
        var selection = _surface.SelectionInDips;
        var visibleSelection = Rect.Intersect(selection, viewport);
        if (!visibleSelection.IsEmpty)
            selection = visibleSelection;
        if (_recordingMode)
        {
            PositionRecordingPanel(viewport, selection);
            return;
        }

        if (!_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            _toolbar.Visibility = Visibility.Collapsed;
            _stylePanel.Visibility = Visibility.Collapsed;
            return;
        }

        var entering = _toolbar.Visibility != Visibility.Visible;
        _toolbar.Visibility = Visibility.Visible;
        UpdateToolbarState();
        if (entering)
        {
            Motion.PopIn(_toolbar);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => UpdateToolIndicator(false));
        }
        // Keep every action visible. Only exceptionally narrow screens wrap
        // the strip; actions are never collapsed behind an overflow menu.
        _toolbar.MaxWidth = Math.Max(1, viewport.Width - 16);
        _toolbar.Measure(new Size(_toolbar.MaxWidth, double.PositiveInfinity));
        var left = FitX(viewport, selection.X, _toolbar.DesiredSize.Width);
        var desiredBelow = selection.Bottom + 12;
        var top = desiredBelow + _toolbar.DesiredSize.Height <= viewport.Bottom - 8
            ? desiredBelow
            : FitY(viewport, selection.Y - _toolbar.DesiredSize.Height - 12, _toolbar.DesiredSize.Height);
        Canvas.SetLeft(_toolbar, left);
        Canvas.SetTop(_toolbar, top);

        if (_stylePanel.Visibility == Visibility.Visible)
        {
            double? toolCenter = null;
            if (_toolButtons.TryGetValue(_surface.Tool, out var activeBtn) && activeBtn.IsVisible)
            {
                var btnPos = activeBtn.TranslatePoint(new Point(0, 0), _toolbar);
                toolCenter = left + btnPos.X + (activeBtn.ActualWidth > 0 ? activeBtn.ActualWidth / 2 : 18);
            }
            PlaceStylePanel(_stylePanel, viewport, FloatingBounds(_toolbar), toolCenter);
        }

        if (_ocrCard.Visibility == Visibility.Visible)
        {
            if (!_ocrCard.IsUserPositioned)
            {
                PositionCallout(_ocrCard, _ocrCard.SetPointer, selection, viewport);
            }
        }
        if (_translationCard.Visibility == Visibility.Visible)
        {
            PositionCallout(_translationCard, _translationCard.SetPointer, selection, viewport);
        }

        var cards = new FrameworkElement[] { _ocrCard, _translationCard }
            .Where(card => card.Visibility == Visibility.Visible).ToArray();
        if (cards.Any(card => FloatingBounds(card).IntersectsWith(ToolbarBounds())))
        {
            // If neither side of the normal strip has enough vertical room,
            // reserve the bottom edge for actions and fit results above it.
            var styleHeight = _stylePanel.Visibility == Visibility.Visible ? _stylePanel.DesiredSize.Height + 6 : 0;
            var stripTop = Math.Max(viewport.Top + 8, viewport.Bottom - _toolbar.DesiredSize.Height - styleHeight - 8);
            Canvas.SetTop(_toolbar, stripTop);
            if (styleHeight > 0)
            {
                Canvas.SetTop(_stylePanel, stripTop + _toolbar.DesiredSize.Height + 6);
            }
            if (_ocrCard.Visibility == Visibility.Visible && !_ocrCard.IsUserPositioned)
            {
                _ocrCard.SetAvailableSize(viewport.Width, Math.Max(200, stripTop - viewport.Top - 16));
                PositionCallout(_ocrCard, _ocrCard.SetPointer, selection, viewport);
            }
            if (_translationCard.Visibility == Visibility.Visible)
            {
                _translationCard.SetAvailableSize(viewport.Width, Math.Max(200, stripTop - viewport.Top - 16));
                PositionCallout(_translationCard, _translationCard.SetPointer, selection, viewport);
            }
        }
        PositionInPlaceTranslation(viewport, selection);
        RefreshGlassPanels();
    }

    private static double FitX(Rect viewport, double x, double width, double margin = 8) =>
        Math.Clamp(x, viewport.Left + margin, Math.Max(viewport.Left + margin, viewport.Right - width - margin));

    private static double FitY(Rect viewport, double y, double height, double margin = 8) =>
        Math.Clamp(y, viewport.Top + margin, Math.Max(viewport.Top + margin, viewport.Bottom - height - margin));

    private void PositionSelectionHint(Rect viewport)
    {
        _selectionHint.Measure(viewport.Size);
        var size = _selectionHint.DesiredSize;
        Canvas.SetLeft(_selectionHint, viewport.Left + Math.Max(0, (viewport.Width - size.Width) / 2));
        Canvas.SetTop(_selectionHint, Math.Max(viewport.Top, viewport.Bottom - size.Height - 22));
    }

    internal static void PlaceStylePanel(GlassPanel panel, Rect viewport, Rect toolbar, double? toolCenter)
    {
        const double gap = 8;
        panel.MaxWidth = Math.Max(1, viewport.Width - 2 * gap);
        panel.Measure(new Size(panel.MaxWidth, double.PositiveInfinity));
        var size = panel.DesiredSize;
        var left = toolCenter - size.Width / 2 ?? toolbar.Left;
        var above = toolbar.Top - size.Height - gap;
        var top = above >= viewport.Top + gap ? above : toolbar.Bottom + gap;
        Canvas.SetLeft(panel, FitX(viewport, left, size.Width));
        Canvas.SetTop(panel, FitY(viewport, top, size.Height));
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
    /// centered below the region, then above or inside when space is tight.
    /// </summary>
    private void PositionRecordingPanel(Rect viewport, Rect selectionDip)
    {
        if (_recordingPanel is null)
        {
            return;
        }
        _toolbar.Visibility = Visibility.Collapsed;
        _stylePanel.Visibility = Visibility.Collapsed;
        if (!_surface.IsSelectionCommitted || _surface.Selection.IsEmpty)
        {
            _recordingPanel.Visibility = Visibility.Collapsed;
            return;
        }
        var entering = _recordingPanel.Visibility != Visibility.Visible;
        _recordingPanel.Visibility = Visibility.Visible;
        _recordingPanel.MaxWidth = Math.Max(1, viewport.Width - 16);
        _recordingPanel.Measure(new Size(_recordingPanel.MaxWidth, double.PositiveInfinity));
        if (entering)
            Motion.PopIn(_recordingPanel);
        var panelWidth = _recordingPanel.DesiredSize.Width;
        var panelHeight = _recordingPanel.DesiredSize.Height;
        var left = selectionDip.Left + (selectionDip.Width - panelWidth) / 2;
        var below = selectionDip.Bottom + 12;
        var above = selectionDip.Top - panelHeight - 12;
        var top = below + panelHeight <= viewport.Bottom - 8 ? below
            : above >= viewport.Top + 8 ? above : selectionDip.Bottom - panelHeight - 8;
        Canvas.SetLeft(_recordingPanel, FitX(viewport, left, panelWidth));
        Canvas.SetTop(_recordingPanel, FitY(viewport, top, panelHeight));
    }

    private void PositionCallout(
        FrameworkElement card,
        Action<CalloutPointerSide, double> setPointer,
        Rect selectionDip,
        Rect viewport)
    {
        card.Measure(new Size(card.Width, Math.Max(200, viewport.Height - 16)));
        var cardHeight = card.DesiredSize.Height;
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
                var left = FitX(viewport, point.X, card.Width);
                var top = FitY(viewport, point.Y, cardHeight);
                var rect = new Rect(left, top, card.Width, cardHeight);
                var toolbar = ToolbarBounds();
                if (rect.IntersectsWith(toolbar))
                {
                    var above = toolbar.Top - cardHeight - 8;
                    var below = toolbar.Bottom + 8;
                    if (above >= viewport.Top + 8)
                    {
                        top = above;
                    }
                    else if (below + cardHeight <= viewport.Bottom - 8)
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
        Canvas.SetTop(card, cardTop);
    }
}
