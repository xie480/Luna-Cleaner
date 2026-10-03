using System.Globalization;

namespace MemGuardian.Next.Cli;

/// <summary>Supported command names and top sort keys.</summary>
internal static class CliNames
{
    internal const string Status = "status";
    internal const string Top = "top";
    internal const string Diagnose = "diagnose";
    internal const string Run = "run";
    internal const string Once = "once";
    internal const string DryRun = "--dry-run";
    internal const string By = "--by";
    internal const string Duration = "--duration";
    internal const string WorkingSet = "working-set";
    internal const string Commit = "commit";
}

internal enum CliCommand
{
    Help,
    Status,
    Top,
    Diagnose,
    Run,
    Once
}

internal enum TopSort
{
    WorkingSet,
    Commit
}

/// <summary>Parsed CLI request with validated command-specific options.</summary>
internal sealed record CliRequest(CliCommand Command, TopSort TopSort = TopSort.WorkingSet,
    int DurationSeconds = 60, bool DryRun = false);

/// <summary>Small dependency-free parser for the fixed first-release command tree.</summary>
internal static class CliRequestParser
{
    internal static bool TryParse(string[] args, out CliRequest request, out string? error)
    {
        request = new CliRequest(CliCommand.Help);
        error = null;
        if (args.Length == 0 || args is ["--help"] or ["-h"] or ["help"])
            return true;

        switch (args[0])
        {
            case CliNames.Status when args.Length == 1:
                request = new CliRequest(CliCommand.Status);
                return true;
            case CliNames.Top:
                return ParseTop(args, out request, out error);
            case CliNames.Diagnose:
                return ParseDiagnose(args, out request, out error);
            case CliNames.Run:
                if (args.Length == 1)
                {
                    request = new CliRequest(CliCommand.Run);
                    return true;
                }
                if (args.Length == 2 && args[1] == CliNames.DryRun)
                {
                    request = new CliRequest(CliCommand.Run, DryRun: true);
                    return true;
                }
                error = "run 只支持可选参数 --dry-run。";
                return false;
            case CliNames.Once when args.Length == 1:
                request = new CliRequest(CliCommand.Once);
                return true;
            default:
                error = $"未知命令或参数：{string.Join(' ', args)}";
                return false;
        }
    }

    private static bool ParseTop(string[] args, out CliRequest request, out string? error)
    {
        request = new CliRequest(CliCommand.Top);
        error = null;
        if (args.Length != 3 || args[1] != CliNames.By || args[2] is not (CliNames.WorkingSet or CliNames.Commit))
        {
            error = "用法：memguardian top --by working-set|commit";
            return false;
        }
        request = new CliRequest(CliCommand.Top,
            args[2] == CliNames.Commit ? TopSort.Commit : TopSort.WorkingSet);
        return true;
    }

    private static bool ParseDiagnose(string[] args, out CliRequest request, out string? error)
    {
        request = new CliRequest(CliCommand.Diagnose);
        error = null;
        if (args.Length == 1) return true;
        if (args.Length != 3 || args[1] != CliNames.Duration ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var duration) ||
            duration is < 1 or > 3600)
        {
            error = "用法：memguardian diagnose [--duration 1..3600]";
            return false;
        }
        request = new CliRequest(CliCommand.Diagnose, DurationSeconds: duration);
        return true;
    }
}
