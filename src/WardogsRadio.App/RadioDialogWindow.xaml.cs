using System.Windows;
using System.Windows.Input;

namespace WardogsRadio.App;

public partial class RadioDialogWindow : Window
{
    RadioDialogWindow(string heading, string message, bool requiresChoice, string acceptLabel)
    {
        InitializeComponent();
        DialogHeading.Text = heading;
        DialogMessage.Text = message;
        CancelButton.Visibility = requiresChoice ? Visibility.Visible : Visibility.Collapsed;
        AcceptButton.Content = acceptLabel;
    }

    public static bool Confirm(Window owner, string heading, string message, string acceptLabel = "CONFIRM") =>
        new RadioDialogWindow(heading, message, true, acceptLabel) { Owner = owner }.ShowDialog() == true;

    public static void Inform(Window? owner, string heading, string message)
    {
        var dialog = new RadioDialogWindow(heading, message, false, "OK");
        if (owner is not null) dialog.Owner = owner;
        dialog.ShowDialog();
    }

    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
