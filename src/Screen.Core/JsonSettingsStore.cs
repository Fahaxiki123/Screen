using System.Text.Json;

namespace Screen.Core;

public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    // Load/Save/Update may be called from different threads (settings dialog,
    // OCR engine, hotkey registration); a crashed save must never interleave
    // with another one on the same .tmp file.
    private readonly object _gate = new();

    public JsonSettingsStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Screen",
            "settings.json");
        DeleteStaleTemporaryFile();
    }

    private void DeleteStaleTemporaryFile()
    {
        try
        {
            File.Delete(Path + ".tmp");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public string Path { get; }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options)
                           ?? new AppSettings();
            settings.Ocr ??= new OcrProfile();
            settings.Translation ??= new AppSettings().Translation;
            settings.Recording ??= new RecordingProfile();
            if (!Enum.IsDefined(settings.TranslationDisplayMode))
            {
                settings.TranslationDisplayMode = TranslationDisplayMode.Card;
            }
            if (settings.SchemaVersion < 3
                && IsLegacyZhipuOcrProfile(settings.Translation))
            {
                settings.Translation = settings.Translation with
                {
                    Provider = TranslationProviderKind.OpenAiCompatible,
                    BaseAddress = "https://open.bigmodel.cn/api/paas/v4",
                    Model = "glm-4.7-flash"
                };
            }
            if (settings.SchemaVersion < AppSettings.CurrentSchemaVersion)
            {
                settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            }
            return settings;
        }
        catch (JsonException)
        {
            PreserveCorruptFile();
            return new AppSettings();
        }
        // IOException/UnauthorizedAccessException (transient antivirus or
        // indexer locks, ACL changes) deliberately propagate instead of
        // returning defaults: Update() is a read-modify-write over Load(), so
        // defaults returned here would be persisted and silently wipe the
        // user's settings. Corruption is deterministic and keeps the defaults
        // path; a locked file is not.
    }

    private static bool IsLegacyZhipuOcrProfile(TranslationProfile profile) =>
        profile.BaseAddress.Contains("open.bigmodel.cn", StringComparison.OrdinalIgnoreCase)
        && profile.BaseAddress.Contains("layout_parsing", StringComparison.OrdinalIgnoreCase)
        && profile.Model.Equals("glm-ocr", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reloads the latest on-disk settings, applies <paramref name="mutate"/>, and
    /// persists the result atomically. Use this instead of Save when the caller's
    /// snapshot may be stale — other components (runtime SaveDirectory updates,
    /// hotkey fallback writes) may have persisted changes since it was loaded.
    /// </summary>
    public void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            var latest = Load();
            mutate(latest);
            Save(latest);
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            var directory = System.IO.Path.GetDirectoryName(Path)
                            ?? throw new InvalidOperationException("Settings path has no directory.");
            Directory.CreateDirectory(directory);
            var temporary = Path + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
                File.Move(temporary, Path, true);
            }
            catch
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                throw;
            }
        }
    }

    private void PreserveCorruptFile()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return;
            }
            var backup = Path + $".corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}";
            File.Move(Path, backup, false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
