namespace MemGuardian.Next.Cli;

/// <summary>CLI entry point with graceful Ctrl+C cancellation and dependency-free parsing.</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("MemGuardian.Next 需要 Windows 10/11 x64。");
            return 3;
        }

        if (!CliRequestParser.TryParse(args, out var request, out var error))
        {
            Console.Error.WriteLine(error);
            ConsolePresenter.PrintHelp();
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            return await new GuardianApplication().ExecuteAsync(request, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"命令失败：{exception.Message}");
            return 5;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
