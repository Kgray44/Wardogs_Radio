using System.Windows;
using System.Windows.Input;
using WardogsRadio.Core;

namespace WardogsRadio.App;

public partial class BackupTransferWindow : Window
{
    sealed record PackageChoice(Guid Id, string Name) { public override string ToString() => Name; }
    readonly bool _export;
    public WrRadioExportSelection? ExportSelection { get; private set; }
    public WrRadioImportOptions? ImportOptions { get; private set; }

    public BackupTransferWindow(bool export, WrRadioPackageSummary? summary = null,
        IEnumerable<Station>? stations = null, IEnumerable<LibrarySong>? songs = null, IEnumerable<RadioMacro>? macros = null)
    {
        InitializeComponent();
        _export = export;
        SetChoices(StationItems, (stations ?? []).Select(station => new PackageChoice(station.Id, station.Name)));
        SetChoices(SongItems, (songs ?? []).Select(song => new PackageChoice(song.Id, song.Name)));
        SetChoices(MacroItems, (macros ?? []).Select(macro => new PackageChoice(macro.Id, macro.Name)));
        if (export)
        {
            Heading.Text = "EXPORT CONFIGURATION";
            Description.Text = "Choose portable configuration to save or share. Station dependencies are included automatically so a package cannot contain broken song or source references.";
            PackageName.Text = "WARDOGS Radio Export";
            ContinueButton.Content = "CONTINUE";
        }
        else
        {
            Heading.Text = "IMPORT WARDOGS RADIO PACKAGE";
            Description.Text = "Review the package before anything changes. Import adds safe copies by default; it never replaces your active configuration.";
            ExportDetails.Visibility = Visibility.Collapsed;
            PackageSummary.Visibility = Visibility.Visible;
            SummaryText.Text = summary is null ? "Package summary unavailable." : Describe(summary);
            LocalMedia.Visibility = Visibility.Collapsed;
            LocalMediaHint.Visibility = Visibility.Collapsed;
            ConflictOptions.Visibility = Visibility.Visible;
            ContinueButton.Content = "IMPORT";
        }
    }

    void Continue_Click(object sender, RoutedEventArgs e)
    {
        var contents = SelectedContents();
        if (contents == WrRadioContent.None && !(_export && FullBackup.IsChecked == true))
        {
            RadioDialogWindow.Inform(this, "Choose configuration", "Select at least one category to continue.");
            return;
        }
        if (_export)
        {
            var isFullBackup = FullBackup.IsChecked == true;
            ExportSelection = new WrRadioExportSelection
            {
            Contents = isFullBackup ? WrRadioContent.All : contents,
            IncludeLocalMedia = LocalMedia.IsChecked == true,
            PackageName = PackageName.Text, Description = PackageDescription.Text,
            PackageType = isFullBackup ? WrRadioPackageType.FullBackup : WrRadioPackageType.Selection,
            StationIds = isFullBackup ? [] : SelectedIds(StationItems), SongIds = isFullBackup ? [] : SelectedIds(SongItems), MacroIds = isFullBackup ? [] : SelectedIds(MacroItems)
            };
        }
        else ImportOptions = new WrRadioImportOptions
        {
            Contents = contents,
            StationIds = SelectedIds(StationItems), SongIds = SelectedIds(SongItems), MacroIds = SelectedIds(MacroItems),
            LibraryConflictResolution = LibraryConflict.SelectedIndex switch
            {
                1 => LibraryConflictResolution.KeepExisting,
                2 => LibraryConflictResolution.ReplaceExisting,
                _ => LibraryConflictResolution.ImportAsCopy
            },
            StationConflictResolution = StationConflict.SelectedIndex switch
            {
                1 => StationConflictResolution.KeepExisting,
                2 => StationConflictResolution.ReplaceExisting,
                3 => StationConflictResolution.Skip,
                _ => StationConflictResolution.KeepBoth
            }
        };
        DialogResult = true;
    }

    static void SetChoices(System.Windows.Controls.ListBox list, IEnumerable<PackageChoice> choices)
    {
        var items = choices.OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        list.ItemsSource = items;
        foreach (var item in items) list.SelectedItems.Add(item);
    }

    static List<Guid> SelectedIds(System.Windows.Controls.ListBox list) => list.SelectedItems.Cast<PackageChoice>().Select(item => item.Id).ToList();

    WrRadioContent SelectedContents()
    {
        var contents = WrRadioContent.None;
        if (Stations.IsChecked == true) contents |= WrRadioContent.Stations;
        if (Library.IsChecked == true) contents |= WrRadioContent.LibrarySongs | WrRadioContent.MediaSources;
        if (Macros.IsChecked == true) contents |= WrRadioContent.Macros;
        if (Keyboard.IsChecked == true) contents |= WrRadioContent.KeyboardBindings;
        if (Controller.IsChecked == true) contents |= WrRadioContent.ControllerBindings;
        if (Playback.IsChecked == true) contents |= WrRadioContent.PlaybackSettings;
        if (Audio.IsChecked == true) contents |= WrRadioContent.AudioRoutingSettings;
        if (General.IsChecked == true) contents |= WrRadioContent.GeneralSettings;
        return contents;
    }

    static string Describe(WrRadioPackageSummary summary) =>
        $"{summary.PackageName}\n{summary.Stations} station(s) · {summary.Songs} song(s) · {summary.Sources} source(s) · {summary.Macros} macro(s)" +
        (summary.MediaFiles == 0 ? "" : $"\n{summary.MediaFiles} embedded media file(s) · {summary.EmbeddedMediaBytes / 1024d / 1024d:0.0} MB");

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
}
