using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Screen.Core;

namespace Screen.App;

/// <summary>Region + options confirmed on the recording setup panel.</summary>
internal sealed record RecordingSelection(
    PixelRect Region,
    bool ShowCursor,
    bool ShowClickEffects,
    bool RecordSystemAudio,
    bool RecordMicrophone,
    RecordingFormat Format = RecordingFormat.Mp4);

/// <summary>
/// Main structure for CaptureOverlayWindow (fields, constructor, initialization).
/// </summary>
internal sealed partial class CaptureOverlayWindow : Window
{
    private readonly CaptureFrame _frame;
    private readonly IScreenCaptureService _captureService;
    private readonly IOcrEngine _ocrEngine;
    private readonly ITranslationProvider _translationProvider;
    private readonly CredentialService _credentialService;
    private readonly CaptureSurface _surface;
    private readonly Canvas _root;
    private readonly GlassPanel _selectionHint;
    private readonly WrapPanel _toolbar;
    private readonly GlassPanel _stylePanel;
    private readonly PinWindowManager _pinManager;
    private readonly JsonSettingsStore _settingsStore;
    private readonly OcrCard _ocrCard;
    private readonly TranslationCard _translationCard;
    private readonly ContextMenu _translationPopup;
    private readonly Button _translationButton;
    private readonly Button _translationDropDownButton;
    private readonly Dictionary<CaptureTool, RadioButton> _toolButtons = [];
    private bool _recordingMode;
    private WrapPanel? _recordingPanel;
    private bool _showCursorChoice = true;
    private bool _recordSystemAudioChoice;
    private bool _recordMicrophoneChoice;
    private CancellationTokenSource? _translationOperation;
    private ScrollCaptureController? _scrollCapture;
    private uint _color = AnnotationArgb("Red");
    private double _thickness = 4;
    private double _fontSize = 16;
    private double _numberSize = 24;
    private int _textVariant = AnnotationVariants.TextRegular;
    private int _mosaicVariant = AnnotationVariants.MosaicPixelate;
    private double _mosaicSize = 32;
    private double _mosaicIntensity = 0.7;
    private bool _applyingSelectedStyle;

    /// <summary>Raised in recording mode when the user confirms the region via the setup panel.</summary>
    public event EventHandler<RecordingSelection>? RecordingRegionConfirmed;

    public CaptureOverlayWindow(
        CaptureFrame frame,
        IScreenCaptureService captureService,
        WindowSnapService windowSnapService,
        PinWindowManager pinManager,
        IOcrEngine ocrEngine,
        ITranslationProvider translationProvider,
        CredentialService credentialService,
        JsonSettingsStore settingsStore,
        bool recordingMode = false)
    {
        _frame = frame;
        _captureService = captureService;
        _pinManager = pinManager;
        _ocrEngine = ocrEngine;
        _translationProvider = translationProvider;
        _credentialService = credentialService;
        _settingsStore = settingsStore;
        _recordingMode = recordingMode;
        Title = recordingMode ? "Screen 录屏" : "Screen 截图";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Brush.Black");
        Cursor = Cursors.Cross;

        _root = new Canvas { ClipToBounds = true };
        _root.SetValue(_backdropHostProperty, this);
        Content = _root;
        _surface = new CaptureSurface(
            frame,
            windowSnapService.GetWindows(frame.ScreenBounds),
            windowSnapService);
        _surface.SelectionChanged += (_, _) => OnCaptureSelectionChanged();
        _surface.AnnotationContentChanged += (_, _) => OnCaptureAnnotationsChanged();
        _surface.SelectionCommitted += (_, _) => OnSelectionCommitted();
        _root.Children.Add(_surface);

        InitializeSurfaceAdornments();
        _selectionHint = BuildSelectionHint();
        _root.Children.Add(_selectionHint);
        if (recordingMode)
        {
            // Single-purpose selection: no mode switching, and the setup
            // panel replaces the annotation toolbar after commit.
            _recordingPanel = BuildRecordingPanel();
            _recordingPanel.Visibility = Visibility.Collapsed;
            _root.Children.Add(_recordingPanel);
        }
        _toolbar = BuildToolbar(out _translationButton, out _translationDropDownButton);
        _root.Children.Add(_toolbar);
        _stylePanel = BuildStylePanel();
        _root.Children.Add(_stylePanel);
        Panel.SetZIndex(_toolbar, 20);
        Panel.SetZIndex(_stylePanel, 21);

        _ocrCard = new OcrCard(ocrEngine, _surface.RenderSelection, TryExtractDirectTextAsync)
        {
            Visibility = Visibility.Collapsed
        };
        _root.Children.Add(_ocrCard);
        _ocrCard.DocumentRecognized += document => _surface.ShowOcrLines(document);
        _translationCard = new TranslationCard { Visibility = Visibility.Collapsed };
        _translationCard.RetranslateRequested += (_, _) => TranslateWithCurrentLanguageAsync(this, new RoutedEventArgs());
        _translationCard.TargetLanguageChanged += async (_, language) =>
        {
            try
            {
                _settingsStore.Update(latest => latest.Translation = latest.Translation with { TargetLanguage = language });
                await TranslateSelectionAsync(language);
            }
            catch (Exception exception)
            {
                _translationCard.SetError(exception.Message);
            }
        };
        _root.Children.Add(_translationCard);
        _translationCard.IsVisibleChanged += (_, _) => PositionFloatingPanels();
        _ocrCard.IsVisibleChanged += (_, _) => PositionFloatingPanels();
        _translationPopup = BuildTranslationPopup();
        InitializeInPlaceTranslation();
        _surface.SelectedAnnotationChanged += (_, _) => SyncSelectedStyle();
        _ocrCard.RecognitionStateChanged += (_, _) => UpdateProcessingIndicator();
        _ocrCard.IsVisibleChanged += (_, _) =>
        {
            if (_ocrCard.Visibility != Visibility.Visible)
            {
                _ocrCard.CancelRecognition();
                _surface.ShowOcrLines(null);
            }
        };
        _translationCard.IsVisibleChanged += (_, _) =>
        {
            if (_translationCard.Visibility != Visibility.Visible && !_inPlaceMode)
            {
                _surface.ShowOcrLines(null);
                CancelTranslation();
            }
        };

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) =>
        {
            _surface.Width = ActualWidth;
            _surface.Height = ActualHeight;
            PositionFloatingPanels();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) =>
        {
            _captureClosed = true;
            ReleaseGlassBackdrop();
            CancelTextOperations();
            _translationPopup.IsOpen = false;
            // Cancel without disposing: an in-flight translation may still be
            // linking this token, and disposing it surfaces
            // ObjectDisposedException instead of cancellation. The source is
            // reclaimed once the task drops its reference.
            _translationOperation?.Cancel();
            _ocrCard.Dispose();
            // If a long capture is somehow still active when this window dies,
            // tear it down — otherwise its global keyboard hook and helper
            // windows would linger for the rest of the process lifetime.
            _scrollCapture?.Dispose();
        };
        Loaded += PrepareGlassBackdropAsync;
        LayoutUpdated += OnGlassLayoutUpdated;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var dpi = NativeMethods.GetDpiAtPoint(new NativeMethods.NativePoint(
            _frame.ScreenBounds.X + _frame.ScreenBounds.Width / 2,
            _frame.ScreenBounds.Y + _frame.ScreenBounds.Height / 2));
        Width = _frame.Width * 96.0 / dpi;
        Height = _frame.Height * 96.0 / dpi;
        NativeMethods.SetWindowPos(
            handle,
            new nint(-1),
            _frame.ScreenBounds.X,
            _frame.ScreenBounds.Y,
            _frame.ScreenBounds.Width,
            _frame.ScreenBounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    private void OnSelectionCommitted()
    {
        PositionFloatingPanels();
        // Keep focus on the capture surface for Enter, Esc and annotation shortcuts.
        _surface.Focus();
    }
}
