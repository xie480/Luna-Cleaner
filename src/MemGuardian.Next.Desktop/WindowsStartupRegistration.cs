using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace MemGuardian.Next.Desktop;

public interface IStartupRegistration
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled, bool startMinimized);
}

/// <summary>Uses only the current user's documented Run key; no elevation or scheduled task is created.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupRegistration : IStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MemGuardian.Next";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
        }
    }

    public void SetEnabled(bool enabled, bool startMinimized)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的开机启动注册表项。");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法解析当前应用程序路径。");
        var entryAssembly = Assembly.GetEntryAssembly()?.Location;
        var command = StartupCommandBuilder.Build(executable, entryAssembly, startMinimized);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
