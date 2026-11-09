using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OmniSpec.Core.Settings;

namespace OmniSpec.Infrastructure.Settings;

/// <summary>
/// Persists <see cref="AppSettings"/> as indented JSON. Writes are atomic (temp file + replace).
/// A corrupt file is preserved as settings.corrupt-*.json and defaults are used instead.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<JsonSettingsStore>? _logger;

    public JsonSettingsStore(string settingsPath, ILogger<JsonSettingsStore>? logger = null)
    {
        SettingsPath = settingsPath;
        _logger = logger;
    }

    public string SettingsPath { get; }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings().Normalize();
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            return settings.Normalize();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _logger?.LogWarning(ex, "Settings file is corrupt; using defaults");
            try
            {
                File.Copy(SettingsPath, Path.ChangeExtension(SettingsPath, $".corrupt-{DateTime.Now:yyyyMMddHHmmss}.json"), overwrite: true);
            }
            catch { /* best effort */ }
            return new AppSettings().Normalize();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not read settings; using defaults");
            return new AppSettings().Normalize();
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, SettingsPath, overwrite: true);
    }
}

public sealed class SettingsService : ISettingsService
{
    private readonly ISettingsStore _store;
    private readonly ILogger<SettingsService>? _logger;
    private readonly object _gate = new();

    public SettingsService(ISettingsStore store, ILogger<SettingsService>? logger = null)
    {
        _store = store;
        _logger = logger;
        Current = store.Load();
    }

    public AppSettings Current { get; }
    public string SettingsPath => _store.SettingsPath;
    public event EventHandler? Changed;

    public void Save()
    {
        lock (_gate)
        {
            Current.Normalize();
            try { _store.Save(Current); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Saving settings failed"); }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
