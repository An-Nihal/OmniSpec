namespace OmniSpec.Infrastructure;

/// <summary>
/// Locations used by OmniSpec. By default data lives in %LOCALAPPDATA%\OmniSpec. If a file named
/// "portable.txt" exists next to the executable, everything is stored in a "data" folder beside it instead.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? rootOverride = null)
    {
        var baseDir = AppContext.BaseDirectory;
        IsPortable = rootOverride is null && File.Exists(Path.Combine(baseDir, "portable.txt"));
        Root = rootOverride
               ?? (IsPortable
                   ? Path.Combine(baseDir, "data")
                   : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmniSpec"));
    }

    public bool IsPortable { get; }
    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string LogDirectory => Path.Combine(Root, "logs");
    public string HistoryDirectory => Path.Combine(Root, "history");
    public string ExportDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OmniSpec");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
    }
}
