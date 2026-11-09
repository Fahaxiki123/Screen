using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// Lightweight settings view-model. UI controls bind directly to these
/// properties; every change is debounced and persisted through the existing
/// <see cref="JsonSettingsStore"/> without a manual Save button.
/// </summary>
internal sealed class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly JsonSettingsStore _store;
    private readonly CredentialService _credentials;
    private readonly StartupService _startup;
    private readonly DispatcherTimer _saveTimer;
    private readonly AppSettings _settings;

    private bool _suppressSave = true;
    private bool _dirty;
    private bool _credentialsDirty;
    private bool _disposed;

    private string _tencentSecretId;
    private string _tencentSecretKey;
    private string _apiKey;
    private string _racingApiKey;

    public SettingsViewModel(JsonSettingsStore store, CredentialService credentials, StartupService startup)
    {
        _store = store;
        _credentials = credentials;
        _startup = startup;
        _settings = store.Load();

        _tencentSecretId = credentials.Read(TranslationProfile.TencentSecretIdCredentialName) ?? string.Empty;
        _tencentSecretKey = credentials.Read(TranslationProfile.TencentSecretKeyCredentialName) ?? string.Empty;
        _apiKey = credentials.Read(_settings.Translation.CredentialName) ?? string.Empty;
        _racingApiKey = credentials.Read(_settings.Translation.RacingCredentialName) ?? string.Empty;

        _saveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(650)
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };

        _suppressSave = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppSettings Snapshot => _settings;

    public string StatusText { get; private set; } = "设置将自动保存";
    public bool HasError { get; private set; }

    // ---------- General ----------
    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows == value)
            {
                return;
            }
            _settings.StartWithWindows = value;
            OnPropertyChanged();
            QueueSave();
        }
    }

    // ---------- Hot keys ----------
    public string HotKey
    {
        get => _settings.HotKey;
        set
        {
            if (string.Equals(_settings.HotKey, value, StringComparison.Ordinal))
            {
                return;
            }
            _settings.HotKey = value;
            OnPropertyChanged();
            QueueSave();
        }
    }

    public string ClickThroughRecoveryHotKey
    {
        get => _settings.ClickThroughRecoveryHotKey;
        set
        {
            if (string.Equals(_settings.ClickThroughRecoveryHotKey, value, StringComparison.Ordinal))
            {
                return;
            }
            _settings.ClickThroughRecoveryHotKey = value;
            OnPropertyChanged();
            QueueSave();
        }
    }

    public string RecordHotKey
    {
        get => _settings.RecordHotKey;
        set
        {
            if (string.Equals(_settings.RecordHotKey, value, StringComparison.Ordinal))
            {
                return;
            }
            _settings.RecordHotKey = value;
            OnPropertyChanged();
            QueueSave();
        }
    }

    // ---------- Recording ----------
    public int RecordingFrameRateIndex
    {
        get => _settings.Recording.FrameRate switch
        {
            15 => 0,
            60 => 2,
            _ => 1
        };
        set
        {
            var frameRate = value switch
            {
                0 => 15,
                2 => 60,
                _ => 30
            };
            if (_settings.Recording.FrameRate == frameRate)
            {
                return;
            }
            _settings.Recording = _settings.Recording with { FrameRate = frameRate };
            OnPropertyChanged();
            QueueSave();
        }
    }

    public int RecordingQualityIndex
    {
        get => _settings.Recording.Quality switch
        {
            RecordingQuality.Smooth => 0,
            RecordingQuality.High => 2,
            _ => 1
        };
        set
        {
            var quality = value switch
            {
                0 => RecordingQuality.Smooth,
                2 => RecordingQuality.High,
                _ => RecordingQuality.Standard
            };
            if (_settings.Recording.Quality == quality)
            {
                return;
            }
            _settings.Recording = _settings.Recording with { Quality = quality };
            OnPropertyChanged();
            QueueSave();
        }
    }

    public bool RecordingShowCursor
    {
        get => _settings.Recording.ShowCursor;
        set
        {
            if (_settings.Recording.ShowCursor == value)
            {
                return;
            }
            _settings.Recording = _settings.Recording with { ShowCursor = value };
            OnPropertyChanged();
            QueueSave();
        }
    }

    public bool RecordingSystemAudio
    {
        get => _settings.Recording.RecordSystemAudio;
        set
        {
            if (_settings.Recording.RecordSystemAudio == value)
            {
                return;
            }
            _settings.Recording = _settings.Recording with { RecordSystemAudio = value };
            OnPropertyChanged();
            QueueSave();
        }
    }

    public bool RecordingMicrophone
    {
        get => _settings.Recording.RecordMicrophone;
        set
        {
            if (_settings.Recording.RecordMicrophone == value)
            {
                return;
            }
            _settings.Recording = _settings.Recording with { RecordMicrophone = value };
            OnPropertyChanged();
            QueueSave();
        }
    }

    public bool RecordingClickEffects
    {
        get => _settings.Recording.ShowClickEffects;
        set
        {
            if (_settings.Recording.ShowClickEffects == value)
            {
                return;
            }
            _settings.Recording = _settings.Recording with { ShowClickEffects = value };
            OnPropertyChanged();
            QueueSave();
        }
    }

    // ---------- OCR ----------
    public int OcrModeIndex
    {
        get => _settings.Ocr.Mode switch
        {
            OcrProviderMode.CloudFirst => 1,
            OcrProviderMode.LocalOnly => 2,
            _ => 0
        };
        set
        {
            var mode = value switch
            {
                1 => OcrProviderMode.CloudFirst,
                2 => OcrProviderMode.LocalOnly,
                _ => OcrProviderMode.Smart
            };
            if (_settings.Ocr.Mode == mode)
            {
                return;
            }
            _settings.Ocr = _settings.Ocr with { Mode = mode };
            OnPropertyChanged();
            QueueSave();
        }
    }

    public bool UseCloudForTranslation
    {
        get => _settings.Ocr.UseCloudForTranslation;
        set
        {
            if (_settings.Ocr.UseCloudForTranslation == value)
            {
                return;
            }
            _settings.Ocr = _settings.Ocr with { UseCloudForTranslation = value };
            OnPropertyChanged();
            QueueSave();
        }
    }

    // ---------- Translation ----------
    public int TranslationDisplayModeIndex
    {
        get => _settings.TranslationDisplayMode == TranslationDisplayMode.InPlace ? 1 : 0;
        set
        {
            var mode = value == 1 ? TranslationDisplayMode.InPlace : TranslationDisplayMode.Card;
            if (_settings.TranslationDisplayMode == mode)
            {
                return;
            }
            _settings.TranslationDisplayMode = mode;
            OnPropertyChanged();
            QueueSave();
        }
    }

    public int TranslationProviderIndex
    {
        get => _settings.Translation.Provider switch
        {
            TranslationProviderKind.MyMemory => 1,
            TranslationProviderKind.OpenAiCompatible => 2,
            _ => 0
        };
        set
        {
            var provider = value switch
            {
                1 => TranslationProviderKind.MyMemory,
                2 => TranslationProviderKind.OpenAiCompatible,
                _ => TranslationProviderKind.Tencent
            };
            if (_settings.Translation.Provider == provider)
            {
                return;
            }
            _settings.Translation = _settings.Translation with { Provider = provider };
            OnPropertyChanged();
            QueueSave();
        }
    }

    public int TencentRegionIndex
    {
        get
        {
            var regions = TranslationRegions();
            var index = Array.FindIndex(regions, region =>
                string.Equals(region, _settings.Translation.TencentRegion, StringComparison.OrdinalIgnoreCase));
            return index >= 0 ? index : 0;
        }
        set
        {
            var regions = TranslationRegions();
            var region = value >= 0 && value < regions.Length ? regions[value] : regions[0];
            if (string.Equals(_settings.Translation.TencentRegion, region, StringComparison.Ordinal))
            {
                return;
            }
            _settings.Translation = _settings.Translation with { TencentRegion = region };
            OnPropertyChanged();
            QueueSave();
        }
    }

    public bool EnableMyMemoryFallback
    {
        get => _settings.Translation.EnableMyMemoryFallback;
        set
        {
            if (_settings.Translation.EnableMyMemoryFallback == value)
            {
                return;
            }
            _settings.Translation = _settings.Translation with { EnableMyMemoryFallback = value };
            OnPropertyChanged();
            QueueSave();
        }
    }

    public string MyMemoryContactEmail
    {
        get => _settings.Translation.MyMemoryContactEmail;
        set
        {
            if (string.Equals(_settings.Translation.MyMemoryContactEmail, value, StringComparison.Ordinal))
            {
                return;
            }
            _settings.Translation = _settings.Translation with { MyMemoryContactEmail = value };
            OnPropertyChanged();
            QueueSave();
        }
    }

    public string BaseAddress
    {
        get => _settings.Translation.BaseAddress;
        set
        {
            if (string.Equals(_settings.Translation.BaseAddress, value, StringComparison.Ordinal))
            {
                return;
            }
            _settings.Translation = _settings.Translation with { BaseAddress = value };
            OnPropertyChanged();
            QueueSave();
        }
    }

    public string Model
    {
        get => _settings.Translation.Model;
        set
        {
            if (string.Equals(_settings.Translation.Model, value, StringComparison.Ordinal))
            {
                return;
            }
            _settings.Translation = _settings.Translation with { Model = value };
            OnPropertyChanged();
            QueueSave();
        }
    }

    public bool EnableRacing
    {
        get => _settings.Translation.EnableRacing;
        set
        {
            if (_settings.Translation.EnableRacing == value)
            {
                return;
            }
            _settings.Translation = _settings.Translation with { EnableRacing = value };
            OnPropertyChanged();
            QueueSave();
        }
    }

    public string RacingBaseAddress
    {
        get => _settings.Translation.RacingBaseAddress;
        set
        {
            if (string.Equals(_settings.Translation.RacingBaseAddress, value, StringComparison.Ordinal))
            {
                return;
            }
            _settings.Translation = _settings.Translation with { RacingBaseAddress = value };
            OnPropertyChanged();
            QueueSave();
        }
    }

    public string RacingModel
    {
        get => _settings.Translation.RacingModel;
        set
        {
            if (string.Equals(_settings.Translation.RacingModel, value, StringComparison.Ordinal))
            {
                return;
            }
            _settings.Translation = _settings.Translation with { RacingModel = value };
            OnPropertyChanged();
            QueueSave();
        }
    }

    public bool EnableFastFallbackRace
    {
        get => _settings.Translation.EnableFastFallbackRace;
        set
        {
            if (_settings.Translation.EnableFastFallbackRace == value)
            {
                return;
            }
            _settings.Translation = _settings.Translation with { EnableFastFallbackRace = value };
            OnPropertyChanged();
            QueueSave();
        }
    }

    public string FastFallbackDelayText
    {
        get => _settings.Translation.FastFallbackDelaySeconds.ToString();
        set
        {
            var normalized = value.Trim();
            if (string.Equals(FastFallbackDelayText, normalized, StringComparison.Ordinal))
            {
                return;
            }
            _settings.Translation = _settings.Translation with
            {
                FastFallbackDelaySeconds = int.TryParse(normalized, out var seconds) ? seconds : _settings.Translation.FastFallbackDelaySeconds
            };
            OnPropertyChanged();
            QueueSave();
        }
    }

    public int TargetLanguageIndex
    {
        get
        {
            var languages = TranslationLanguages();
            var index = Array.FindIndex(languages, language =>
                string.Equals(language, _settings.Translation.TargetLanguage, StringComparison.Ordinal));
            return index >= 0 ? index : 0;
        }
        set
        {
            var languages = TranslationLanguages();
            var language = value >= 0 && value < languages.Length ? languages[value] : languages[0];
            if (string.Equals(_settings.Translation.TargetLanguage, language, StringComparison.Ordinal))
            {
                return;
            }
            _settings.Translation = _settings.Translation with { TargetLanguage = language };
            OnPropertyChanged();
            QueueSave();
        }
    }

    // ---------- Credentials (stored in Windows Credential Manager) ----------
    public string TencentSecretId
    {
        get => _tencentSecretId;
        set => SetCredential(ref _tencentSecretId, value, nameof(TencentSecretId));
    }

    public string TencentSecretKey
    {
        get => _tencentSecretKey;
        set => SetCredential(ref _tencentSecretKey, value, nameof(TencentSecretKey));
    }

    public string ApiKey
    {
        get => _apiKey;
        set => SetCredential(ref _apiKey, value, nameof(ApiKey));
    }

    public string RacingApiKey
    {
        get => _racingApiKey;
        set => SetCredential(ref _racingApiKey, value, nameof(RacingApiKey));
    }

    public void SetStatus(string text, bool isError)
    {
        HasError = isError;
        StatusText = text;
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(StatusText));
    }

    public TranslationProfile BuildTranslationProfile() => _settings.Translation;

    public TranslationCredentials BuildTranslationCredentials() => new(
        string.IsNullOrWhiteSpace(_apiKey) ? null : _apiKey,
        string.IsNullOrWhiteSpace(_tencentSecretId) ? null : _tencentSecretId,
        string.IsNullOrWhiteSpace(_tencentSecretKey) ? null : _tencentSecretKey,
        string.IsNullOrWhiteSpace(_racingApiKey) ? null : _racingApiKey);

    public void SaveNow()
    {
        if (!_dirty)
        {
            return;
        }

        // Credentials are keyed writes to Credential Manager with no
        // whole-document overwrite risk, so persist them even while a settings
        // validation error is pending — otherwise closing the dialog with an
        // unrelated error (e.g. a hotkey conflict) would silently discard
        // freshly entered API keys.
        try
        {
            PersistCredentials();
            _credentialsDirty = false;
        }
        catch (Exception exception)
        {
            HasError = true;
            StatusText = $"凭据保存失败：{exception.Message}";
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(StatusText));
            if (!_disposed)
            {
                _saveTimer.Start();
            }
        }

        NormalizeBeforeSave();
        var validationError = Validate();
        if (validationError is not null)
        {
            HasError = true;
            StatusText = validationError;
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(StatusText));
            // Keep the change pending and retry later; otherwise valid edits queued
            // behind the invalid one would never be persisted.
            if (!_disposed)
            {
                _saveTimer.Start();
            }
            return;
        }

        try
        {
            // Merge into the latest on-disk settings instead of overwriting the whole
            // document: runtime writers may have updated SaveDirectory or the
            // click-through hint flag while the dialog was open.
            _store.Update(latest =>
            {
                latest.StartWithWindows = _settings.StartWithWindows;
                latest.HotKey = _settings.HotKey;
                latest.ClickThroughRecoveryHotKey = _settings.ClickThroughRecoveryHotKey;
                latest.RecordHotKey = _settings.RecordHotKey;
                latest.Ocr = _settings.Ocr;
                latest.Translation = _settings.Translation;
                latest.TranslationDisplayMode = _settings.TranslationDisplayMode;
                latest.Recording = _settings.Recording;
            });
            _startup.SetEnabled(_settings.StartWithWindows);

            _dirty = false;
            _credentialsDirty = false;
            HasError = false;
            StatusText = $"已自动保存 · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception exception)
        {
            HasError = true;
            StatusText = $"自动保存失败：{exception.Message}";
            if (!_disposed)
            {
                _saveTimer.Start();
            }
        }

        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(StatusText));
    }

    public void Dispose()
    {
        _disposed = true;
        _saveTimer.Stop();
        SaveNow();
    }

    public static string[] TranslationRegions() =>
        ["ap-guangzhou", "ap-shanghai", "ap-beijing", "ap-chengdu", "ap-hongkong"];

    public static string[] TranslationLanguages() =>
        ["简体中文", "繁體中文", "English", "日本語", "한국어", "Français", "Español", "Português", "Deutsch"];

    private void SetCredential(ref string field, string value, string propertyName)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }
        field = value;
        _credentialsDirty = true;
        OnPropertyChanged(propertyName);
        QueueSave();
    }

    private void PersistCredentials()
    {
        if (!_credentialsDirty)
        {
            return;
        }

        WriteOrDelete(TranslationProfile.TencentSecretIdCredentialName, _tencentSecretId);
        WriteOrDelete(TranslationProfile.TencentSecretKeyCredentialName, _tencentSecretKey);
        WriteOrDelete(_settings.Translation.CredentialName, _apiKey);
        WriteOrDelete(_settings.Translation.RacingCredentialName, _racingApiKey);
    }

    private void WriteOrDelete(string targetName, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            _credentials.Delete(targetName);
        }
        else
        {
            _credentials.Write(targetName, secret);
        }
    }

    private void NormalizeBeforeSave()
    {
        static string TrimUrl(string? value) => (value ?? string.Empty).Trim().TrimEnd('/');

        _settings.Translation = _settings.Translation with
        {
            BaseAddress = TrimUrl(_settings.Translation.BaseAddress),
            Model = (_settings.Translation.Model ?? string.Empty).Trim(),
            MyMemoryContactEmail = (_settings.Translation.MyMemoryContactEmail ?? string.Empty).Trim(),
            RacingBaseAddress = TrimUrl(_settings.Translation.RacingBaseAddress),
            RacingModel = (_settings.Translation.RacingModel ?? string.Empty).Trim()
        };
    }

    private string? Validate()
    {
        if (!HotKeyWindow.TryParseHotKey(_settings.HotKey, out _, out _))
        {
            return "截图快捷键格式无效。";
        }
        if (!HotKeyWindow.TryParseHotKey(
                _settings.ClickThroughRecoveryHotKey,
                out _,
                out _,
                allowUnmodifiedFunctionKey: true))
        {
            return "恢复贴图穿透快捷键格式无效。";
        }
        if (string.Equals(_settings.HotKey, _settings.ClickThroughRecoveryHotKey, StringComparison.OrdinalIgnoreCase))
        {
            return "两个快捷键不能相同。";
        }
        if (!HotKeyWindow.TryParseHotKey(_settings.RecordHotKey, out _, out _))
        {
            return "录屏快捷键格式无效。";
        }
        if (string.Equals(_settings.HotKey, _settings.RecordHotKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_settings.ClickThroughRecoveryHotKey, _settings.RecordHotKey, StringComparison.OrdinalIgnoreCase))
        {
            return "录屏快捷键不能与其他快捷键相同。";
        }
        if (_settings.Translation.Provider == TranslationProviderKind.OpenAiCompatible
            && TranslationEndpointRules.ValidateBaseAddress(_settings.Translation.BaseAddress) is { } baseAddressError)
        {
            return baseAddressError;
        }
        // The racing endpoint carries the same Bearer credential; empty means
        // the race is dormant, which is fine.
        if (_settings.Translation.Provider == TranslationProviderKind.OpenAiCompatible
            && !string.IsNullOrWhiteSpace(_settings.Translation.RacingBaseAddress)
            && TranslationEndpointRules.ValidateBaseAddress(_settings.Translation.RacingBaseAddress) is { } racingAddressError)
        {
            return racingAddressError;
        }
        if (_settings.Translation.Provider == TranslationProviderKind.OpenAiCompatible
            && string.IsNullOrWhiteSpace(_settings.Translation.Model))
        {
            return "AI 模型不能为空。";
        }
        if (_settings.Translation.EnableFastFallbackRace
            && (_settings.Translation.FastFallbackDelaySeconds < 2 || _settings.Translation.FastFallbackDelaySeconds > 30))
        {
            return "快速翻译等待秒数需要在 2 到 30 之间。";
        }
        return null;
    }

    private void QueueSave()
    {
        if (_suppressSave)
        {
            return;
        }
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
