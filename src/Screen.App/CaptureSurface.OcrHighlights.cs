using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Screen.Core;

namespace Screen.App;

internal sealed partial class CaptureSurface
{
    // Feedback lives in a sibling visual, never in the pixels supplied to OCR,
    // annotations, a pin, recording or export. Disabling hit tests preserves drawing input.
    private readonly Canvas _ocrHighlights = new() { IsHitTestVisible = false, ClipToBounds = true };
    private OcrDocument? _ocrDocument;
    private PixelRect _recognizedRegion;

    internal void ShowOcrLines(OcrDocument? document)
    {
        VerifyAccess();
        if (ReferenceEquals(_ocrDocument, document) && (document is null || _recognizedRegion == Selection))
            return;
        _ocrDocument = document;
        _recognizedRegion = Selection;
        LayoutOcrLines(animate: true);
    }

    private void LayoutOcrLines(bool animate)
    {
        _ocrHighlights.Children.Clear();
        if (_ocrDocument is null || Selection.IsEmpty || _recognizedRegion != Selection || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var selection = SelectionInDips;
        var scaleX = ActualWidth / _frame.Width;
        var scaleY = ActualHeight / _frame.Height;
        var localBounds = new Rect(0, 0, Selection.Width, Selection.Height);
        foreach (var line in _ocrDocument.Lines)
        {
            if (line.Bounds.IsEmpty)
                continue;
            // The recognition engine returns coordinates in the cropped selection, not the desktop.
            var bounds = Rect.Intersect(localBounds, ToRect(line.Bounds));
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                continue;
            var highlight = new Border
            {
                Style = _chrome.OcrHighlight,
                Width = bounds.Width * scaleX,
                Height = bounds.Height * scaleY
            };
            Canvas.SetLeft(highlight, bounds.X * scaleX);
            Canvas.SetTop(highlight, bounds.Y * scaleY);
            if (animate)
                Motion.PopIn(highlight, delayMs: _ocrHighlights.Children.Count * 100);
            _ocrHighlights.Children.Add(highlight);
        }
        if (animate && SystemParameters.ClientAreaAnimation && _ocrHighlights.Children.Count > 0)
        {
            const double bandHeight = 70;
            var offset = new TranslateTransform(0, selection.Height);
            var scan = new Rectangle { Fill = _chrome.ScanBand, Width = selection.Width, Height = bandHeight, RenderTransform = offset };
            _ocrHighlights.Children.Add(scan);
            // Base Y lies fully outside the clipped selection after the single sweep finishes.
            offset.BeginAnimation(TranslateTransform.YProperty, Motion.Tween(-bandHeight, selection.Height, 750));
        }
        _ocrHighlights.Measure(selection.Size);
        _ocrHighlights.Arrange(selection);
    }
}
