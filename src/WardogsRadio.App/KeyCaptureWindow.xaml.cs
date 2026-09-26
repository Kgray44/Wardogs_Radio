using System.Windows;
using System.Windows.Input;

namespace WardogsRadio.App;

public partial class KeyCaptureWindow : Window
{
    public string? CapturedKey { get; private set; }
    public KeyCaptureWindow() => InitializeComponent();
    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; return; }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Tab) return;
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Windows) != 0) return;
        CapturedKey = string.Join("+", new[]
        {
            (modifiers & ModifierKeys.Control) != 0 ? "Ctrl" : null,
            (modifiers & ModifierKeys.Alt) != 0 ? "Alt" : null,
            (modifiers & ModifierKeys.Shift) != 0 ? "Shift" : null,
            key.ToString()
        }.Where(x => x is not null));
        e.Handled = true;
        DialogResult = true;
    }
}
