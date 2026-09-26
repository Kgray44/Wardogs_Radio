using System.Windows;
using System.Windows.Controls;

namespace WardogsRadio.App;

public partial class IconPickerWindow : Window
{
    public string? SelectedIconId { get; private set; }

    public IconPickerWindow(string? current)
    {
        InitializeComponent();
        CategoryBox.ItemsSource = new[] { "All" }.Concat(IconCatalog.All.Select(x => x.Category).Distinct()).ToList();
        CategoryBox.SelectedIndex = 0;
        IconList.ItemsSource = IconCatalog.All;
        IconList.SelectedItem = IconCatalog.Get(current);
    }

    void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    void Category_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    void ApplyFilter()
    {
        if (IconList is null || SearchBox is null || CategoryBox is null) return;
        var search = SearchBox.Text.Trim();
        var category = CategoryBox.SelectedItem as string ?? "All";
        IconList.ItemsSource = IconCatalog.All.Where(x =>
            (category == "All" || x.Category == category) &&
            (search.Length == 0 || x.Name.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
    }
    void Icon_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IconList.SelectedItem is RadioIcon icon) SelectedName.Text = icon.Name;
    }
    void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (IconList.SelectedItem is not RadioIcon icon) return;
        SelectedIconId = icon.Id;
        DialogResult = true;
    }
}
