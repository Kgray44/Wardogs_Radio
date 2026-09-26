using System.Windows;
using WardogsRadio.Update;
using System.IO;

namespace WardogsRadio.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--startup-health-check", StringComparer.OrdinalIgnoreCase))
        {
            var expected = File.Exists(Path.Combine(AppContext.BaseDirectory, "VERSION")) ? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "VERSION")).Trim() : "";
            Shutdown(PackageVerifier.Verify(AppContext.BaseDirectory, expected, out _) ? 0 : 1);
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
