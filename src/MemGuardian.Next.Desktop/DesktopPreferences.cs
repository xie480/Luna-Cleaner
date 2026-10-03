using System.Text.Json;
using System.IO;

namespace MemGuardian.Next.Desktop;

/// <summary>UI-only switches are kept separately from the shared diagnosis and reclaim thresholds.</summary>
public sealed record DesktopPreferences
{
    public bool MonitorOnLaunch { get; init; } = true;
    public bool AutomaticReclaim { get; init; }
    public bool RunInBackground { get; init; }
    public bool StartMinimized { get; init; }
}

/// <summary>Small, user-local settings file with bounded and atomic reads/writes.</summary>
public sealed class DesktopPreferencesStore
{
    private const int MaximumBytes = 16 * 1024;
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, MaxDepth = 8 };

    public DesktopPreferencesStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        Directory.CreateDirectory(directoryPath);
        _path = Path.Combine(directoryPath, "desktop.json");
    }

    public DesktopPreferences Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > MaximumBytes) return new DesktopPreferences();
            return JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(_path), _json) ?? new DesktopPreferences();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DesktopPreferences();
        }
    }

    public void Save(DesktopPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var temporary = _path + ".tmp";
        var content = JsonSerializer.SerializeToUtf8Bytes(preferences, _json);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, _path, overwrite: true);
    }
}

/// <summary>Builds the current-user logon command without relying on the caller's working directory.</summary>
public static class StartupCommandBuilder
{
    public static string Build(string executablePath, string? entryAssemblyPath = null, bool startMinimized = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var isDotnetHost = Path.GetFileNameWithoutExtension(executablePath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var command = Quote(executablePath);
        if (isDotnetHost && !string.IsNullOrWhiteSpace(entryAssemblyPath)) command += " " + Quote(entryAssemblyPath);
        if (startMinimized) command += " --background";
        return command;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
