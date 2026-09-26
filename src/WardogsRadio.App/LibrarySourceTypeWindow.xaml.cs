using System.Windows;

namespace WardogsRadio.App;

public partial class LibrarySourceTypeWindow : Window
{
    public string? ProviderId { get; private set; }

    public LibrarySourceTypeWindow() => InitializeComponent();

    void Provider_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string providerId) return;
        ProviderId = providerId;
        DialogResult = true;
    }
}
