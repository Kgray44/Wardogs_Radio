using System.Windows;
using System.Windows.Input;

namespace WardogsRadio.App;

public partial class RadioDialogWindow : Window
{
    int? _threeChoice;
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

    public static bool Choose(Window owner, string heading, string message, string acceptLabel, string otherLabel)
    {
        var dialog = new RadioDialogWindow(heading, message, true, acceptLabel) { Owner = owner };
        dialog.CancelButton.Content = otherLabel;
        return dialog.ShowDialog() == true;
    }

    public static int? ChooseThree(Window owner, string heading, string message,
        string firstLabel, string secondLabel, string thirdLabel)
    {
        var dialog = new RadioDialogWindow(heading, message, true, firstLabel) { Owner = owner };
        dialog.CancelButton.Content = secondLabel;
        dialog.CancelButton.IsCancel = false;
        dialog.ThirdButton.Content = thirdLabel;
        dialog.ThirdButton.Visibility = Visibility.Visible;
        dialog.ShowDialog();
        return dialog._threeChoice;
    }

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

    void Accept_Click(object sender, RoutedEventArgs e) { _threeChoice = 0; DialogResult = true; }
    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (ThirdButton.Visibility == Visibility.Visible) { _threeChoice = 1; DialogResult = true; }
        else DialogResult = false;
    }
    void Third_Click(object sender, RoutedEventArgs e) { _threeChoice = 2; DialogResult = true; }
}
