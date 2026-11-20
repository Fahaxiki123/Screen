using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Screen.Core;

namespace Screen.App;

internal sealed partial class CaptureSurface
{
    // A retained child visual lets WPF animate only the glow. The screenshot and
    // annotation render path stays idle, and RenderSelection never includes it.
    private readonly DrawingVisual _processingVisual = new();
    private readonly TranslateTransform _processingTranslate = new();
    private PixelRect _processingBounds;
    private Size _processingSize;
    private bool _processingVisualActive;

    public bool IsProcessing { get; private set; }

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _processingVisual,
        1 => _ocrHighlights,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private void InitializeProcessingVisual()
    {
        AddVisualChild(_processingVisual);
        AddVisualChild(_ocrHighlights);
        Unloaded += (_, _) =>
        {
            SetProcessing(false);
            ShowOcrLines(null);
        };
        SelectionChanged += (_, _) =>
        {
            if (_ocrDocument is not null && _recognizedRegion != Selection)
                ShowOcrLines(null);
        };
    }

    public void SetProcessing(bool isProcessing)
    {
        VerifyAccess();
        if (IsProcessing == isProcessing)
        {
            return;
        }

        IsProcessing = isProcessing;
        RefreshProcessingVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RefreshProcessingVisual();
        LayoutOcrLines(animate: false);
    }

    private void RefreshProcessingVisual()
    {
        if (!IsProcessing || Selection.IsEmpty || ActualWidth <= 0 || ActualHeight <= 0)
        {
            if (_processingVisualActive)
            {
                _processingTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                using (_processingVisual.RenderOpen())
                { }
                _processingVisualActive = false;
            }
            return;
        }

        var size = new Size(ActualWidth, ActualHeight);
        if (_processingVisualActive && _processingBounds == Selection && _processingSize == size)
        {
            return;
        }

        _processingBounds = Selection;
        _processingSize = size;
        _processingVisualActive = true;
        var bandHeight = Math.Clamp(Selection.Height * 0.32, 24, 120);
        var lineHeight = Math.Min(1.5, Selection.Height);
        var start = Selection.Y - bandHeight / 2 + lineHeight / 2;
        var end = Selection.Bottom - bandHeight / 2 - lineHeight / 2;
        _processingTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        _processingTranslate.Y = start;
        using (var dc = _processingVisual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(ActualWidth / _frame.Width, ActualHeight / _frame.Height));
            dc.PushClip(new RectangleGeometry(ToRect(Selection)));
            dc.PushTransform(_processingTranslate);
            dc.DrawRectangle(_chrome.ScanBand, null, new Rect(Selection.X, 0, Selection.Width, bandHeight));
            dc.DrawRectangle(_chrome.ScanLine, null, new Rect(Selection.X, bandHeight / 2 - lineHeight / 2, Selection.Width, lineHeight));
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }

        _processingTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(start, end, TimeSpan.FromSeconds(1.55))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }
}
