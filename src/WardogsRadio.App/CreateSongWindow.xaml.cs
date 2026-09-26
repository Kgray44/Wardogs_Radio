using System.Windows;

namespace WardogsRadio.App;

public partial class CreateSongWindow : Window
{
    public string SongTitle => SongName.Text.Trim();
    public bool NameAfter => AfterChoice.IsChecked == true;

    public CreateSongWindow(string sourceName, string splitTime)
    {
        InitializeComponent();
        PointText.Text = $"Split {sourceName} at {splitTime}";
        SongName.Focus();
    }

    void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SongTitle))
        {
            ErrorText.Text = "Enter a name for the new song.";
            SongName.Focus();
            return;
        }
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
