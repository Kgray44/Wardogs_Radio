using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WardogsRadio.App;

public partial class AccentColorPickerWindow : Window
{
    static readonly (string Name, string Hex)[] Palette =
    [
        ("Olive", "#9FB672"), ("Moss", "#83AA71"), ("Sage", "#A9BE9A"),
        ("Amber", "#E6B65A"), ("Sand", "#D8BE86"), ("Copper", "#C99062"),
        ("Muted red", "#D68A65"), ("Coral", "#D98E86"), ("Rose", "#C989A2"),
        ("Blue", "#639BBC"), ("Sky", "#88B7CF"), ("Steel", "#899FB3"),
        ("Teal", "#5EA69A"), ("Sea", "#83B8A8"), ("Mint", "#A1C3A2"),
        ("Violet", "#9582B5"), ("Lavender", "#B09CC7"), ("Slate", "#A3A8BA")
    ];

    readonly List<Button> _swatches = [];
    public string? SelectedColor { get; private set; }

    public AccentColorPickerWindow(string currentColor)
    {
        InitializeComponent();
        foreach (var (name, hex) in Palette)
        {
            var swatch = new Border
            {
                Width = 52, Height = 38, CornerRadius = new CornerRadius(3),
                Background = Brush(hex), Margin = new Thickness(0, 0, 0, 5)
            };
            var label = new TextBlock { Text = name, FontSize = 11, TextAlignment = TextAlignment.Center };
            var tile = new Button
            {
                Tag = hex, Width = 112, MinHeight = 76, Margin = new Thickness(0, 0, 9, 9),
                Padding = new Thickness(7), Content = new StackPanel { Children = { swatch, label } },
                ToolTip = $"{name} {hex}"
            };
            tile.Click += (_, _) => HexBox.Text = hex;
            _swatches.Add(tile);
            PalettePanel.Children.Add(tile);
        }
        HexBox.Text = NormalizeColor(currentColor);
    }

    static bool IsHexColor(string? value) => value is not null && Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$");
    public static string NormalizeColor(string? value) => IsHexColor(value) ? value!.ToUpperInvariant() : "#9FB672";
    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex)!);

    void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PreviewSwatch is null || ApplyButton is null) return;
        var hex = HexBox.Text.Trim().ToUpperInvariant();
        var valid = IsHexColor(hex);
        ApplyButton.IsEnabled = valid;
        ValidationText.Text = valid ? "" : "Enter six hexadecimal digits after #, for example #9FB672.";
        if (valid)
        {
            PreviewSwatch.Background = Brush(hex);
            PreviewHex.Text = hex;
            PreviewHex.Foreground = Brush(hex);
        }
        foreach (var tile in _swatches)
        {
            var chosen = valid && string.Equals(tile.Tag?.ToString(), hex, StringComparison.OrdinalIgnoreCase);
            tile.BorderBrush = chosen ? (Brush)FindResource("OliveBrush") : (Brush)FindResource("LineBrush");
            tile.BorderThickness = chosen ? new Thickness(2) : new Thickness(1);
        }
    }

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        var hex = HexBox.Text.Trim().ToUpperInvariant();
        if (!IsHexColor(hex)) return;
        SelectedColor = hex;
        DialogResult = true;
    }
}
