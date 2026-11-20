using Screen.Core;

namespace Screen.App;

internal sealed partial class SettingsViewModel
{
    private static readonly int[] _gifFrameRates = [10, 15, 20, 30];
    private static readonly int[] _gifWidths = [0, 1920, 1280, 960];

    public string SaveDirectory
    {
        get => _settings.SaveDirectory;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || _settings.SaveDirectory == value)
                return;
            _settings.SaveDirectory = value;
            OnPropertyChanged();
            QueueSave();
        }
    }

    public int RecordingFormatIndex
    {
        get => _settings.Recording.Format == RecordingFormat.Gif ? 1 : 0;
        set
        {
            if (value is not (0 or 1) || value == RecordingFormatIndex)
                return;
            _settings.Recording = _settings.Recording with { Format = value == 1 ? RecordingFormat.Gif : RecordingFormat.Mp4 };
            OnPropertyChanged();
            OnPropertyChanged(nameof(RecordingSummary));
            QueueSave();
        }
    }

    // Custom is a projection, not a preset: binding initialization cannot normalize old settings.
    public int RecordingPresetIndex
    {
        get => (_settings.Recording.FrameRate, _settings.Recording.Quality) switch
        {
            (30, RecordingQuality.Standard) => 0,
            (60, RecordingQuality.High) => 1,
            _ => 2
        };
        set
        {
            if (value is not (0 or 1) || value == RecordingPresetIndex)
                return;
            _settings.Recording = _settings.Recording with
            {
                FrameRate = value == 0 ? 30 : 60,
                Quality = value == 0 ? RecordingQuality.Standard : RecordingQuality.High
            };
            QueueSave(nameof(RecordingFrameRateIndex));
            QueueSave(nameof(RecordingQualityIndex));
            OnPropertyChanged(nameof(RecordingFrameRateIndex));
            OnPropertyChanged(nameof(RecordingQualityIndex));
            NotifyRecordingPreset();
        }
    }

    private void NotifyRecordingPreset()
    {
        OnPropertyChanged(nameof(RecordingPresetIndex));
        OnPropertyChanged(nameof(RecordingSummary));
    }

    public string RecordingSummary => _settings.Recording.Format == RecordingFormat.Gif
        ? "GIF 适合短小的动图演示，不含声音。"
        : $"{_settings.Recording.FrameRate} 帧 · {_settings.Recording.VideoBitrate / 1_000_000} Mbps" + (RecordingPresetIndex switch
        {
            0 => "，适合教程和日常分享。",
            1 => "，适合演示动画和游戏。",
            _ => "，使用自定义画质。"
        });

    public int RecordingGifFrameRateIndex
    {
        get => Array.IndexOf(_gifFrameRates, _settings.Recording.GifFrameRate);
        set
        {
            if ((uint)value >= _gifFrameRates.Length || value == RecordingGifFrameRateIndex)
                return;
            _settings.Recording = _settings.Recording with { GifFrameRate = _gifFrameRates[value] };
            OnPropertyChanged();
            QueueSave();
        }
    }

    public int RecordingGifMaxWidthIndex
    {
        get => Array.IndexOf(_gifWidths, _settings.Recording.GifMaxWidth);
        set
        {
            if ((uint)value >= _gifWidths.Length || value == RecordingGifMaxWidthIndex)
                return;
            _settings.Recording = _settings.Recording with { GifMaxWidth = _gifWidths[value] };
            OnPropertyChanged();
            QueueSave();
        }
    }

    // Display order differs from the original Tencent / MyMemory / AI index.
    public int TranslationServiceIndex
    {
        get => TranslationProviderIndex switch { 2 => 0, 0 => 1, _ => 2 };
        set
        {
            if (value is >= 0 and <= 2)
                TranslationProviderIndex = value switch { 0 => 2, 1 => 0, _ => 1 };
        }
    }
}
