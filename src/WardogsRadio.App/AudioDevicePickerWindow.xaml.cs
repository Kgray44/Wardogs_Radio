using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WardogsRadio.Playback;

namespace WardogsRadio.App;

public partial class AudioDevicePickerWindow : Window
{
    sealed record DeviceCard(WindowsAudioEndpoint Endpoint, bool Current)
    {
        public string Name => Endpoint.Name;
        public string Category => Endpoint.Category;
        public string State => Current ? "CURRENT DEVICE" : "AVAILABLE NOW";
        public Brush Surface => Current ? new SolidColorBrush(Color.FromRgb(45, 64, 48)) : new SolidColorBrush(Color.FromRgb(24, 32, 30));
        public Brush Outline => Current ? new SolidColorBrush(Color.FromRgb(159, 182, 114)) : new SolidColorBrush(Color.FromRgb(52, 65, 60));
    }

    readonly List<WindowsAudioEndpoint> _devices = [];
    readonly string? _currentId;
    public WindowsAudioEndpoint? SelectedEndpoint { get; private set; }

    public AudioDevicePickerWindow(IEnumerable<WindowsAudioEndpoint> devices, bool microphone, string? currentId)
    {
        InitializeComponent();
        _devices = devices.Where(x => x.IsInput == microphone).OrderBy(x => x.Name).ToList();
        _currentId = currentId;
        Heading.Text = microphone ? "MICROPHONE" : "LISTENING OUTPUT";
        Description.Text = microphone ? "Choose the mic your game will hear" : "Choose the headphones or speakers you will hear";
        RefreshList();
        Loaded += (_, _) => SearchBox.Focus();
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshList();

    void RefreshList()
    {
        if (DevicesList is null || CountText is null) return;
        var query = SearchBox?.Text?.Trim() ?? "";
        var found = _devices.Where(x => query.Length == 0 || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        DevicesList.ItemsSource = found.Select(x => new DeviceCard(x, x.Id == _currentId)).ToList();
        CountText.Text = found.Count == 0 ? "No matching devices · Refresh the list in Audio & Routing" : $"{found.Count} available device(s)";
    }

    void Choose_Click(object sender, RoutedEventArgs e)
    {
        SelectedEndpoint = (sender as FrameworkElement)?.Tag as WindowsAudioEndpoint;
        DialogResult = SelectedEndpoint is not null;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        e.Handled = true;
    }
}
