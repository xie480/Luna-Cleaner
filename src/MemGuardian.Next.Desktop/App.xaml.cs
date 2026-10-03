using System.Windows;

namespace MemGuardian.Next.Desktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow(e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase));
        MainWindow = window;
        window.Show();
    }
}
