using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WardogsRadio.Core;

namespace WardogsRadio.App;

public partial class ActionPickerWindow : Window
{
    readonly ICollectionView _view;
    public MacroActionDefinition? SelectedAction { get; private set; }

    public ActionPickerWindow()
    {
        InitializeComponent();
        ActionList.ItemsSource = MacroActionCatalog.All;
        _view = CollectionViewSource.GetDefaultView(ActionList.ItemsSource);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MacroActionDefinition.Category)));
        _view.Filter = x => x is MacroActionDefinition action && (string.IsNullOrWhiteSpace(SearchBox.Text) ||
            action.Name.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase) ||
            action.Category.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase));
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();
    void ActionList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Choose();
    void Add_Click(object sender, RoutedEventArgs e) => Choose();
    void Choose()
    {
        if (ActionList.SelectedItem is not MacroActionDefinition action) return;
        SelectedAction = action;
        DialogResult = true;
    }
}
