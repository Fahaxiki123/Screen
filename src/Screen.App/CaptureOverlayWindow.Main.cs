using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Screen.Core;

namespace Screen.App;

internal enum CaptureEntryMode
{
    Screenshot,
    ScrollCapture,
    Ocr,
    Translation,
    Recording
}

/// <summary>Region + options confirmed on the recording setup panel.</summary>
internal sealed record RecordingSelection(
    PixelRect Region,
    bool ShowCursor,
    bool ShowClickEffects,
    bool RecordSystemAudio,
    bool RecordMicrophone);

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
    private readonly Border _modeBar;
    private readonly Border _toolbar;
    private readonly Border _stylePanel;
    private readonly PinWindowManager _pinManager;
    private readonly JsonSettingsStore _settingsStore;
    private readonly OcrCard _ocrCard;
    private readonly TranslationCard _translationCard;
    private readonly Popup _translationPopup;
    private readonly Button _translationButton;
    private readonly Button _translationDropDownButton;
    private readonly Dictionary<CaptureTool, Button> _toolButtons = [];
    private readonly Dictionary<CaptureEntryMode, Button> _modeButtons = [];
    private readonly bool _recordingMode;
    private Border? _recordingPanel;
    private LucideIconElement? _cursorToggleIcon;
    private LucideIconElement? _speakerToggleIcon;
    private LucideIconElement? _micToggleIcon;
    private bool _showCursorChoice = true;
    private bool _recordSystemAudioChoice;
    private bool _recordMicrophoneChoice;
    private CaptureEntryMode _entryMode = CaptureEntryMode.Screenshot;
    private CancellationTokenSource? _translationOperation;
    private ScrollCaptureController? _scrollCapture;
    private uint _color = 0xFFFF4D4F;
    private double _thickness = 4;
    private double _fontSize = 16;
    private int _textVariant = AnnotationVariants.TextRegular;
    private int _mosaicVariant = AnnotationVariants.MosaicPixelate;
    private double _mosaicSize = 32;
    private double _mosaicIntensity = 0.7;

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
        if (recordingMode)
        {
            _entryMode = CaptureEntryMode.Recording;
        }
        Title = recordingMode ? "Screen 录屏" : "Screen 截图";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;

        _root = new Canvas { ClipToBounds = true };
        Content = _root;
        _surface = new CaptureSurface(
            frame,
            windowSnapService.GetWindows(frame.ScreenBounds),
            windowSnapService);
        _surface.SelectionChanged += (_, _) => OnCaptureSelectionChanged();
        _surface.AnnotationContentChanged += (_, _) => CancelTextOperations();
        _surface.SelectionCommitted += (_, _) => OnSelectionCommitted();
        _root.Children.Add(_surface);

        _modeBar = BuildModeBar();
        _root.Children.Add(_modeBar);
        if (recordingMode)
        {
            // Single-purpose selection: no mode switching, and the setup
            // panel replaces the annotation toolbar after commit.
            _modeBar.Visibility = Visibility.Collapsed;
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
        _translationCard = new TranslationCard { Visibility = Visibility.Collapsed };
        _translationCard.RetranslateRequested += (_, _) => TranslateWithCurrentLanguage(this, new RoutedEventArgs());
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
        _ocrCard.RecognitionStateChanged += (_, _) => UpdateProcessingIndicator();
        _ocrCard.IsVisibleChanged += (_, _) =>
        {
            if (_ocrCard.Visibility != Visibility.Visible)
                _ocrCard.CancelRecognition();
        };
        _translationCard.IsVisibleChanged += (_, _) =>
        {
            if (_translationCard.Visibility != Visibility.Visible && !_inPlaceMode)
                CancelTranslation();
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
        UpdateModeButtons();
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
        _modeBar.Visibility = Visibility.Collapsed;
        PositionFloatingPanels();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (_entryMode)
            {
                case CaptureEntryMode.ScrollCapture:
                    StartLongCapture(this, new RoutedEventArgs());
                    break;
                case CaptureEntryMode.Ocr:
                    ShowOcr(this, new RoutedEventArgs());
                    break;
                case CaptureEntryMode.Translation:
                    TranslateWithCurrentLanguage(this, new RoutedEventArgs());
                    break;
                case CaptureEntryMode.Recording:
                    // The setup panel is already shown by
                    // PositionFloatingPanels; recording begins only when the
                    // user clicks 开始录制 (or presses Enter).
                    break;
            }
        }));
    }
}
