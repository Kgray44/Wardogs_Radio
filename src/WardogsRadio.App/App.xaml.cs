using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WardogsRadio.Update;
using System.IO;

namespace WardogsRadio.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // WPF's default wheel command jumps several logical rows. Handle it once at
        // the nearest scroll surface so lists, pages, popups, and menus all move in
        // a deliberate, readable increment instead.
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(SlowScrollWheel), true);
        if (e.Args.Contains("--youtube-search-key-check", StringComparer.OrdinalIgnoreCase))
        {
            var key = BundledYouTubeSearchKey.Read();
            Shutdown(key is { Length: >= 20 } && !key.Any(char.IsWhiteSpace) ? 0 : 1);
            return;
        }
        if (e.Args.Contains("--startup-health-check", StringComparer.OrdinalIgnoreCase))
        {
            var expected = File.Exists(Path.Combine(AppContext.BaseDirectory, "VERSION")) ? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "VERSION")).Trim() : "";
            Shutdown(PackageVerifier.Verify(AppContext.BaseDirectory, expected, out _) ? 0 : 1);
            return;
        }
        var packagePath = e.Args.FirstOrDefault(arg => arg.EndsWith(".wradio", StringComparison.OrdinalIgnoreCase) && File.Exists(arg));
        MainWindow = new MainWindow(packagePath);
        MainWindow.Show();
    }

    static void SlowScrollWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject origin) return;
        var scroll = FindScrollViewer(origin);
        if (scroll is null || scroll.ScrollableHeight <= 0 || e.Delta == 0) return;
        var direction = Math.Sign(e.Delta);
        var increment = scroll.CanContentScroll ? .35d : 12d;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset - direction * increment);
        e.Handled = true;
    }

    static ScrollViewer? FindScrollViewer(DependencyObject origin)
    {
        for (DependencyObject? node = origin; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer scroll) return scroll;
        return null;
    }
}
