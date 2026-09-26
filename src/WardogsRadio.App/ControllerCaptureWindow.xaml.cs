using System.Windows;

namespace WardogsRadio.App;

public partial class ControllerCaptureWindow : Window
{
    public ControllerCaptureWindow() => InitializeComponent();
    void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
