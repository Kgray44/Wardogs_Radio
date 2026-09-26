using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WardogsRadio.Core;
using WardogsRadio.Diagnostics;
using WardogsRadio.Input;
using WardogsRadio.Playback;
using WardogsRadio.Voicemeeter;

namespace WardogsRadio.App;

public partial class MainWindow : Window, IMacroActionHandler
{
    const double LocalHeadsetLoudnessCalibrationDb = 3;
    sealed record MacroCard(RadioMacro Macro, string Tag, string ActionSummary, string BindingSummary, string HealthSummary, string ActivationSummary, string StateSummary, int ActionCount)
    {
        public string Glyph => Macro.Glyph;
        public string AccentColor => Macro.AccentColor;
        public string ActionCountLabel => ActionCount == 1 ? "1 action" : $"{ActionCount} actions";
        public string HealthColor => Tag switch { "READY" => "#9FB672", "BROKEN" => "#D68A65", "RUNNING" => "#5EA69A", "DISABLED" => "#82918A", _ => "#E6B65A" };
        public bool CanCancel => Tag == "RUNNING";
        public string RunLabel => StateSummary == "ON" && Macro.Activation is (MacroActivation.Hold or MacroActivation.Momentary) ? "RELEASE" :
            StateSummary == "ON" && Macro.Activation == MacroActivation.Toggle ? "TURN OFF" : "RUN";
    }
    sealed record MusicStripChoice(int? Index, string Label);
    // Kept in memory and surfaced only in Diagnostics while repairing startup. It lets
    // us distinguish page/player readiness from the optional B1 feed without adding a
    // noisy normal-playback UI.
    sealed class YouTubeStartupForensics(string stationName)
    {
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly Dictionary<string, long> _events = [];
        long _uiTickCount;
        double _uiTickTotalMilliseconds;
        double _uiTickMaxMilliseconds;
        public string StationName { get; } = stationName;
        public void Mark(string stage) => _events.TryAdd(stage, _clock.ElapsedMilliseconds);
        public void RecordUiTick(TimeSpan elapsed)
        {
            _uiTickCount++;
            _uiTickTotalMilliseconds += elapsed.TotalMilliseconds;
            _uiTickMaxMilliseconds = Math.Max(_uiTickMaxMilliseconds, elapsed.TotalMilliseconds);
        }
        public string Describe() => string.Join(" · ", _events.Select(entry => $"{entry.Key} {entry.Value} ms")) +
            $" · UI signal tick avg {(_uiTickCount == 0 ? 0 : _uiTickTotalMilliseconds / _uiTickCount):0.0} ms, max {_uiTickMaxMilliseconds:0.0} ms";
    }
    // This is temporary repair evidence, intentionally exposed only through
    // Diagnostics. It records the local path's first observed state and peaks
    // without pretending a player IPC response proves an audible endpoint.
    sealed class LocalPlaybackForensics(string stationName)
    {
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly Dictionary<string, long> _events = [];
        public string StationName { get; } = stationName;
        public string? HeadsetDevice { get; private set; }
        public string? HeadsetReportedDevice { get; private set; }
        public string? GameDevice { get; private set; }
        public string? GameReportedDevice { get; private set; }
        public double RequestedVolume { get; private set; }
        public double? ReportedVolume { get; private set; }
        public bool? ReportedMute { get; private set; }
        public bool? ReportedPlaying { get; private set; }
        public float HeadsetPeak { get; private set; }
        public float MusicStripPeak { get; private set; }
        public float B1Peak { get; private set; }
        long _uiTickCount;
        double _uiTickTotalMilliseconds;
        double _uiTickMaxMilliseconds;
        public void Mark(string stage) => _events.TryAdd(stage, _clock.ElapsedMilliseconds);
        public void RecordHeadsetSetup(string? device, double requested)
        {
            HeadsetDevice = device;
            RequestedVolume = requested;
        }
        public void RecordReportedState(double? volume, bool? mute, bool? playing)
        {
            ReportedVolume ??= volume;
            ReportedMute ??= mute;
            ReportedPlaying ??= playing;
        }
        public void RecordReportedDevice(string? device) => HeadsetReportedDevice ??= device;
        public void RecordGameDevice(string? configured, string? reported)
        {
            GameDevice = configured;
            GameReportedDevice ??= reported;
        }
        public void RecordUiTick(TimeSpan elapsed)
        {
            _uiTickCount++;
            _uiTickTotalMilliseconds += elapsed.TotalMilliseconds;
            _uiTickMaxMilliseconds = Math.Max(_uiTickMaxMilliseconds, elapsed.TotalMilliseconds);
        }
        public void RecordPeaks(SignalLevel headset, SignalLevel music, SignalLevel b1)
        {
            HeadsetPeak = Math.Max(HeadsetPeak, headset.Peak);
            MusicStripPeak = Math.Max(MusicStripPeak, music.Peak);
            B1Peak = Math.Max(B1Peak, b1.Peak);
            if (headset.Peak > .001f) Mark("first headset endpoint signal");
            if (b1.Peak > .001f) Mark("first B1 endpoint signal");
        }
        public string Describe() => $"{string.Join(" · ", _events.Select(entry => $"{entry.Key} {entry.Value} ms"))}\n" +
            $"headset configured={HeadsetDevice ?? "not selected"}; mpv={HeadsetReportedDevice ?? "—"}; requested={RequestedVolume:P0}; reported volume={ReportedVolume?.ToString("P0") ?? "—"}; mute={ReportedMute?.ToString() ?? "—"}; playing={ReportedPlaying?.ToString() ?? "—"}\n" +
            $"game configured={GameDevice ?? "off"}; mpv={GameReportedDevice ?? "—"}; endpoint peaks headset={HeadsetPeak:0.0000}, music strip={MusicStripPeak:0.0000}, B1={B1Peak:0.0000}\n" +
            $"signal timer avg {(_uiTickCount == 0 ? 0 : _uiTickTotalMilliseconds / _uiTickCount):0.0} ms, max {_uiTickMaxMilliseconds:0.0} ms";
    }
    readonly ConfigurationStore _store;
    readonly WrRadioPackageService _backupTransfer;
    readonly string? _startupPackagePath;
    readonly VoicemeeterRemote _vm = new();
    readonly VoicemeeterStripLimiter _voicemeeterStripLimiter;
    readonly TemporaryYouTubeRoute _youtubeRoute;
    readonly XInputControllerService _controllers = new();
    readonly DiagnosticService _diagnostics;
    readonly VirtualPlaybackTimeline _timeline = new(new SystemClock());
    readonly WindowsAudioEndpointService _audioEndpoints = new();
    readonly WindowsAudioPeakMeter _headsetPeakMeter = new();
    readonly WindowsAudioCapturePeakMeter _gameBusEndpointPeakMeter = new();
    readonly ClipGuardController _clipGuard = new();
    readonly PrimaryPlaybackStartupCoordinator _youtubeStartupCoordinator = new();
    readonly BroadcastLevelTestSession _broadcastLevelTest = new();
    AppConfiguration _config = new();
    OutputHealthSnapshot? _outputHealth;
    OutputTelemetryAssessment _outputTelemetry = new(OutputTelemetryConfidence.Unavailable, "B1 telemetry has not been sampled yet.");
    VoicemeeterMeterForensics? _meterForensics;
    BroadcastLevelTestResult? _broadcastLevelTestResult;
    Station? _active;
    bool _youtubeReady;
    Task? _youtubeInitializationTask;
    bool _youtubePlayerReady;
    bool _youtubeHasLiveTimeline;
    bool _youtubeStartPaused;
    bool _youtubeEnded;
    string? _youtubeInstanceToken;
    bool _youtubeRouteRecoveryBlocked;
    string? _youtubeHeadsetRouteError;
    DateTime _nextYouTubeRouteHealthCheck;
    string? _youtubePlayerErrorDetail;
    YouTubeStartupForensics? _youtubeStartupForensics;
    LocalPlaybackForensics? _localStartupForensics;
    double? _youtubePendingResumeSeconds;
    string? _gameOutputEndpointName;
    string? _gameOutputEndpointId;
    bool _loadingSetupControls;
    bool _sawMicSignal, _sawMusicSignal, _sawGameSignal, _sawGameEndpointSignal, _sawMonitorSignal;
    IReadOnlyList<ExternalSessionDescriptor> _externalSessions = [];
    ExternalAudioProvider? _externalProvider;
    MpvProvider? _mpvProvider;
    MpvProvider? _gameMpvProvider;
    YouTubeGameFeed? _youtubeGameFeed;
    string? _youtubeGameFeedError;
    GlobalHotkeyService? _hotkeys;
    GlobalKeyReleaseMonitor? _keyRelease;
    RawHidControllerMonitor? _rawControllers;
    XInputControllerMonitor? _xinputEvents;
    TaskCompletionSource<ControllerControlEvent?>? _controllerCapture;
    bool _windowHookAttached;
    bool _loadingMusicStrips;
    bool _loadingAudioRouteControls;
    bool _loadingPlaybackSettings;
    bool _closingInProgress;
    bool _closingFinalized;
    bool _exitRequested;
    readonly System.Windows.Forms.NotifyIcon _trayIcon;
    readonly ContextMenu _trayMenu;
    readonly Dictionary<int, Guid> _hotkeyMacros = [];
    readonly HashSet<Guid> _unavailableHotkeys = [];
    readonly HashSet<Guid> _unavailableRelease = [];
    readonly Dictionary<uint, List<int>> _releaseHotkeys = [];
    readonly MacroHeldSourceTracker _heldMacroSources = new();
    readonly MacroExecutionEngine _macroEngine;
    readonly MacroExecutionContext _setupRouteContext = new();
    readonly List<(int Strip, string Route)> _setupChangedRoutes = [];
    readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    // Keep normal mixer telemetry at the known-good 5 Hz cadence.  Diagnostics
    // performs the expensive raw scans only while its page is visible.
    readonly DispatcherTimer _signalTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer _volumeSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    readonly SemaphoreSlim _b1AuditionGate = new(1, 1);
    B1Audition? _b1Audition;
    MpvProvider? _b1AuditionMutedPlayer;
    bool _b1PointerHeld;
    bool _b1KeyboardHeld;
    string? _b1AuditionErrorDetail;
    VoicemeeterStatus? _signalStatus;
    DateTime _signalStatusExpires;
    bool _refreshingPlayback;
    bool _refreshingCollections;
    bool _syncingMasterVolumeSliders;
    bool _loadingClipGuardControls;
    DateTime _nextSetupSignalRefresh;
    DateTime _nextSignalPresentation;
    DateTime _nextMeterForensicsCapture;
    DateTimeOffset? _lastOutputEventAt;
    bool _syncingMicrophoneVolumeSliders;
    bool _suppressStationSelectionActivation;
    Station? _stationSelectionBeforeEdit;
    bool _playbackToggleInFlight;
    int _setupStep;
    readonly SemaphoreSlim _activationGate = new(1, 1);
    CancellationTokenSource? _transitionCancellation;
    readonly HashSet<Guid> _pendingOrderRefresh = [];
    readonly HashSet<Guid> _runningMacros = [];
    readonly Dictionary<Guid, CancellationTokenSource> _macroCancellation = [];

    public MainWindow(string? startupPackagePath = null)
    {
        _startupPackagePath = startupPackagePath;
        InitializeComponent();
        _trayMenu = new ContextMenu { Placement = PlacementMode.MousePoint, StaysOpen = false };
        var openTrayMenuItem = new MenuItem { Header = "Open WARDOGS Radio" };
        openTrayMenuItem.Click += (_, _) => QueueRestoreFromTray();
        _trayMenu.Items.Add(openTrayMenuItem);
        _trayMenu.Items.Add(new Separator());
        var exitTrayMenuItem = new MenuItem { Header = "Exit WARDOGS Radio" };
        exitTrayMenuItem.Click += (_, _) => ExitFromTray();
        _trayMenu.Items.Add(exitTrayMenuItem);
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "WARDOGS Radio",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => QueueRestoreFromTray();
        _trayIcon.MouseUp += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    _trayMenu.IsOpen = false;
                    RestoreFromTray();
                }));
            }
            else if (e.Button == System.Windows.Forms.MouseButtons.Right)
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => _trayMenu.IsOpen = true));
        };
        _voicemeeterStripLimiter = new VoicemeeterStripLimiter(_vm);
        _youtubeRoute = new TemporaryYouTubeRoute(_vm);
        var configurationRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDOGS Radio");
        _store = new ConfigurationStore(configurationRoot);
        _backupTransfer = new WrRadioPackageService(configurationRoot);
        _diagnostics = new DiagnosticService(_vm, _controllers);
        _macroEngine = new MacroExecutionEngine(this);
        _macroEngine.ExecutionStarted += (_, id) => Dispatcher.BeginInvoke(() => { _runningMacros.Add(id); RefreshCollections(); });
        _macroEngine.ExecutionFinished += (_, id) => Dispatcher.BeginInvoke(() => { _runningMacros.Remove(id); RefreshCollections(); });
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _signalTimer.Tick += (_, _) =>
        {
            var started = Stopwatch.GetTimestamp();
            RefreshSignalMeters();
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (_active?.ProviderId == "youtube") _youtubeStartupForensics?.RecordUiTick(elapsed);
            else _localStartupForensics?.RecordUiTick(elapsed);
        };
        _volumeSaveTimer.Tick += async (_, _) =>
        {
            _volumeSaveTimer.Stop();
            try { await _store.SaveAsync(_config); }
            catch (Exception error) { Footer.Text = "LEVEL CHANGE NOT SAVED · " + error.Message; }
        };
    }

    async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplicationVersionText.Text = "Version " + (File.Exists(Path.Combine(AppContext.BaseDirectory, "VERSION")) ? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "VERSION")).Trim() : "development build");
        _config = await _store.LoadAsync();
        InitializeNowPlayingSurface();
        // WebView startup must not wait for optional MPV audio-device discovery.
        // A slow or stalled MPV probe should never leave YouTube stations on a
        // permanent loading placeholder.
        await InitializeYouTubeAsync();
        _loadingPlaybackSettings = true;
        foreach (var station in _config.Profile.Stations) station.Runtime.IsOnAir = false;
        MpvPathBox.Text = _config.MpvPath;
        MpvProviderState.Text = new MpvLocator().Find(_config.MpvPath) is null ? "NEEDS SETUP" : "READY";
        MasterVolume.Value = _config.MasterVolume;
        GameMasterVolume.Value = _config.GameMasterVolume;
        LoadClipGuardControls();
        ShowHeadsetMasterLevel(_config.MasterVolume);
        ShowGameMasterLevel(_config.GameMasterVolume);
        ShowMicrophoneVolume(_config.MicrophoneVolume);
        CrossfadeCheck.IsChecked = _config.CrossfadeEnabled;
        CrossfadeDurationSlider.Value = Math.Clamp(_config.CrossfadeSeconds, .05, 5);
        CrossfadeCurveBox.ItemsSource = Enum.GetValues<TransitionCurve>();
        CrossfadeCurveBox.SelectedItem = _config.Curve;
        RadioMode.IsChecked = _config.DefaultPlaybackMode == PlaybackMode.Radio;
        PlayerMode.IsChecked = _config.DefaultPlaybackMode == PlaybackMode.Player;
        RestartTrackMode.IsChecked = _config.DefaultPlaybackMode == PlaybackMode.RestartTrack;
        _loadingPlaybackSettings = false;
        RefreshCollections();
        await LoadAudioEndpointsAsync();
        await LoadMpvOutputsAsync();
        _vm.TryLogin(out _);
        // Stabilization: release any old limiter lease. Clip Guard remains
        // telemetry-only and is not allowed to alter the playback path.
        ReconcileVoicemeeterLimiter();
        await _store.SaveAsync(_config);
        if (!EnsureYouTubeRouteHealthy(out var routeRecovery))
        {
            Footer.Text = routeRecovery;
        }
        PopulateMusicStrips();
        await InitializeMicrophoneVolumeAsync();
        RegisterHotkeys();
        StartControllerInput();
        RefreshCollections();
        RefreshSetupWizard();
        RunDiagnostics();
        SetActiveNavigation(DashboardNav);
        RefreshSetupWizard();
        _playbackTimer.Start();
        _signalTimer.Start();
        var startupPackagePath = _startupPackagePath;
        if (!string.IsNullOrWhiteSpace(startupPackagePath) && File.Exists(startupPackagePath))
            await Dispatcher.InvokeAsync(() => _ = ImportPackageAsync(startupPackagePath));
    }

    async void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_refreshingPlayback || _mpvProvider is not { } provider || _active is not { } station) return;
        _refreshingPlayback = true;
        try
        {
            var playback = await provider.RefreshAsync();
            if (!ReferenceEquals(provider, _mpvProvider) || !ReferenceEquals(station, _active)) return;
            playback = await EnforceLocalSongBoundaryAsync(station, provider, playback);
            await SyncGameToHeadsetAsync(provider, playback);
            station.Runtime.PositionSeconds = playback.PositionSeconds;
            if (playback.DurationSeconds is > 0) station.Runtime.DurationSeconds = playback.DurationSeconds.Value;
            station.Runtime.SequenceIndex = Math.Clamp(provider.CurrentPlaylistIndex, 0, Math.Max(0, provider.LoadedFiles.Count - 1));
            if (playback.DurationSeconds is > 0 && provider.LoadedFiles.Count > station.Runtime.SequenceIndex)
                CacheDuration(provider.LoadedFiles[station.Runtime.SequenceIndex], playback.DurationSeconds.Value);
            var priorPlaying = station.Runtime.WasPlaying;
            var priorOnAir = station.Runtime.IsOnAir;
            station.Runtime.WasPlaying = playback.IsPlaying;
            station.Runtime.IsOnAir = playback.IsPlaying;
            RefreshDashboardPlaylist();
            RefreshSharedPlaybackPresentation();
            if (!string.IsNullOrWhiteSpace(playback.Track?.Title)) NativeStatus.Text = playback.Track.Title;
            NativeHint.Text = playback.IsPlaying ? "Playing · Check your headphones and voice chat output." : "Paused · Press Play to resume.";
            if (priorPlaying != playback.IsPlaying || priorOnAir != station.Runtime.IsOnAir) RefreshCollections();
        }
        catch (Exception error)
        {
            if (ReferenceEquals(provider, _mpvProvider))
            {
                NativeHint.Text = "Native player telemetry unavailable: " + error.Message;
                Progress.Value = 0;
            }
        }
        finally { _refreshingPlayback = false; }
    }

    static string DisplayTime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");
    }

    void RegisterHotkeys()
    {
        _hotkeys?.Dispose();
        _keyRelease?.Dispose();
        _hotkeyMacros.Clear();
        _unavailableHotkeys.Clear();
        _unavailableRelease.Clear();
        _releaseHotkeys.Clear();
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        if (source is null) return;
        if (!_windowHookAttached) { source.AddHook(WindowMessage); _windowHookAttached = true; }
        _hotkeys = new GlobalHotkeyService(source.Handle);
        var id = 700;
        foreach (var macro in _config.Profile.Macros.Where(x => x.Enabled))
        {
            var bindings = macro.KeyboardBindings.Count > 0 ? macro.KeyboardBindings : string.IsNullOrWhiteSpace(macro.Hotkey) ? [] : [macro.Hotkey];
            foreach (var binding in bindings)
            {
                if (!TryParseBinding(binding, out var key, out var modifiers)) { _unavailableHotkeys.Add(macro.Id); continue; }
                var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
                if (_hotkeys.TryRegister(new HotkeyRegistration(id, virtualKey, macro.Name, modifiers), out var detail))
                {
                    var registeredId = id++;
                    _hotkeyMacros[registeredId] = macro.Id;
                    if (macro.Activation is MacroActivation.Hold or MacroActivation.Momentary)
                    {
                        if (!_releaseHotkeys.TryGetValue(virtualKey, out var list)) _releaseHotkeys[virtualKey] = list = [];
                        list.Add(registeredId);
                    }
                }
                else _unavailableHotkeys.Add(macro.Id);
            }
        }
        if (_releaseHotkeys.Count > 0)
        {
            _keyRelease = new GlobalKeyReleaseMonitor(_releaseHotkeys.Keys);
            _keyRelease.KeyReleased += Released;
            if (!_keyRelease.IsAvailable)
                foreach (var macroId in _releaseHotkeys.Values.SelectMany(x => x).Select(x => _hotkeyMacros[x])) _unavailableRelease.Add(macroId);
        }
    }

    static bool TryParseBinding(string binding, out Key key, out uint modifiers)
    {
        key = Key.None; modifiers = 0;
        var parts = binding.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !Enum.TryParse(parts[^1], true, out key) || key == Key.None) return false;
        foreach (var part in parts.SkipLast(1))
            modifiers |= part.ToLowerInvariant() switch { "ctrl" or "control" => 0x0002u, "alt" => 0x0001u, "shift" => 0x0004u, _ => 0u };
        return parts.SkipLast(1).All(x => x.Equals("ctrl", StringComparison.OrdinalIgnoreCase) || x.Equals("control", StringComparison.OrdinalIgnoreCase) || x.Equals("alt", StringComparison.OrdinalIgnoreCase) || x.Equals("shift", StringComparison.OrdinalIgnoreCase));
    }

    void StartControllerInput()
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        if (source is null) return;
        _rawControllers = new RawHidControllerMonitor(source.Handle);
        _rawControllers.ControlChanged += ControllerChanged;
        _rawControllers.DevicesChanged += ControllerInventoryChanged;
        _xinputEvents = new XInputControllerMonitor();
        _xinputEvents.ControlChanged += ControllerChanged;
        _xinputEvents.DevicesChanged += ControllerInventoryChanged;
    }

    void ControllerInventoryChanged() => Dispatcher.BeginInvoke(() => { RefreshCollections(); RunDiagnostics(); });

    void ControllerChanged(ControllerControlEvent input) => Dispatcher.BeginInvoke(async () =>
    {
        if (input.Pressed && _controllerCapture is { } capture)
        {
            _controllerCapture = null;
            capture.TrySetResult(input);
            return;
        }
        foreach (var macro in _config.Profile.Macros.Where(x => x.Enabled && x.ControllerBindings.Contains(input.BindingKey, StringComparer.OrdinalIgnoreCase)))
            await RunMacroAsync(macro, input.Pressed ? MacroTrigger.Press : MacroTrigger.Release, input.BindingKey);
    });

    async Task<ControllerControlEvent?> CaptureControllerAsync(CancellationToken cancellationToken)
    {
        if (_controllerCapture is not null) throw new InvalidOperationException("A controller binding is already being captured.");
        var capture = new TaskCompletionSource<ControllerControlEvent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _controllerCapture = capture;
        using var registration = cancellationToken.Register(() => capture.TrySetResult(null));
        try { return await capture.Task; }
        finally { if (ReferenceEquals(_controllerCapture, capture)) _controllerCapture = null; }
    }

    void Released(uint virtualKey)
    {
        if (_releaseHotkeys.TryGetValue(virtualKey, out var hotkeyIds))
            Dispatcher.BeginInvoke(async () =>
            {
                foreach (var hotkeyId in hotkeyIds)
                {
                    if (!_hotkeyMacros.TryGetValue(hotkeyId, out var macroId)) continue;
                    var macro = _config.Profile.Macros.FirstOrDefault(x => x.Id == macroId);
                    if (macro is not null) await RunMacroAsync(macro, MacroTrigger.Release, $"hotkey:{hotkeyId}");
                }
            });
    }

    IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        _rawControllers?.HandleMessage(message, wParam, lParam);
        if (_hotkeys?.IsHotkeyMessage(message, wParam, out var id) == true && _hotkeyMacros.TryGetValue(id, out var macroId))
        {
            var macro = _config.Profile.Macros.FirstOrDefault(x => x.Id == macroId);
            if (macro is not null) Dispatcher.BeginInvoke(async () => await RunMacroAsync(macro, MacroTrigger.Press, $"hotkey:{id}"));
            handled = true;
        }
        return IntPtr.Zero;
    }

    Task InitializeYouTubeAsync()
    {
        if (_youtubeReady) return Task.CompletedTask;
        return _youtubeInitializationTask ??= InitializeYouTubeCoreAsync();
    }

    async Task InitializeYouTubeCoreAsync()
    {
        // WebView2 WPF may defer Core creation forever when its host is
        // Collapsed. Keep it in layout but visually hidden just long enough to
        // establish the Core, then restore the normal collapsed idle state.
        var restoreCollapsed = YouTubeView.Visibility == Visibility.Collapsed;
        if (restoreCollapsed) YouTubeView.Visibility = Visibility.Hidden;
        try
        {
            await YouTubeView.EnsureCoreWebView2Async();
            YouTubeView.CoreWebView2.WebMessageReceived += YouTubeMessage;
            YouTubeView.CoreWebView2.SetVirtualHostNameToFolderMapping("wardogs-radio.example", AppContext.BaseDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
            _youtubeReady = true;
            YouTubeProviderState.Text = "WEBVIEW2 READY";
            RefreshCollections();
        }
        catch (Exception ex)
        {
            YouTubeProviderState.Text = "WEBVIEW2 UNAVAILABLE";
            Footer.Text = $"WEBVIEW2 UNAVAILABLE · {ex.Message}";
        }
        finally
        {
            if (restoreCollapsed && YouTubeView.Visibility == Visibility.Hidden)
                YouTubeView.Visibility = Visibility.Collapsed;
            _youtubeInitializationTask = null;
        }
    }

    async Task<bool> EnsureYouTubeReadyAsync()
    {
        if (!_youtubeReady) await InitializeYouTubeAsync();
        return _youtubeReady;
    }

    bool EnsureYouTubeRouteHealthy(out string detail)
    {
        // A failed restore is evidence only until a current recovery attempt fails.
        // Never let an earlier transient failure poison every later YouTube tune.
        if (_youtubeRoute.IsActive)
        {
            if (_youtubeRoute.TryCheckHealth(out _))
            {
                _youtubeRouteRecoveryBlocked = false;
                _youtubeHeadsetRouteError = null;
                detail = "Temporary YouTube route is active and healthy.";
                return true;
            }
            if (!_youtubeRoute.TryEnd(out var endFailure))
            {
                detail = "The active temporary route could not be restored: " + endFailure;
                _youtubeRouteRecoveryBlocked = true;
                _youtubeHeadsetRouteError = detail;
                return false;
            }
            _youtubeRouteRecoveryBlocked = false;
            _youtubeHeadsetRouteError = null;
            detail = "Repaired the previous temporary YouTube route.";
            return true;
        }
        if (!_youtubeRoute.TryRecover(out var recovery))
        {
            detail = recovery;
            _youtubeRouteRecoveryBlocked = true;
            _youtubeHeadsetRouteError = recovery;
            return false;
        }

        _youtubeRouteRecoveryBlocked = false;
        _youtubeHeadsetRouteError = null;
        detail = recovery;
        return true;
    }

    void ResetYouTubePresentationForLoad(Station station, bool startPaused)
    {
        _youtubePlayerReady = false;
        _youtubeHasLiveTimeline = false;
        _youtubeEnded = false;
        _youtubePlayerErrorDetail = null;
        _youtubeStartPaused = startPaused;
        _youtubePendingResumeSeconds = null;
        _youtubeInstanceToken = null;
        station.Runtime.WasPlaying = false;
        station.Runtime.IsOnAir = false;
        YouTubeView.Visibility = Visibility.Collapsed;
        YouTubePlaceholder.Visibility = Visibility.Visible;
        YouTubePlaceholderTitle.Text = "YOUTUBE PLAYER LOADING";
        YouTubePlaceholderDetail.Text = "Preparing the visible player and checking its temporary listening route…";
        RepairYouTubeRouteButton.Visibility = Visibility.Collapsed;
        RefreshSharedPlaybackPresentation();
    }

    void ShowYouTubeRouteRepairRequired(string detail)
    {
        _youtubeRouteRecoveryBlocked = true;
        _youtubePlayerReady = false;
        _youtubeHasLiveTimeline = false;
        if (_active?.ProviderId != "youtube") return;
        YouTubeView.Visibility = Visibility.Collapsed;
        YouTubePlaceholder.Visibility = Visibility.Visible;
        YouTubePlaceholderTitle.Text = "YOUTUBE AUDIO ROUTE NEEDS REPAIR";
        YouTubePlaceholderDetail.Text = detail;
        RepairYouTubeRouteButton.Visibility = Visibility.Visible;
        RefreshSharedPlaybackPresentation();
    }

    async void RepairYouTubeRoute_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureYouTubeRouteHealthy(out var detail))
        {
            ShowYouTubeRouteRepairRequired(detail);
            Footer.Text = "YOUTUBE ROUTE NEEDS RESTORATION · " + detail;
            return;
        }
        if (_active is { ProviderId: "youtube" } station)
        {
            Footer.Text = "YOUTUBE ROUTE REPAIRED · Reloading the visible player.";
            await LoadYouTubeAsync(station, startPaused: false);
        }
    }

    async void YouTubeMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_active?.ProviderId != "youtube" || YouTubeView.Visibility == Visibility.Collapsed) return;
        try
        {
            using var message = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var instance = message.RootElement.TryGetProperty("instance", out var instanceValue) ? instanceValue.GetString() : null;
            if (instance != _youtubeInstanceToken) return;
            var type = message.RootElement.GetProperty("type").GetString();
            var detail = message.RootElement.TryGetProperty("detail", out var value) ? value.GetString() : null;
            if (type == "ready")
            {
                _youtubeStartupForensics?.Mark("T4 player ready");
                _youtubePlayerReady = true;
                RefreshSharedPlaybackPresentation();
                UpdateRepeatButton();
                if (_active.PlaylistSongs.Count > 0) await SetYouTubeSongsAsync(_active);
                var activeStation = _active;
                var initialReturnSeconds = _youtubePendingResumeSeconds ?? 0;
                await _youtubeStartupCoordinator.StartAsync(async () =>
                {
                    await ApplyYouTubeListeningGainAsync(activeStation);
                    if (initialReturnSeconds > 0)
                        await YouTubeCommandAsync($"seek({initialReturnSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})");
                    if (!_youtubeStartPaused)
                    {
                        _youtubeStartupForensics?.Mark("T7 play issued");
                        await YouTubeCommandAsync("play()");
                    }
                }, async () =>
                {
                    // B1 process-loopback is secondary and may not block the visible
                    // player or headset route after the ready message.
                    _youtubeStartupForensics?.Mark("T5 B1 start scheduled");
                    await StartYouTubeGameFeedAsync(activeStation);
                });
                Footer.Text = _youtubeStartPaused
                    ? "YOUTUBE READY · Station selected in a paused state."
                    : initialReturnSeconds > 0
                        ? $"YOUTUBE READY · Resuming near {DisplayTime(initialReturnSeconds)} in the visible player."
                        : "YOUTUBE READY · Playback requested in the visible player. If your browser blocks autoplay, press Play in the video.";
                _youtubeStartPaused = false;
                if (_youtubeHeadsetRouteError is not null)
                    Footer.Text += " B1-only hold unavailable: " + _youtubeHeadsetRouteError;
            }
            else if (type == "state")
            {
                var playing = detail == "1";
                if (detail is "0" or "1" or "2")
                {
                    if (detail == "0")
                    {
                        _youtubeEnded = true;
                        _youtubePendingResumeSeconds = null;
                        _active.Runtime.PositionSeconds = 0;
                        Progress.Value = 0;
                        TimeText.Text = $"00:00 / {(_active.Runtime.DurationSeconds > 0 ? DisplayTime(_active.Runtime.DurationSeconds) : "--:--")}";
                    }
                    else if (playing) _youtubeEnded = false;
                    if (playing) _youtubeStartupForensics?.Mark("T8 playing state");
                    _active.Runtime.WasPlaying = playing;
                    _active.Runtime.IsOnAir = playing;
                    PlayButton.Content = playing ? "Ⅱ  PAUSE" : "▶  PLAY";
                    RefreshCollections();
                    RefreshSharedPlaybackPresentation();
                }
            }
            else if (type == "progress" && detail is not null)
            {
                using var progress = JsonDocument.Parse(detail);
                var position = progress.RootElement.GetProperty("position").GetDouble();
                var duration = progress.RootElement.GetProperty("duration").GetDouble();
                if (double.IsFinite(position) && double.IsFinite(duration))
                {
                    _youtubeStartupForensics?.Mark("T9 progress");
                    _youtubeHasLiveTimeline = true;
                    if (_youtubeEnded) return;
                    if (_youtubePendingResumeSeconds is { } pending)
                    {
                        if (position < pending - 2) return;
                        _youtubePendingResumeSeconds = null;
                    }
                    _active.Runtime.PositionSeconds = position;
                    if (_active.PlaylistSongs.Count > 0 && progress.RootElement.TryGetProperty("segmentIndex", out var segmentIndex) && segmentIndex.TryGetInt32(out var songIndex))
                        _active.Runtime.SequenceIndex = Math.Clamp(songIndex, 0, _active.PlaylistSongs.Count - 1);
                    if (duration > 0)
                    {
                        _active.Runtime.DurationSeconds = duration;
                        var song = _active.PlaylistSongs.ElementAtOrDefault(Math.Clamp(_active.Runtime.SequenceIndex, 0,
                            Math.Max(0, _active.PlaylistSongs.Count - 1)));
                        var source = song is null ? null : _config.MusicLibrary.Sources.FirstOrDefault(candidate =>
                            candidate.ProviderId == "youtube" && string.Equals(candidate.Source, song.Source, StringComparison.OrdinalIgnoreCase));
                        if (source is not null && (source.DurationSeconds is not { } known || Math.Abs(known - duration) >= .1))
                            source.DurationSeconds = duration;
                    }
                    RefreshDashboardPlaylist();
                    RefreshSharedPlaybackPresentation();
                }
            }
            else if (type == "error")
            {
                _active.Runtime.WasPlaying = false;
                _active.Runtime.IsOnAir = false;
                PlayButton.Content = "▶  PLAY";
                _youtubePlayerErrorDetail = $"YouTube IFrame player error {detail ?? "unknown"} while opening {_active.Name}: {_active.Source}";
                Footer.Text = detail == "153" ? "THIS YOUTUBE VIDEO COULD NOT OPEN · Check the connection or try another video." :
                    detail is "101" or "150" ? "THIS VIDEO CANNOT PLAY HERE · Its owner does not allow embedded playback." :
                    "YOUTUBE PLAYBACK FAILED · Try another video or run Diagnostics for details.";
                YouTubeView.Visibility = Visibility.Collapsed;
                YouTubePlaceholder.Visibility = Visibility.Visible;
                YouTubePlaceholderTitle.Text = "YOUTUBE PLAYER ERROR";
                YouTubePlaceholderDetail.Text = Footer.Text;
                RepairYouTubeRouteButton.Visibility = Visibility.Collapsed;
                RefreshSharedPlaybackPresentation();
            }
        }
        catch (Exception error)
        {
            _youtubePlayerErrorDetail = error.ToString();
            Footer.Text = "YOUTUBE PLAYBACK STATUS UNAVAILABLE · Run Diagnostics for details.";
        }
    }

    async Task YouTubeCommandAsync(string command)
    {
        if (!_youtubeReady || !_youtubePlayerReady) throw new InvalidOperationException("YouTube player is still loading or unavailable.");
        await YouTubeView.CoreWebView2.ExecuteScriptAsync("window.wardogs?." + command + ";");
    }

    async Task ApplyYouTubeListeningGainAsync(Station station)
    {
        // A persistent WebView audio session can retain its original Windows
        // endpoint. Keep the temporary Voicemeeter route at unity and make the
        // player the sole listening-gain authority, regardless of that route.
        if (_youtubeRoute.IsActive) _youtubeRoute.SetHeadsetGain(1);
        var gain = Math.Clamp((int)Math.Round(_config.MasterVolume * station.Volume * 100), 0, 100);
        await YouTubeCommandAsync($"volume({gain})");
    }

    async Task SilenceYouTubeForRouteTeardownAsync()
    {
        if (!_youtubeReady || !_youtubePlayerReady) return;
        try
        {
            // Silence the player before restoring Windows' physical default so
            // route teardown cannot leave an audible WebView session behind.
            await YouTubeView.CoreWebView2.ExecuteScriptAsync(
                "(() => { window.wardogs?.volume(0); window.wardogs?.pause(); })();");
            await Task.Delay(150);
        }
        catch
        {
            // Route restoration still has to run and retains its recovery record
            // if it cannot complete; a failed best-effort player command must
            // not block that recovery path.
        }
    }

    void RefreshCollections()
    {
        var stations = _config.Profile.Stations.OrderBy(x => x.Order).ToList();
        var selectedId = (StationList.SelectedItem as Station)?.Id ?? _active?.Id;
        StationRail.ItemsSource = stations;
        _refreshingCollections = true;
        try
        {
            StationList.ItemsSource = stations;
        StationList.SelectedItem = stations.FirstOrDefault(x => x.Id == selectedId);
        }
        finally { _refreshingCollections = false; }
        var setupStationId = (SetupStationBox.SelectedItem as Station)?.Id ?? _active?.Id;
        SetupStationBox.ItemsSource = stations.Where(x => x.Enabled && (x.PlaylistFiles?.Count > 0 || !string.IsNullOrWhiteSpace(x.Source))).ToList();
        SetupStationBox.SelectedItem = (SetupStationBox.ItemsSource as IEnumerable<Station>)?.FirstOrDefault(x => x.Id == setupStationId);
        var macros = _config.Profile.Macros.OrderBy(x => x.Order).ToList();
        MacroRail.ItemsSource = macros.Where(x => x.ShowOnDashboard && x.Enabled).Select(MacroCardFor).ToList();
        var cards = macros.Select(MacroCardFor).ToList();
        if (MacroSearchBox is not null && !string.IsNullOrWhiteSpace(MacroSearchBox.Text))
            cards = cards.Where(x => x.Macro.Name.Contains(MacroSearchBox.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        var statusFilter = (MacroFilterBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (!string.IsNullOrWhiteSpace(statusFilter) && statusFilter != "All")
            cards = cards.Where(x => x.Tag.Equals(statusFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        MacroPageList.ItemsSource = cards;
        RefreshDashboardPlaylist();
        RefreshNowPlayingPresentation();
    }

    void MacroFilter_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) RefreshCollections(); }

    async void MacroDashboardVisibility_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RadioMacro macro || sender is not CheckBox check) return;
        var prior = macro.ShowOnDashboard;
        macro.ShowOnDashboard = check.IsChecked == true;
        try
        {
            await _store.SaveAsync(_config);
            RefreshCollections();
            Footer.Text = macro.ShowOnDashboard ? $"{macro.Name} now appears on the Dashboard." : $"{macro.Name} is hidden from the Dashboard.";
        }
        catch (Exception error)
        {
            macro.ShowOnDashboard = prior;
            RefreshCollections();
            Footer.Text = "DASHBOARD MACRO CHOICE NOT SAVED · " + error.Message;
        }
    }

    MacroCard MacroCardFor(RadioMacro macro)
    {
        var devices = _controllers.Enumerate().ToList();
        var connected = devices.Select(x => x.DeviceId).Where(x => x is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var controllerAvailable = macro.ControllerBindings.All(x => ControllerBindingCodec.TryDecode(x, out var deviceId, out _) && connected.Contains(deviceId)) &&
            (_rawControllers?.IsAvailable == true || _xinputEvents is not null);
        var health = MacroHealthEvaluator.Evaluate(macro, _config.Profile, IsMacroActionOperational,
            !_unavailableHotkeys.Contains(macro.Id), !_unavailableRelease.Contains(macro.Id),
            controllerAvailable, _runningMacros.Contains(macro.Id));
        var bindings = macro.KeyboardBindings.Count > 0 ? macro.KeyboardBindings : string.IsNullOrWhiteSpace(macro.Hotkey) ? [] : [macro.Hotkey];
        var first = macro.Actions.FirstOrDefault();
        var count = macro.Actions.Count + macro.ReleaseActions.Count + macro.OffActions.Count;
        var actionSummary = first is null ? "Add an action" : MacroActionCatalog.Summarize(first, _config.Profile.Stations);
        var visibleBindings = bindings.Concat(macro.ControllerBindings.Select(x => ControllerBindingCodec.Display(x, devices))).ToList();
        return new MacroCard(macro, health.State.ToString().ToUpperInvariant(), actionSummary,
            string.Join(" · ", visibleBindings.DefaultIfEmpty("No binding")),
            string.Join(" · ", health.Issues.Distinct()), macro.Activation.ToString().ToUpperInvariant(),
            macro.Activation is MacroActivation.Toggle or MacroActivation.Hold or MacroActivation.Momentary ? _macroEngine.IsActive(macro.Id) ? "ON" : "OFF" : "", count);
    }

    bool IsMacroActionOperational(RadioAction action)
    {
        if (action.Kind is ActionKind.Delay or ActionKind.OpenPage) return true;
        if (action.Kind == ActionKind.LaunchApplication) return !string.IsNullOrWhiteSpace(action.Argument) && File.Exists(action.Argument);
        if (action.Kind == ActionKind.OpenVoicemeeter) return FindVoicemeeterExecutable(_vm.Probe().Edition) is not null;
        if (action.Kind is ActionKind.SetPlayerMode or ActionKind.SetRadioMode) return true;
        if (action.Kind == ActionKind.CycleStations) return _config.Profile.Stations.Any(station => station.Enabled);
        if (action.Kind is ActionKind.PlayCurrentStation or ActionKind.PauseCurrentStation)
            return _active is not null && (_mpvProvider?.Snapshot.Health == ProviderHealth.Ready ||
                _externalProvider?.Snapshot.Health == ProviderHealth.Ready || _active.ProviderId == "youtube" && _youtubePlayerReady);
        if (action.Kind is ActionKind.SetHeadsetMasterGain or ActionKind.SetGameMasterGain or
            ActionKind.RestorePreviousHeadsetMasterGain or ActionKind.RestorePreviousGameMasterGain) return true;
        if (action.Kind == ActionKind.SetTransitionDuration) return true;
        if (action.Kind == ActionKind.FadeToStation) return _active?.ProviderId == "mpv" &&
            _config.Profile.Stations.Any(x => x.Id == action.StationId && x.ProviderId == "mpv" && x.Enabled);
        if (action.Kind is ActionKind.SetMasterGain or ActionKind.Duck or ActionKind.RestorePreviousMasterGain) return _mpvProvider?.Snapshot.Health == ProviderHealth.Ready;
        if (action.Kind is ActionKind.MuteAll or ActionKind.RestoreMusic) return _mpvProvider?.Snapshot.Health == ProviderHealth.Ready;
        if (action.Kind is ActionKind.ToggleGameRoute or ActionKind.EnableGameRoute or ActionKind.DisableGameRoute or
            ActionKind.RestorePreviousBroadcastState or ActionKind.ToggleMonitorRoute or ActionKind.EnableMonitorRoute or
            ActionKind.DisableMonitorRoute or ActionKind.RestorePreviousMonitorState)
            return _config.MusicStripIndex is { } strip && new VoicemeeterRouteController(_vm).TryRead(strip,
                action.Kind is ActionKind.ToggleMonitorRoute or ActionKind.EnableMonitorRoute or ActionKind.DisableMonitorRoute or ActionKind.RestorePreviousMonitorState
                    ? "A1" : _config.GameBus, out _);
        if (action.Kind is ActionKind.SetStationGain or ActionKind.RestorePreviousStationGain)
            return _config.Profile.Stations.Any(x => x.Id == action.StationId) &&
                (_active?.Id != action.StationId || _mpvProvider?.Snapshot.Health == ProviderHealth.Ready);
        if (action.Kind is ActionKind.ActivateStation or ActionKind.PlayStation or ActionKind.PauseStation or ActionKind.ToggleStation)
        {
            var station = _config.Profile.Stations.FirstOrDefault(x => x.Id == action.StationId);
            if (station?.ProviderId == "mpv") return new MpvLocator().Find(_config.MpvPath) is not null &&
                (station.PlaylistFiles?.Count > 0 ? station.PlaylistFiles.All(File.Exists) :
                    File.Exists(station.Source) || Directory.Exists(station.Source) || Uri.TryCreate(station.Source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https");
            if (station?.ProviderId == "youtube") return _youtubeReady && YouTubeUrl.Normalize(station.Source).IsValid;
            return station?.ProviderId == "external-audio" && _externalProvider?.Snapshot.Health == ProviderHealth.Ready;
        }
        if (action.Kind is ActionKind.Next or ActionKind.Previous or ActionKind.Seek) return _mpvProvider?.Snapshot.Health == ProviderHealth.Ready || _externalProvider?.Snapshot.Health == ProviderHealth.Ready;
        return false;
    }

    void Show(UIElement element, string title, string caption, Button nav)
    {
        foreach (var view in new UIElement[] { DashboardView, StationsView, LibraryView, MacrosView, AudioView, SettingsView, DiagnosticsView, SetupView })
            view.Visibility = Visibility.Collapsed;
        element.Visibility = Visibility.Visible;
        PageTitle.Text = title;
        PageCaption.Text = caption;
        SetActiveNavigation(nav);
    }

    void SetActiveNavigation(Button current)
    {
        foreach (var button in new[] { DashboardNav, StationsNav, LibraryNav, MacrosNav, AudioNav, SettingsNav, DiagnosticsNav, SetupNav })
            button.Tag = button == current ? "active" : null;
    }

    void Dashboard_Click(object s, RoutedEventArgs e) => Show(DashboardView, "DASHBOARD", "Your stations and sound", DashboardNav);
    void Stations_Click(object s, RoutedEventArgs e) => Show(StationsView, "STATIONS", "Choose or create a station", StationsNav);
    void Library_Click(object s, RoutedEventArgs e)
    {
        RefreshLibrary();
        Show(LibraryView, "MUSIC LIBRARY", "Sources and song cues shared by your stations", LibraryNav);
    }
    void Macros_Click(object s, RoutedEventArgs e) => Show(MacrosView, "MACROS", "Your radio shortcuts", MacrosNav);
    void Audio_Click(object s, RoutedEventArgs e) => Show(AudioView, "AUDIO & ROUTING", "Where your sound goes", AudioNav);
    void Settings_Click(object s, RoutedEventArgs e) { RefreshRecentBackups(); Show(SettingsView, "SETTINGS", "Music, providers, and controls", SettingsNav); }

    void RefreshRecentBackups()
    {
        try
        {
            var backups = Directory.Exists(_backupTransfer.BackupsDirectory)
                ? Directory.EnumerateFiles(_backupTransfer.BackupsDirectory, "*.wradio").OrderByDescending(File.GetLastWriteTime).Take(3)
                    .Select(path => $"{Path.GetFileName(path)} · {File.GetLastWriteTime(path):g}").ToList()
                : [];
            RecentBackupsText.Text = backups.Count == 0 ? "No automatic safety backups yet." : string.Join(Environment.NewLine, backups);
        }
        catch (Exception error) { RecentBackupsText.Text = "Backups could not be listed · " + error.Message; }
    }

    ConfigurationMutationPlaybackState ConfigurationMutationState() => new(
        _active is not null,
        _active?.Runtime.WasPlaying == true || _mpvProvider?.Snapshot.IsPlaying == true ||
            _gameMpvProvider?.Snapshot.IsPlaying == true,
        _mpvProvider is not null,
        _gameMpvProvider is not null,
        _youtubeGameFeed is not null,
        _youtubeRoute.IsActive,
        _externalProvider is not null);

    async Task<bool> PrepareConfigurationMutationAsync(string operation)
    {
        var state = ConfigurationMutationState();
        if (state.RequiresStopConfirmation && !RadioDialogWindow.Confirm(this, $"{operation} package?",
            "WARDOGS needs to stop the current station before changing configuration.", "STOP AND CONTINUE")) return false;
        if (!state.RequiresDetach) return true;

        _transitionCancellation?.Cancel();
        await _activationGate.WaitAsync();
        try
        {
            // A selected station is not necessarily playing, but every attached
            // provider must be detached before imported configuration replaces its
            // objects. Stop actual playback explicitly; disposal is only the
            // ownership boundary, never the only stop mechanism.
            if (state.ActualPlayback)
            {
                if (_mpvProvider is { } liveHeadset) try { await liveHeadset.StopAsync(); } catch { }
                if (_gameMpvProvider is { } liveGame) try { await liveGame.StopAsync(); } catch { }
                if (_externalProvider is { } liveExternal) try { await liveExternal.StopAsync(); } catch { }
            }
            if (_active?.ProviderId == "youtube")
            {
                await SilenceYouTubeForRouteTeardownAsync();
                if (_youtubeReady) YouTubeView.CoreWebView2.Navigate("about:blank");
            }
            await StopB1AuditionAsync("Configuration change detached the current station.");
            await StopGameOutputAsync();
            if (_mpvProvider is { } headset)
            {
                _mpvProvider = null;
                await headset.DisposeAsync();
            }
            if (_externalProvider is { } external)
            {
                _externalProvider = null;
                await external.DisposeAsync();
            }
            EndYouTubeRoute("Configuration change detached the current station, but the temporary YouTube route needs restoration.");
            _active = null;
            _youtubePlayerReady = false;
            _youtubeInstanceToken = null;
            _youtubePendingResumeSeconds = null;
            _youtubeGameFeedError = null;
            _localStartupForensics = null;
            _youtubeStartupForensics = null;
            _localPlaylistEnded = false;
            PlayButton.Content = "▶  PLAY";
            NowStation.Text = "NO STATION SELECTED";
            NowDetail.Text = "Import or restore will load a fresh station configuration.";
            NativeStatus.Text = "SELECT A STATION";
            NativeHint.Text = "Playback was detached before the configuration changed.";
            UpdateRepeatButton();
        }
        finally { _activationGate.Release(); }
        return true;
    }

    async void ExportConfiguration_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new BackupTransferWindow(export: true, stations: _config.Profile.Stations,
            songs: _config.MusicLibrary.Songs, macros: _config.Profile.Macros) { Owner = this };
        if (wizard.ShowDialog() != true || wizard.ExportSelection is not { } selection) return;
        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "WARDOGS Radio package (*.wradio)|*.wradio", DefaultExt = ".wradio",
            FileName = selection.PackageType == WrRadioPackageType.FullBackup
                ? $"Wardogs-Radio-Backup-{DateTime.Now:yyyy-MM-dd}.wradio"
                : $"Wardogs-Radio-Export-{DateTime.Now:yyyy-MM-dd}.wradio"
        };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var summary = await _backupTransfer.ExportAsync(_config, selection, picker.FileName);
            Footer.Text = $"EXPORTED · {summary.Stations} station(s), {summary.Songs} song(s), {summary.Sources} source(s), {summary.Macros} macro(s).";
        }
        catch (Exception error) { RadioDialogWindow.Inform(this, "Export failed", error.Message); }
    }

    async void ImportPackage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "WARDOGS Radio package (*.wradio)|*.wradio", DefaultExt = ".wradio" };
        if (picker.ShowDialog(this) != true) return;
        await ImportPackageAsync(picker.FileName);
    }

    async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;

        var packages = paths.Where(path => path.EndsWith(".wradio", StringComparison.OrdinalIgnoreCase)).ToList();
        if (packages.Count > 0)
        {
            if (packages.Count != 1 || paths.Length != 1)
            {
                RadioDialogWindow.Inform(this, "Choose one package", "Drop one .wradio package to preview and import it safely.");
                return;
            }
            await ImportPackageAsync(packages[0]);
            return;
        }

        var media = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (media.Count == 0)
        {
            Footer.Text = "DROP IGNORED · Drag existing media files or one .wradio package.";
            return;
        }
        try
        {
            foreach (var path in media)
            {
                var source = MusicLibraryService.EnsureSource(_config.MusicLibrary, "mpv", path,
                    Path.GetFileNameWithoutExtension(path));
                MusicLibraryService.EnsureWholeSourceSong(_config.MusicLibrary, source, source.Name);
            }
            await _store.SaveAsync(_config);
            RefreshLibrary();
            Footer.Text = $"{media.Count} {Plural(media.Count, "LOCAL SOURCE", "LOCAL SOURCES")} ADDED TO LIBRARY · Media remains in place.";
        }
        catch (Exception error) { Footer.Text = "COULD NOT ADD DROPPED MEDIA · " + error.Message; }
    }

    async Task ImportPackageAsync(string packagePath)
    {
        try
        {
            var package = await _backupTransfer.OpenAsync(packagePath);
            var wizard = new BackupTransferWindow(export: false, _backupTransfer.BuildImportPlan(_config, package).Summary,
                package.Stations, package.Songs, package.Macros) { Owner = this };
            if (wizard.ShowDialog() != true || wizard.ImportOptions is not { } options) return;
            var presentEndpoints = await _audioEndpoints.DiscoverAsync();
            options.AvailableAudioDeviceIds = presentEndpoints.Select(endpoint => endpoint.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            options.AvailableControllerDeviceIds = _controllers.Enumerate().Where(controller => controller.Connected && !string.IsNullOrWhiteSpace(controller.DeviceId))
                .Select(controller => controller.DeviceId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var preview = _backupTransfer.BuildImportPlan(_config, package, options);
            var conflictText = preview.Conflicts.Count == 0 ? "No conflicts found." :
                $"{preview.Conflicts.Count} conflict(s) will use the choices you selected:\n• " +
                string.Join("\n• ", preview.Conflicts.Take(3).Select(conflict => $"{conflict.Category}: {conflict.Name} — {conflict.Message}"));
            var warningText = preview.Warnings.Count == 0 ? "" : "\n\nNeeds attention:\n• " + string.Join("\n• ", preview.Warnings.Take(3));
            if (!RadioDialogWindow.Confirm(this, "Import package?", $"{preview.Summary.Stations} station(s), {preview.Summary.Songs} song(s), and {preview.Summary.Macros} macro(s) are ready to import. {conflictText}{warningText}\n\nA full automatic safety backup is created first.", "IMPORT")) return;
            if (!await PrepareConfigurationMutationAsync("Import")) return;
            var plan = await _backupTransfer.ImportAsync(_store, _config, packagePath, options);
            _config = plan.ProposedConfiguration;
            RefreshCollections(); RefreshLibrary(); RefreshSetupWizard(); RefreshRecentBackups(); RegisterHotkeys();
            Footer.Text = $"IMPORT COMPLETE · {plan.Summary.Stations} station(s), {plan.Summary.Songs} song(s), {plan.Summary.Macros} macro(s).";
        }
        catch (Exception error) { RadioDialogWindow.Inform(this, "Import could not be completed", error.Message); }
    }

    async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "WARDOGS Radio backup (*.wradio)|*.wradio", DefaultExt = ".wradio", InitialDirectory = Directory.Exists(_backupTransfer.BackupsDirectory) ? _backupTransfer.BackupsDirectory : null };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var package = await _backupTransfer.OpenAsync(picker.FileName);
            if (package.Manifest.PackageType != WrRadioPackageType.FullBackup)
            {
                RadioDialogWindow.Inform(this, "Not a full backup", "Restore only accepts a complete WARDOGS Radio backup. Use Import Package for a station pack or a selection.");
                return;
            }
            var summary = _backupTransfer.BuildImportPlan(new AppConfiguration { Profile = new RadioProfile(), MusicLibrary = new MusicLibrary() }, package,
                new WrRadioImportOptions { Contents = WrRadioContent.All, LibraryConflictResolution = LibraryConflictResolution.ReplaceExisting, StationConflictResolution = StationConflictResolution.ReplaceExisting }).Summary;
            if (!RadioDialogWindow.Confirm(this, "Restore backup?", $"This replaces the active configuration with {summary.Stations} station(s), {summary.Songs} song(s), and {summary.Macros} macro(s). A safety backup of the current configuration is created first.", "RESTORE")) return;
            if (!await PrepareConfigurationMutationAsync("Restore")) return;
            var plan = await _backupTransfer.RestoreAsync(_store, _config, picker.FileName);
            _config = plan.ProposedConfiguration;
            RefreshCollections(); RefreshLibrary(); RefreshSetupWizard(); RefreshRecentBackups(); RegisterHotkeys();
            Footer.Text = "RESTORE COMPLETE · The previous configuration was saved as a safety backup.";
        }
        catch (Exception error) { RadioDialogWindow.Inform(this, "Restore could not be completed", error.Message); }
    }
    void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        var launcher = Path.Combine(AppContext.BaseDirectory, "WARDOGS Radio Launcher.exe");
        if (!File.Exists(launcher)) { RadioDialogWindow.Inform(this, "Launcher unavailable", "Updates are available from the installed WARDOGS Radio Launcher."); return; }
        Process.Start(new ProcessStartInfo(launcher) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
        _exitRequested = true;
        Close();
    }
    void Diagnostics_Click(object s, RoutedEventArgs e) { Show(DiagnosticsView, "DIAGNOSTICS", "Check what's connected", DiagnosticsNav); RunDiagnostics(); }
    void Setup_Click(object s, RoutedEventArgs e) { Show(SetupView, "SETUP WIZARD", "Get your audio ready", SetupNav); _setupStep = 0; ShowSetupStep(); RefreshSetupWizard(); }

    void SetupBack_Click(object sender, RoutedEventArgs e) { _setupStep = Math.Max(0, _setupStep - 1); ShowSetupStep(); }
    void SetupNext_Click(object sender, RoutedEventArgs e) { _setupStep = Math.Min(9, _setupStep + 1); ShowSetupStep(); }

    void ShowSetupStep()
    {
        var steps = new[] { SetupStepSystem, SetupStepConnect, SetupStepDevices, SetupStepMonitor, SetupStepRouting, SetupStepVerify, SetupStepProviders, SetupStepStations, SetupStepControls, SetupStepFinish };
        var titles = new[] { "SYSTEM CHECK", "CONNECT VOICEMEETER", "MICROPHONE", "LISTENING OUTPUT", "GAME VOICE OUTPUT", "SIGNAL CHAIN TEST", "MUSIC SERVICES", "STATIONS", "CONTROLS", "READY CHECK" };
        for (var index = 0; index < steps.Length; index++) steps[index].Visibility = index == _setupStep ? Visibility.Visible : Visibility.Collapsed;
        SetupProgress.Value = _setupStep + 1;
        SetupStepTitle.Text = $"STEP {_setupStep + 1} OF {steps.Length} · {titles[_setupStep]}";
        SetupBackButton.IsEnabled = _setupStep > 0;
        SetupNextButton.IsEnabled = _setupStep < steps.Length - 1;
        SetupView.ScrollToTop();
        RefreshSetupWizard();
    }

    async void Station_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.Tag is not Station station) return;
        try { await ActivateAsync(station); }
        catch (Exception error) { Footer.Text = $"COULD NOT TUNE {station.Name.ToUpperInvariant()} · {error.Message}"; }
    }

    async void TuneSelected_Click(object s, RoutedEventArgs e)
    {
        if (StationList.SelectedItem is Station station) await ActivateAsync(station);
        else Footer.Text = "SELECT A STATION TO TUNE.";
    }

    async Task ActivateAsync(Station station, bool forceCrossfade = false, TimeSpan? fadeDuration = null,
        TransitionCurve? fadeCurve = null, bool startPaused = false)
    {
        if (station.ProviderId.Equals("mpv", StringComparison.OrdinalIgnoreCase))
        {
            _localStartupForensics = new LocalPlaybackForensics(station.Name);
            _localStartupForensics.Mark("T0 station click");
        }
        _transitionCancellation?.Cancel();
        await _activationGate.WaitAsync();
        try
        {
        if (!station.Enabled) { Footer.Text = $"{station.Name.ToUpperInvariant()} IS DISABLED."; return; }
        if (_active?.Id == station.Id && (_mpvProvider is not null ||
            _externalProvider?.Snapshot.Health == ProviderHealth.Ready || station.ProviderId == "youtube" && _youtubePlayerReady))
        {
            if (!station.Runtime.WasPlaying && !startPaused) await ToggleActiveAsync();
            return;
        }
        if (station.ProviderId.Equals("youtube", StringComparison.OrdinalIgnoreCase))
        {
            _youtubeStartupForensics = new YouTubeStartupForensics(station.Name);
            _youtubeStartupForensics.Mark("T0 station selected");
        }
        if (await TryCrossfadeAsync(station, forceCrossfade, fadeDuration, fadeCurve)) return;
        var old = _active;
        if (old is not null)
        {
            if (old.ProviderId == "youtube" && _youtubeReady)
            {
                await SilenceYouTubeForRouteTeardownAsync();
                await StopB1AuditionAsync("Normal headset listening restored.");
                YouTubeView.CoreWebView2.Navigate("about:blank");
                _youtubePlayerReady = false;
                _youtubeHasLiveTimeline = false;
                _youtubeInstanceToken = null;
                _youtubePendingResumeSeconds = null;
                if (station.ProviderId != "youtube")
                {
                    try { await StopGameOutputAsync(); }
                    finally { EndYouTubeRoute("Station switch continued, but the previous YouTube route still needs restoration."); }
                }
            }
            if (_mpvProvider is { } previous)
            {
                try
                {
                    var snapshot = await previous.RefreshAsync();
                    old.Runtime.PositionSeconds = snapshot.PositionSeconds;
                    old.Runtime.SequenceIndex = Math.Clamp(previous.CurrentPlaylistIndex, 0, Math.Max(0, previous.LoadedFiles.Count - 1));
                    if (snapshot.DurationSeconds is > 0 && previous.LoadedFiles.Count > old.Runtime.SequenceIndex)
                        CacheDuration(previous.LoadedFiles[old.Runtime.SequenceIndex], snapshot.DurationSeconds.Value);
                }
                catch (Exception error) { Footer.Text = "Could not capture the previous station position: " + error.Message; }
            }
            if (_pendingOrderRefresh.Remove(old.Id))
            {
                old.Runtime.SequenceIndex = 0;
                old.Runtime.PositionSeconds = 0;
                old.Runtime.VirtualStartUtc = null;
            }
            else old.Runtime.VirtualStartUtc = (old.ModeOverride ?? _config.DefaultPlaybackMode) == PlaybackMode.Radio ? DateTimeOffset.UtcNow : null;
            old.Runtime.IsOnAir = false;
            old.Runtime.WasPlaying = false;
        }
        _active = station;
        _localStartupForensics?.Mark("T1 provider setup started");
        _localPlaylistEnded = false;
        station.Runtime.IsOnAir = false;
        station.Runtime.WasPlaying = false;
        PlayButton.Content = "▶  PLAY";
        await StopGameOutputAsync();
        if (_mpvProvider is not null)
        {
            await _mpvProvider.DisposeAsync();
            _mpvProvider = null;
        }
        if (!station.ProviderId.Equals("external-audio", StringComparison.OrdinalIgnoreCase) && _externalProvider is not null)
        {
            await _externalProvider.DisposeAsync();
            _externalProvider = null;
        }
        _youtubeStartupForensics?.Mark("T1 previous output teardown complete");
        RefreshActiveStationPresentation(station);
        TransitionText.Text = old is null ? "READY" : $"TUNED FROM {old.Name.ToUpperInvariant()} → {station.Name.ToUpperInvariant()}";
        MusicRouteText.Text = $"{ProviderBadge.Text} · {station.Name}";
        if (station.ProviderId.Equals("youtube", StringComparison.OrdinalIgnoreCase))
        {
            // WebView2 is hosted by the Dashboard. Keep its player visible when a
            // global macro or station-list action tunes YouTube from another page.
            // This changes the in-app page, but does not activate/raise the window.
            if (DashboardView.Visibility != Visibility.Visible)
                Show(DashboardView, "DASHBOARD", "Your stations and sound", DashboardNav);
            NativePanel.Visibility = Visibility.Collapsed;
            await LoadYouTubeAsync(station, startPaused);
        }
        else
        {
            YouTubeView.Visibility = Visibility.Collapsed;
            NativePanel.Visibility = Visibility.Visible;
            if (station.ProviderId.Equals("external-audio", StringComparison.OrdinalIgnoreCase)) await LoadExternalAsync(station);
            else if (station.ProviderId.Equals("mpv", StringComparison.OrdinalIgnoreCase)) await LoadMpvAsync(station);
            else if (station.ProviderId is "applemusic" or "soundcloud")
            {
                NativeStatus.Text = $"{ProviderBadge.Text} NEEDS SETUP";
                NativeHint.Text = "Connect your account in Settings to play this station.";
                Footer.Text = $"{ProviderBadge.Text} needs an account connection.";
            }
            else
            {
                NativeStatus.Text = $"{station.Name.ToUpperInvariant()} PROVIDER UNAVAILABLE";
                NativeHint.Text = "This source cannot be played by the current application configuration.";
            }
        }
        UpdateRepeatButton();
        if (_mpvProvider is { } readyMpv)
        {
            var shouldPlay = await RestoreMpvPositionAsync(station, readyMpv);
            await LoadGameOutputAsync(station, readyMpv);
            if (shouldPlay) await ToggleActiveAsync();
        }
        else if (_externalProvider?.Snapshot.Health == ProviderHealth.Ready)
            await ToggleActiveAsync();
        RefreshCollections();
        await _store.SaveAsync(_config);
        }
        finally { _activationGate.Release(); }
    }

    async Task<bool> TryCrossfadeAsync(Station station, bool forceCrossfade, TimeSpan? fadeDuration, TransitionCurve? fadeCurve)
    {
        var outgoingStation = _active;
        var outgoing = _mpvProvider;
        if (!(forceCrossfade || _config.CrossfadeEnabled) || outgoingStation?.ProviderId != "mpv" ||
            station.ProviderId != "mpv" || outgoing is null || !outgoing.Snapshot.IsPlaying ||
            (string.IsNullOrWhiteSpace(station.Source) && (station.PlaylistFiles?.Count ?? 0) == 0)) return false;

        using var cancellation = new CancellationTokenSource();
        _transitionCancellation = cancellation;
        MpvProvider? incoming = null;
        MpvProvider? incomingGame = null;
        var switched = false;
        var outgoingGame = _gameMpvProvider;
        var outgoingGain = 0d;
        var outgoingGameGain = 0d;
        try
        {
            var token = cancellation.Token;
            outgoingGain = await outgoing.ReadVolumeAsync(token);
            if (outgoingGame is not null) outgoingGameGain = await outgoingGame.ReadVolumeAsync(token);
            if (station.Shuffle && station.ShuffleSeed is null) station.ShuffleSeed = Random.Shared.Next();
            incoming = new MpvProvider(new MpvLocator(), _config.MpvPath, _config.MpvAudioDeviceName, LocalHeadsetLoudnessCalibrationDb);
            await incoming.LoadAsync(station, token);
            await incoming.SetVolumeAsync(0, token);
            if (!await RestoreMpvPositionAsync(station, incoming))
            {
                return false;
            }
            if (_config.GameMpvAudioDeviceName is { Length: > 0 } gameDevice &&
                _config.MpvAudioDeviceName is { Length: > 0 } headsetDevice && gameDevice != headsetDevice)
            {
                try
                {
                    incomingGame = new MpvProvider(new MpvLocator(), _config.MpvPath, gameDevice);
                    await incomingGame.LoadAsync(station, token);
                    await incomingGame.SetVolumeAsync(0, token);
                    var position = await incoming.RefreshAsync(token);
                    await incomingGame.SelectTrackAsync(incoming.CurrentPlaylistIndex, position.PositionSeconds, token);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    if (incomingGame is not null) await incomingGame.DisposeAsync();
                    incomingGame = null;
                    GameMpvOutputState.Text = "Game music output unavailable: " + error.Message;
                }
            }
            await incoming.PlayAsync(token);
            if (incomingGame is not null) await incomingGame.PlayAsync(token);
            var seconds = Math.Clamp((fadeDuration ?? TimeSpan.FromSeconds(_config.CrossfadeSeconds)).TotalSeconds, .05, 10);
            var curve = fadeCurve ?? _config.Curve;
            const int steps = 20;
            for (var step = 1; step <= steps; step++)
            {
                token.ThrowIfCancellationRequested();
                var gains = TransitionMath.Gains((double)step / steps, curve);
                await outgoing.SetVolumeAsync(outgoingGain * gains.Outgoing, token);
                if (outgoingGame is not null) await outgoingGame.SetVolumeAsync(outgoingGameGain * gains.Outgoing, token);
                await incoming.SetVolumeAsync(_config.MasterVolume * station.Volume * gains.Incoming, token);
                if (incomingGame is not null) await incomingGame.SetVolumeAsync(GamePlayerGain(station) * gains.Incoming, token);
                TransitionText.Text = $"CROSSFADING · {Math.Round(100d * step / steps):0}%";
                if (step < steps) await Task.Delay(TimeSpan.FromSeconds(seconds / steps), token);
            }
            var outgoingPosition = await outgoing.RefreshAsync(token);
            outgoingStation.Runtime.PositionSeconds = outgoingPosition.PositionSeconds;
            outgoingStation.Runtime.SequenceIndex = Math.Clamp(outgoing.CurrentPlaylistIndex, 0, Math.Max(0, outgoing.LoadedFiles.Count - 1));
            outgoingStation.Runtime.VirtualStartUtc = (outgoingStation.ModeOverride ?? _config.DefaultPlaybackMode) == PlaybackMode.Radio ? DateTimeOffset.UtcNow : null;
            outgoingStation.Runtime.IsOnAir = false;
            outgoingStation.Runtime.WasPlaying = false;
            switched = true;
            _active = station;
            _localPlaylistEnded = false;
            station.Runtime.IsOnAir = true;
            station.Runtime.WasPlaying = true;
            station.Runtime.VirtualStartUtc = null;
            _mpvProvider = incoming;
            _gameMpvProvider = incomingGame;
            GameMpvOutputState.Text = incomingGame is not null
                ? $"Game music player ready on {_config.GameMpvAudioDeviceName}. Check the Voicemeeter B1 meter."
                : _config.GameMpvAudioDeviceName is null ? "Game music output is off. Only your headset plays." :
                    "Game music output could not start. Headset playback continues.";
            incoming = null;
            incomingGame = null;
            try { await outgoing.DisposeAsync(); }
            catch (Exception error) { Footer.Text = "Previous headset player did not close cleanly: " + error.Message; }
            if (outgoingGame is not null)
            {
                try { await outgoingGame.DisposeAsync(); }
                catch (Exception error) { Footer.Text = "Previous game player did not close cleanly: " + error.Message; }
            }
            RefreshActiveStationPresentation(station);
            NativeStatus.Text = _mpvProvider.Snapshot.Track?.Title ?? station.Name;
            NativeHint.Text = "Playing · Check your headphones and voice chat output.";
            NativePanel.Visibility = Visibility.Visible;
            YouTubeView.Visibility = Visibility.Collapsed;
            PlayButton.Content = "Ⅱ  PAUSE";
            TransitionText.Text = $"TUNED FROM {outgoingStation.Name.ToUpperInvariant()} → {station.Name.ToUpperInvariant()}";
            UpdateRepeatButton();
            RefreshCollections();
            await _store.SaveAsync(_config);
            return true;
        }
        catch (OperationCanceledException)
        {
            TransitionText.Text = "TRANSITION CANCELLED";
            return true;
        }
        catch (Exception error)
        {
            Footer.Text = switched ? $"TUNED {station.Name.ToUpperInvariant()}, BUT COULD NOT FINISH THE TRANSITION · {error.Message}" :
                $"COULD NOT TUNE {station.Name.ToUpperInvariant()} · {error.Message}. Previous station stays on air.";
            TransitionText.Text = switched ? "TRANSITION NEEDS ATTENTION" : "TRANSITION FAILED · PREVIOUS STATION RESTORED";
            return true;
        }
        finally
        {
            if (!switched && outgoing is { Snapshot.IsPlaying: true })
            {
                try { await outgoing.SetVolumeAsync(outgoingGain); } catch { }
            }
            if (!switched && outgoingGame is { Snapshot.IsPlaying: true })
            {
                try { await outgoingGame.SetVolumeAsync(outgoingGameGain); } catch { }
            }
            if (ReferenceEquals(_transitionCancellation, cancellation)) _transitionCancellation = null;
            if (incoming is not null)
            {
                try { await incoming.DisposeAsync(); }
                catch (Exception error) { Footer.Text = "Could not close the cancelled incoming player: " + error.Message; }
            }
            if (incomingGame is not null)
            {
                try { await incomingGame.DisposeAsync(); }
                catch (Exception error) { Footer.Text = "Could not close the cancelled game player: " + error.Message; }
            }
        }
    }

    void RefreshActiveStationPresentation(Station station)
    {
        NowStation.Text = station.Name.ToUpperInvariant();
        NowDetail.Text = station.PlaylistSongs.Count > 0 ? $"Playlist · {station.PlaylistSongs.Count} songs · {(station.Shuffle ? "shuffled order" : "play in order")}" :
            station.PlaylistFiles?.Count > 0 ? $"Local playlist · {station.PlaylistFiles.Count} tracks · {(station.Shuffle ? "shuffle" : "play in order")}" :
            string.IsNullOrWhiteSpace(station.Source) ? "Choose where this station gets its music." : station.Source;
        ProviderBadge.Text = new ProviderNameConverter().Convert(station.ProviderId, typeof(string), null!, System.Globalization.CultureInfo.CurrentCulture).ToString()!.ToUpperInvariant();
        HeroIcon.Data = IconCatalog.Get(station.IconId).Shape;
        try { HeroIcon.Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(station.AccentColor)!; }
        catch { HeroIcon.Fill = (System.Windows.Media.Brush)FindResource("OliveBrush"); }
        ConfigureStationButton.Visibility = string.IsNullOrWhiteSpace(station.Source) && (station.PlaylistFiles?.Count ?? 0) == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConfigureStationButton.Content = "ADD A SOURCE";
        MusicRouteText.Text = $"{ProviderBadge.Text} · {station.Name}";
    }

    static string? DurationKey(string path)
    {
        if (!File.Exists(path) || Path.GetExtension(path).Equals(".m3u", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".m3u8", StringComparison.OrdinalIgnoreCase)) return null;
        var info = new FileInfo(path);
        return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    void CacheDuration(string path, double seconds)
    {
        var key = DurationKey(path);
        if (key is not null && double.IsFinite(seconds) && seconds > 0) _config.LocalDurationCache[key] = seconds;
    }

    async Task<double?> LocalDurationAsync(string path)
    {
        var key = DurationKey(path);
        if (key is null) return null;
        if (_config.LocalDurationCache.TryGetValue(key, out var seconds) && seconds > 0) return seconds;
        try
        {
            await using var probe = new MpvProvider(new MpvLocator(), _config.MpvPath);
            await probe.LoadAsync(new Station { Source = path });
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var snapshot = await probe.RefreshAsync();
                if (snapshot.DurationSeconds is > 0)
                {
                    CacheDuration(path, snapshot.DurationSeconds.Value);
                    return snapshot.DurationSeconds.Value;
                }
                await Task.Delay(50);
            }
        }
        catch (Exception error) { Footer.Text = "Could not read the track duration: " + error.Message; }
        return null;
    }

    async Task<bool> RestoreMpvPositionAsync(Station station, MpvProvider provider)
    {
        var runtime = station.Runtime;
        var files = provider.LoadedFiles;
        if (files.Count == 0) return false;
        if (files.Any(file => DurationKey(file) is null))
        {
            NativeHint.Text = "This live URL or playlist file cannot restore an exact track position; playing from its current start.";
            runtime.VirtualStartUtc = null;
            return true;
        }
        var sequence = station.PlaylistSongs.Count == files.Count
            ? station.PlaylistSongs.Select(song => song.Id.ToString("N")).ToList() : files.ToList();
        if (!runtime.Sequence.SequenceEqual(sequence, StringComparer.OrdinalIgnoreCase))
        {
            runtime.Sequence = sequence;
            runtime.SequenceIndex = 0;
            runtime.PositionSeconds = station.PlaylistSongs.Count > 0 ? station.PlaylistSongs[0].StartSeconds : 0;
            runtime.VirtualStartUtc = null;
        }
        var index = Math.Clamp(runtime.SequenceIndex, 0, files.Count - 1);
        var mode = station.ModeOverride ?? _config.DefaultPlaybackMode;
        var seconds = StationReturnPolicy.StartingSeconds(mode, runtime.PositionSeconds);
        if (station.PlaylistSongs.Count == files.Count)
            seconds = Math.Max(seconds, station.PlaylistSongs[index].StartSeconds);
        if (mode == PlaybackMode.Radio && runtime.VirtualStartUtc is { } leftAt)
        {
            var durations = new List<double>();
            for (var songIndex = 0; songIndex < files.Count; songIndex++)
            {
                var duration = station.PlaylistSongs.Count == files.Count && station.PlaylistSongs[songIndex].EndSeconds is { } end
                    ? end - station.PlaylistSongs[songIndex].StartSeconds
                    : await LocalDurationAsync(files[songIndex]) - (station.PlaylistSongs.Count == files.Count ? station.PlaylistSongs[songIndex].StartSeconds : 0);
                if (duration is null)
                {
                    Footer.Text = "RADIO POSITION UNAVAILABLE FOR THIS SOURCE · Resuming its saved position.";
                    break;
                }
                durations.Add(duration.Value);
            }
            if (durations.Count == files.Count)
            {
                var cursor = MpvStationTimeline.Advance(durations, index,
                    seconds - (station.PlaylistSongs.Count == files.Count ? station.PlaylistSongs[index].StartSeconds : 0),
                    (DateTimeOffset.UtcNow - leftAt).TotalSeconds, station.EffectiveRepeatMode);
                index = cursor.Index;
                seconds = cursor.Seconds + (station.PlaylistSongs.Count == files.Count ? station.PlaylistSongs[index].StartSeconds : 0);
                if (cursor.Ended)
                {
                    await provider.SelectTrackAsync(index, Math.Max(0, seconds - .01));
                    NativeHint.Text = "PLAYLIST ENDED · Repeat is off. Choose Repeat Playlist or press Play to restart.";
                    runtime.VirtualStartUtc = null;
                    return false;
                }
            }
        }
        runtime.VirtualStartUtc = null;
        await provider.SelectTrackAsync(index, seconds);
        runtime.SequenceIndex = index;
        runtime.PositionSeconds = seconds;
        return true;
    }

    async Task LoadExternalAsync(Station station)
    {
        try
        {
            if (_externalProvider is not null) await _externalProvider.DisposeAsync();
            _externalProvider = new ExternalAudioProvider(new PowerShellMediaSessionBackend(), new ExternalSourceIdentity(station.Source, null, null, station.Source));
            await _externalProvider.LoadAsync(station);
            var snapshot = _externalProvider.Snapshot;
            NativeStatus.Text = snapshot.Health == ProviderHealth.Unavailable ? "EXTERNAL APP UNAVAILABLE · Waiting for its media session." : $"{snapshot.Track?.Title ?? station.Name} · {snapshot.Track?.Artist ?? "External Audio"}";
            Footer.Text = snapshot.Health == ProviderHealth.Unavailable ? "Music app not found. Start it, then scan again." : "Music app ready for playback controls. Check its sound route separately.";
        }
        catch (Exception ex) { NativeStatus.Text = "EXTERNAL SESSION ERROR"; Footer.Text = ex.Message; }
    }

    async Task LoadMpvAsync(Station station)
    {
        MusicLibraryService.EnsureStationLibrary(_config, station);
        MusicLibraryService.MaterializeStationPlaylist(_config, station);
        if (string.IsNullOrWhiteSpace(station.Source) && (station.PlaylistFiles?.Count ?? 0) == 0)
        {
            NativeStatus.Text = $"{station.Name.ToUpperInvariant()} NEEDS A SOURCE";
            NativeHint.Text = "Choose a file, playlist, folder, or stream URL in Stations.";
            ConfigureStationButton.Visibility = Visibility.Visible;
            return;
        }
        if (new MpvLocator().Find(_config.MpvPath) is null)
        {
            NativeStatus.Text = "MUSIC PLAYER NOT FOUND";
            NativeHint.Text = "Choose mpv.exe in Settings to play this station.";
            ConfigureStationButton.Visibility = Visibility.Visible;
            ConfigureStationButton.Content = "REPAIR STATION";
            return;
        }
        try
        {
            if (station.PlaylistSongs.Count == 0 && station.PlaylistFiles.Count == 0 && Directory.Exists(station.Source))
            {
                station.PlaylistFiles = LocalMediaPlaylist.FromDirectory(station.Source, false).ToList();
                // A folder is expanded only when the station is opened. Register the
                // resulting files immediately so this is never a runtime-only playlist.
                MusicLibraryService.EnsureStationLibrary(_config, station);
                MusicLibraryService.MaterializeStationPlaylist(_config, station);
            }
            if (station.Shuffle && station.ShuffleSeed is null) station.ShuffleSeed = Random.Shared.Next();
            SongPlaylist.EnsureLocal(station);
            if (station.PlaylistSongs.Count > 0)
            {
                station.PlaylistFiles = station.PlaylistSongs.Select(song => song.Source).ToList();
                station.Source = station.PlaylistFiles[0];
            }
            _mpvProvider = new MpvProvider(new MpvLocator(), _config.MpvPath, _config.MpvAudioDeviceName, LocalHeadsetLoudnessCalibrationDb);
            _localStartupForensics?.RecordHeadsetSetup(_config.MpvAudioDeviceName, _config.MasterVolume * station.Volume);
            await _mpvProvider.LoadAsync(station);
            _localStartupForensics?.Mark("MPV process started / media loaded");
            await _mpvProvider.SetVolumeAsync(_config.MasterVolume * station.Volume);
            _localStartupForensics?.Mark("audio device selected / requested volume set");
            _localStartupForensics?.RecordReportedState(
                await _mpvProvider.ReadVolumeAsync(), await _mpvProvider.ReadMuteAsync(), _mpvProvider.Snapshot.IsPlaying);
            _localStartupForensics?.RecordReportedDevice(await _mpvProvider.ReadAudioDeviceAsync());
            NativeStatus.Text = _mpvProvider.Snapshot.Track?.Title ?? station.Name;
            NativeHint.Text = "Starting playback…";
            Footer.Text = $"{station.Name} is ready. Check your headphones and voice chat output.";
        }
        catch (Exception ex)
        {
            if (_mpvProvider is not null) await _mpvProvider.DisposeAsync();
            _mpvProvider = null;
            NativeStatus.Text = "STATION UNAVAILABLE";
            NativeHint.Text = ex.Message;
            ConfigureStationButton.Visibility = Visibility.Visible;
            ConfigureStationButton.Content = "REPAIR STATION";
            Footer.Text = $"{station.Name} could not load: {ex.Message}";
        }
    }

    async Task StopGameOutputAsync()
    {
        if (_youtubeGameFeed is { } youtubeFeed)
        {
            _youtubeGameFeed = null;
            await youtubeFeed.DisposeAsync();
        }
        if (_gameMpvProvider is not { } game) return;
        _gameMpvProvider = null;
        await game.DisposeAsync();
    }

    async Task StartYouTubeGameFeedAsync(Station station)
    {
        if (!ReferenceEquals(_active, station) || !station.ProviderId.Equals("youtube", StringComparison.OrdinalIgnoreCase)) return;
        _youtubeGameFeedError = null;
        if (string.IsNullOrWhiteSpace(_config.GameMpvAudioDeviceName))
        {
            _youtubeGameFeedError = "Choose a Voicemeeter game-music output in Audio & Routing.";
            _youtubeStartupForensics?.Mark("T6 B1 unavailable");
            return;
        }
        if (_config.GameMpvAudioDeviceName == _config.MpvAudioDeviceName)
        {
            _youtubeGameFeedError = "Game and headset outputs must be different devices.";
            _youtubeStartupForensics?.Mark("T6 B1 unavailable");
            return;
        }
        try
        {
            var feed = await YouTubeGameFeed.StartAsync(
                (uint)YouTubeView.CoreWebView2.BrowserProcessId,
                _config.GameMpvAudioDeviceName,
                GamePlayerGain(station));
            // Creating the process-loopback capture is asynchronous. Do not let a
            // completed startup for a station that was just retuned attach itself to
            // the new station's game path.
            if (!ReferenceEquals(_active, station) || !station.ProviderId.Equals("youtube", StringComparison.OrdinalIgnoreCase))
            {
                await feed.DisposeAsync();
                return;
            }
            _youtubeGameFeed = feed;
            _youtubeStartupForensics?.Mark("T6 B1 feed ready");
            GameMpvOutputState.Text = "YouTube capture is feeding the selected Voicemeeter music input. Verify its live strip and B1 meters.";
        }
        catch (Exception error)
        {
            if (!ReferenceEquals(_active, station) || !station.ProviderId.Equals("youtube", StringComparison.OrdinalIgnoreCase)) return;
            _youtubeGameFeedError = error.Message;
            _youtubeStartupForensics?.Mark("T6 B1 failed");
            GameMpvOutputState.Text = "YouTube game feed unavailable: " + error.Message;
            Footer.Text = "YOUTUBE HEADSET MAY STILL PLAY · GAME FEED UNAVAILABLE · " + error.Message;
        }
    }

    async Task LoadGameOutputAsync(Station station, MpvProvider headset)
    {
        await StopGameOutputAsync();
        var device = _config.GameMpvAudioDeviceName;
        if (string.IsNullOrWhiteSpace(device))
        {
            GameMpvOutputState.Text = "Game music output is off. Only your headset plays.";
            return;
        }
        if (string.IsNullOrWhiteSpace(_config.MpvAudioDeviceName))
        {
            GameMpvOutputState.Text = "Choose a specific headset output before enabling game music, so the paths cannot accidentally overlap.";
            return;
        }
        if (device == (_config.MpvAudioDeviceName ?? "auto"))
        {
            GameMpvOutputState.Text = "Game output is off because it matches the headset output. Choose a different device.";
            return;
        }
        MpvProvider? game = null;
        try
        {
            game = new MpvProvider(new MpvLocator(), _config.MpvPath, device);
            await game.LoadAsync(station);
            await game.SetVolumeAsync(GamePlayerGain(station));
            var playback = await headset.RefreshAsync();
            await game.SelectTrackAsync(Math.Clamp(headset.CurrentPlaylistIndex, 0, game.LoadedFiles.Count - 1), playback.PositionSeconds);
            _gameMpvProvider = game;
            _localStartupForensics?.RecordGameDevice(device, await game.ReadAudioDeviceAsync());
            _localStartupForensics?.Mark("game MPV process started / media loaded");
            GameMpvOutputState.Text = $"Game music player ready on {device}. Check its Voicemeeter strip and game bus.";
        }
        catch (Exception error)
        {
            if (game is not null) await game.DisposeAsync();
            GameMpvOutputState.Text = "Game music output unavailable: " + error.Message;
            Footer.Text = "HEADSET MUSIC READY · GAME MUSIC NOT CONNECTED · " + error.Message;
        }
    }

    async Task SyncGameToHeadsetAsync(MpvProvider headset, PlaybackSnapshot? knownPlayback = null)
    {
        if (_gameMpvProvider is not { } game) return;
        try
        {
            var headsetState = knownPlayback ?? await headset.RefreshAsync();
            var gameState = await game.RefreshAsync();
            if (headset.CurrentPlaylistIndex != game.CurrentPlaylistIndex)
                await game.SelectTrackAsync(headset.CurrentPlaylistIndex, headsetState.PositionSeconds);
            else if (headsetState.IsPlaying && Math.Abs(gameState.PositionSeconds - headsetState.PositionSeconds) > .7)
                await game.SeekAsync(headsetState.PositionSeconds);
            if (!headsetState.IsPlaying && gameState.IsPlaying) await game.PauseAsync();
        }
        catch (Exception error)
        {
            await StopGameOutputAsync();
            GameMpvOutputState.Text = "Game music stopped: " + error.Message;
            Footer.Text = "HEADSET MUSIC CONTINUES · GAME MUSIC STOPPED · " + error.Message;
        }
    }

    async Task LoadYouTubeAsync(Station station, bool startPaused)
    {
        _youtubeStartupForensics ??= new YouTubeStartupForensics(station.Name);
        MusicLibraryService.EnsureStationLibrary(_config, station);
        MusicLibraryService.MaterializeStationPlaylist(_config, station);
        // Reset before every early return: no prior player token, progress, or
        // persisted resume position may masquerade as this station's live state.
        ResetYouTubePresentationForLoad(station, startPaused);
        var result = YouTubeUrl.Normalize(station.Source);
        if (!result.IsValid)
        {
            _youtubePlayerErrorDetail = result.Message;
            YouTubePlaceholderTitle.Text = "YOUTUBE SOURCE ERROR";
            YouTubePlaceholderDetail.Text = result.Message;
            Footer.Text = "YOUTUBE SOURCE ERROR · " + result.Message;
            RefreshSharedPlaybackPresentation();
            return;
        }
        if (!await EnsureYouTubeReadyAsync())
        {
            YouTubePlaceholderTitle.Text = "WEBVIEW2 RUNTIME REQUIRED";
            YouTubePlaceholderDetail.Text = "Install or repair WebView2, then retry the station.";
            Footer.Text = "WEBVIEW2 RUNTIME IS REQUIRED FOR OFFICIAL YOUTUBE PLAYBACK.";
            return;
        }
        if (!EnsureYouTubeRouteHealthy(out var recoveryDetail))
        {
            ShowYouTubeRouteRepairRequired(recoveryDetail);
            Footer.Text = "YOUTUBE ROUTE NEEDS RESTORATION · " + recoveryDetail;
            return;
        }
        if (!_youtubeRoute.IsActive)
        {
            try
            {
                _youtubeRoute.Begin(_config.MonitorDeviceId ?? "", 1);
                _youtubeStartupForensics.Mark($"T2 temporary route begin complete ({_youtubeRoute.LastBeginDuration.TotalMilliseconds:0} ms)");
            }
            catch (Exception error)
            {
                if (_youtubeRoute.IsActive)
                {
                    _youtubeRouteRecoveryBlocked = true;
                    _youtubeHeadsetRouteError = error.Message;
                    ShowYouTubeRouteRepairRequired(error.Message);
                    Footer.Text = "YOUTUBE ROUTE NEEDS RESTORATION · " + error.Message;
                    return;
                }
                _youtubeHeadsetRouteError = error.Message;
            }
        }
        else
        {
            _youtubeRoute.SetHeadsetGain(1);
            _youtubeStartupForensics.Mark("T2 temporary route active");
        }
        var returnMode = station.ModeOverride ?? _config.DefaultPlaybackMode;
        var returnSeconds = YouTubeReturnPolicy.StartingSeconds(returnMode, station.Runtime.PositionSeconds, station.Runtime.DurationSeconds);
        if (station.PlaylistSongs.Count > 0)
            returnSeconds = Math.Max(returnSeconds, station.PlaylistSongs[Math.Clamp(station.Runtime.SequenceIndex, 0, station.PlaylistSongs.Count - 1)].StartSeconds);
        _youtubePendingResumeSeconds = returnSeconds > 2 ? returnSeconds : null;
        Progress.Value = 0;
        TimeText.Text = "00:00 / --:--";
        var uri = new Uri(result.CanonicalSource!);
        var start = returnSeconds > 0 ? $"&start={Math.Floor(returnSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "";
        _youtubeInstanceToken = Guid.NewGuid().ToString("N");
        var repeat = station.EffectiveRepeatMode.ToString().ToLowerInvariant();
        _youtubeStartupForensics.Mark("T3 WebView navigate");
        // The raw WebView only becomes visible at the same point that it has a
        // real WARDOGS player navigation. Route and source failures keep the
        // themed placeholder on screen instead of about:blank white.
        YouTubePlaceholder.Visibility = Visibility.Collapsed;
        YouTubeView.Visibility = Visibility.Visible;
        YouTubeView.CoreWebView2.Navigate("https://wardogs-radio.example/youtube-player.html" + uri.Query + start + $"&repeat={repeat}&instance={_youtubeInstanceToken}");
        Footer.Text = _youtubeHeadsetRouteError is null
            ? "YouTube player loading · " + result.Message
            : "YOUTUBE HEADSET ROUTE UNAVAILABLE · " + _youtubeHeadsetRouteError + " B1-only hold testing is unavailable.";
    }

    async void AddStation_Click(object s, RoutedEventArgs e)
    {
        var editor = new StationEditorWindow(library: _config.MusicLibrary) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not Station station) return;
        station.Order = _config.Profile.Stations.Count;
        _config.Profile.Stations.Add(station);
        MusicLibraryService.ReconcileStationLibrary(_config, station);
        MusicLibraryService.MaterializeStationPlaylist(_config, station);
        await _store.SaveAsync(_config);
        RefreshCollections();
        StationList.SelectedItem = station;
        Show(StationsView, "STATIONS", "Choose or create a station", StationsNav);
        Footer.Text = $"{station.Name} created. Select it to play.";
    }

    async void EditStation_Click(object s, RoutedEventArgs e)
    {
        if (StationList.SelectedItem is not Station station) { Footer.Text = "SELECT A STATION TO EDIT."; return; }
        await EditStationAsync(station);
    }

    async Task EditStationAsync(Station station)
    {
        var priorProvider = station.ProviderId;
        var priorSource = station.Source;
        var priorPlaylist = (station.PlaylistFiles ?? []).ToList();
        var priorSongIds = station.PlaylistSongs.Select(song => song.Id).ToList();
        var playingSongId = ReferenceEquals(station, _active) && _mpvProvider is { } playingBeforeEdit &&
            playingBeforeEdit.CurrentPlaylistIndex >= 0 && playingBeforeEdit.CurrentPlaylistIndex < priorSongIds.Count
            ? priorSongIds[playingBeforeEdit.CurrentPlaylistIndex] : (Guid?)null;
        var priorShuffle = station.Shuffle;
        var priorSeed = station.ShuffleSeed;
        var priorVolume = station.Volume;
        var priorGameVolume = station.GameVolume;
        var priorRepeatMode = station.EffectiveRepeatMode;
        var editor = new StationEditorWindow(station, _config.MusicLibrary) { Owner = this };
        if (editor.ShowDialog() != true) return;
        MusicLibraryService.ReconcileStationLibrary(_config, station);
        MusicLibraryService.MaterializeStationPlaylist(_config, station);
        await _store.SaveAsync(_config);
        RefreshCollections();
        var currentPlaylist = station.PlaylistFiles ?? [];
        var sourceChanged = StationEditPlanner.RequiresPlaybackReload(priorProvider, priorSource, priorPlaylist, station);
        var orderChanged = station.Shuffle != priorShuffle || station.ShuffleSeed != priorSeed ||
            !priorPlaylist.SequenceEqual(currentPlaylist, StringComparer.OrdinalIgnoreCase) ||
            !priorSongIds.SequenceEqual(station.PlaylistSongs.Select(song => song.Id));
        if (ReferenceEquals(station, _active) && sourceChanged)
        {
            try
            {
                _pendingOrderRefresh.Remove(station.Id);
                station.Runtime.SequenceIndex = 0;
                station.Runtime.PositionSeconds = 0;
                station.Runtime.VirtualStartUtc = null;
                await StopGameOutputAsync();
                if (_mpvProvider is not null) { await _mpvProvider.DisposeAsync(); _mpvProvider = null; }
                if (_externalProvider is not null) { await _externalProvider.DisposeAsync(); _externalProvider = null; }
                await ActivateAsync(station);
                Footer.Text = $"{station.Name.ToUpperInvariant()} UPDATED · SOURCE RELOADED.";
            }
            catch (Exception ex) { Footer.Text = $"STATION SAVED · RELOAD FAILED · {ex.Message}"; }
        }
        else
        {
            if (ReferenceEquals(station, _active))
            {
                RefreshActiveStationPresentation(station);
                if (orderChanged && _mpvProvider is not null && station.PlaylistSongs.Count > 0)
                {
                    station.Runtime.SequenceIndex = playingSongId is { } id
                        ? Math.Max(0, station.PlaylistSongs.FindIndex(song => song.Id == id)) : 0;
                    try { await ReloadLocalPlaylistAsync(station); }
                    catch (Exception error) { Footer.Text = "STATION SAVED · PLAYLIST UPDATE FAILED · " + error.Message; return; }
                }
                if (priorRepeatMode != station.EffectiveRepeatMode && _mpvProvider is { } repeatProvider)
                {
                    try
                    {
                        await repeatProvider.SetRepeatModeAsync(station.EffectiveRepeatMode);
                        if (_gameMpvProvider is not null) await _gameMpvProvider.SetRepeatModeAsync(station.EffectiveRepeatMode);
                    }
                    catch (Exception error)
                    {
                        try { await repeatProvider.SetRepeatModeAsync(priorRepeatMode); } catch { }
                        if (_gameMpvProvider is not null) try { await _gameMpvProvider.SetRepeatModeAsync(priorRepeatMode); } catch { }
                        station.RepeatMode = priorRepeatMode;
                        station.Loop = priorRepeatMode != StationRepeatMode.Off;
                        await _store.SaveAsync(_config);
                        Footer.Text = "STATION SAVED · REPEAT CHANGE FAILED · " + error.Message;
                        return;
                    }
                }
                UpdateRepeatButton();
            }
            if (ReferenceEquals(station, _active) && _mpvProvider is { } provider && priorVolume != station.Volume)
            {
                try { await provider.SetVolumeAsync(_config.MasterVolume * station.Volume); }
                catch (Exception error) { Footer.Text = "STATION SAVED · LIVE LEVEL UPDATE FAILED · " + error.Message; return; }
            }
            if (ReferenceEquals(station, _active) && _gameMpvProvider is { } game && priorGameVolume != station.GameVolume)
            {
                try { await game.SetVolumeAsync(GamePlayerGain(station)); }
                catch (Exception error) { Footer.Text = "STATION SAVED · LIVE GAME LEVEL UPDATE FAILED · " + error.Message; return; }
            }
            Footer.Text = orderChanged && ReferenceEquals(station, _active)
                ? $"{station.Name.ToUpperInvariant()} UPDATED · NEW ORDER APPLIES NEXT TUNE; CURRENT SONG CONTINUES."
                : $"{station.Name.ToUpperInvariant()} UPDATED · PLAYBACK UNCHANGED.";
        }
    }

    async void ConfigureActiveStation_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        await EditStationAsync(_active);
    }

    void StationList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var hit = e.OriginalSource as DependencyObject;
        while (hit is not null && !ReferenceEquals(hit, StationList))
        {
            if (hit is Button { Content: "EDIT" }) break;
            hit = hit is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(hit)
                : LogicalTreeHelper.GetParent(hit);
        }
        if (hit is not Button { Content: "EDIT" }) return;
        _stationSelectionBeforeEdit = StationList.SelectedItem as Station;
        _suppressStationSelectionActivation = true;
        Dispatcher.BeginInvoke(() => _suppressStationSelectionActivation = false,
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    async void EditStationCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Station station) return;
        _suppressStationSelectionActivation = true;
        if (_stationSelectionBeforeEdit is not null) StationList.SelectedItem = _stationSelectionBeforeEdit;
        _stationSelectionBeforeEdit = null;
        _suppressStationSelectionActivation = false;
        await EditStationAsync(station);
    }

    async void ScanExternal_Click(object s, RoutedEventArgs e)
    {
        Footer.Text = "SCANNING WINDOWS MEDIA SESSIONS…";
        try
        {
            _externalSessions = await new PowerShellMediaSessionBackend().DiscoverAsync();
            ExternalSessionList.ItemsSource = _externalSessions;
            Footer.Text = _externalSessions.Count == 0 ? "NO EXTERNAL MEDIA SESSIONS · Start a supported player, then scan again." : $"{_externalSessions.Count} EXTERNAL MEDIA SESSION(S) DISCOVERED · Select one, then create the Station.";
        }
        catch (Exception ex) { Footer.Text = "EXTERNAL SESSION SCAN FAILED · " + ex.Message; }
    }

    async void CreateExternal_Click(object s, RoutedEventArgs e)
    {
        if (ExternalSessionList.SelectedItem is not ExternalSessionDescriptor session) { Footer.Text = "SELECT AN EXTERNAL MEDIA SESSION FIRST."; return; }
        var station = new Station { Name = session.ApplicationName, Description = $"External media session · {session.Track?.Title ?? "No current title"}", Glyph = "◉", IconId = "antenna", AccentColor = "#639BBC", ProviderId = "external-audio", Source = session.SourceAppId ?? session.SessionId, Order = _config.Profile.Stations.Count };
        _config.Profile.Stations.Add(station);
        await _store.SaveAsync(_config);
        RefreshCollections();
        StationList.SelectedItem = station;
        Footer.Text = $"{station.Name} added. Check its sound route before using game voice chat.";
    }

    async void Duplicate_Click(object s, RoutedEventArgs e)
    {
        if (StationList.SelectedItem is not Station source) return;
        var copy = new Station { Name = source.Name + " Copy", Description = source.Description, Glyph = source.Glyph, IconId = source.IconId, AccentColor = source.AccentColor, ProviderId = source.ProviderId, Source = source.Source, PlaylistEntries = source.PlaylistEntries.Select(entry => new StationPlaylistEntry { SongId = entry.SongId }).ToList(), Order = _config.Profile.Stations.Count, Volume = source.Volume, GameVolume = source.GameVolume, Loop = source.Loop, RepeatMode = source.EffectiveRepeatMode, Shuffle = source.Shuffle, ShuffleSeed = source.ShuffleSeed };
        _config.Profile.Stations.Add(copy);
        MusicLibraryService.MaterializeStationPlaylist(_config, copy);
        await _store.SaveAsync(_config);
        RefreshCollections();
        StationList.SelectedItem = copy;
    }

    async void Remove_Click(object s, RoutedEventArgs e)
    {
        if (StationList.SelectedItem is not Station station) return;
        if (!RadioDialogWindow.Confirm(this, "Remove station?", $"Remove {station.Name}? Its audio files will stay on your computer.", "REMOVE STATION")) return;
        _config.Profile.Stations.Remove(station);
        if (_active?.Id == station.Id)
        {
            await StopGameOutputAsync();
            if (_mpvProvider is not null) { await _mpvProvider.DisposeAsync(); _mpvProvider = null; }
            if (_externalProvider is not null) { await _externalProvider.DisposeAsync(); _externalProvider = null; }
            _active = null;
            PlayButton.Content = "▶  PLAY";
            NowStation.Text = "NO STATION SELECTED";
            NativeStatus.Text = "SELECT A STATION";
            UpdateRepeatButton();
        }
        await _store.SaveAsync(_config);
        RefreshCollections();
        Footer.Text = $"{station.Name.ToUpperInvariant()} REMOVED.";
    }

    async void StationList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_refreshingCollections || _suppressStationSelectionActivation) return;
        if (StationList.SelectedItem is not Station station) return;
        try { await ActivateAsync(station); }
        catch (Exception error) { Footer.Text = $"COULD NOT TUNE {station.Name.ToUpperInvariant()} · {error.Message}"; }
    }

    async void Macro_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.Tag is RadioMacro macro)
            await RunMacroAsync(macro, macro.Activation is (MacroActivation.Hold or MacroActivation.Momentary) &&
                _heldMacroSources.HasSource(macro.Id, "dashboard") ? MacroTrigger.Release : MacroTrigger.Press, "dashboard");
    }

    async Task RunMacroAsync(RadioMacro macro, MacroTrigger trigger = MacroTrigger.Press, string source = "dashboard")
    {
        var temporary = macro.Activation is MacroActivation.Hold or MacroActivation.Momentary;
        if (temporary)
        {
            if (trigger == MacroTrigger.Press && !_heldMacroSources.Press(macro.Id, source)) return;
            if (trigger == MacroTrigger.Release && !_heldMacroSources.Release(macro.Id, source)) return;
        }
        if (trigger == MacroTrigger.Release && _macroCancellation.TryGetValue(macro.Id, out var pressing))
        {
            pressing.Cancel();
            return;
        }
        if (trigger == MacroTrigger.Press && _macroCancellation.ContainsKey(macro.Id)) return;
        var health = MacroCardFor(macro);
        if (trigger == MacroTrigger.Press && health.Tag is not ("READY" or "RUNNING"))
        {
            if (temporary) _heldMacroSources.Release(macro.Id, source);
            Footer.Text = $"{macro.Name} needs attention: {health.HealthSummary}.";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        if (trigger == MacroTrigger.Press) _macroCancellation[macro.Id] = cancellation;
        try
        {
            Footer.Text = $"{macro.Name} running…";
            var result = await _macroEngine.TriggerAsync(macro, trigger, cancellation.Token);
            if (temporary && trigger == MacroTrigger.Press && !result.Completed) _heldMacroSources.Release(macro.Id, source);
            Footer.Text = result.Completed ? $"{macro.Name}: {result.Summary}" : $"{macro.Name} stopped: {result.Steps.LastOrDefault(x => !x.Success)?.Message}";
            RefreshCollections();
        }
        catch (OperationCanceledException) { if (temporary && trigger == MacroTrigger.Press) _heldMacroSources.Release(macro.Id, source); Footer.Text = $"{macro.Name} cancelled. Temporary settings were restored where possible."; }
        catch (Exception ex) { if (temporary && trigger == MacroTrigger.Press) _heldMacroSources.Release(macro.Id, source); Footer.Text = $"{macro.Name} failed: {ex.Message}"; }
        finally { if (trigger == MacroTrigger.Press) _macroCancellation.Remove(macro.Id); }
    }

    void CancelMacro_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.Tag is RadioMacro macro && _macroCancellation.TryGetValue(macro.Id, out var cancellation)) cancellation.Cancel();
    }

    public async Task<string> ExecuteAsync(RadioAction action, MacroExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var station = _config.Profile.Stations.FirstOrDefault(x => x.Id == action.StationId);
        switch (action.Kind)
        {
            case ActionKind.ToggleGameRoute:
            case ActionKind.EnableGameRoute:
            case ActionKind.DisableGameRoute:
            case ActionKind.RestorePreviousBroadcastState:
            case ActionKind.ToggleMonitorRoute:
            case ActionKind.EnableMonitorRoute:
            case ActionKind.DisableMonitorRoute:
            case ActionKind.RestorePreviousMonitorState:
                if (_config.MusicStripIndex is not { } musicStrip)
                    throw new InvalidOperationException("Select the Voicemeeter music strip in Audio & Routing first.");
                var route = action.Kind is ActionKind.ToggleMonitorRoute or ActionKind.EnableMonitorRoute or
                    ActionKind.DisableMonitorRoute or ActionKind.RestorePreviousMonitorState ? "A1" : _config.GameBus;
                var routing = new VoicemeeterRouteController(_vm);
                string routeResult;
                if (action.Kind is ActionKind.RestorePreviousBroadcastState or ActionKind.RestorePreviousMonitorState)
                    routeResult = routing.Restore(musicStrip, route, context);
                else
                {
                    bool? enabled = action.Kind switch
                    {
                        ActionKind.EnableGameRoute or ActionKind.EnableMonitorRoute => true,
                        ActionKind.DisableGameRoute or ActionKind.DisableMonitorRoute => false,
                        _ => null
                    };
                    routeResult = routing.Change(musicStrip, route, enabled, context);
                }
                UpdateMusicStripState();
                return routeResult;
            case ActionKind.SetStationGain:
                if (station is null) throw new InvalidOperationException("Choose an existing station.");
                if (action.Value is not { } stationDb || !double.IsFinite(stationDb) || stationDb < -60 || stationDb > 12)
                    throw new InvalidOperationException("Choose a station gain from -60 to +12 dB.");
                if (_active?.Id == station.Id && _mpvProvider is null &&
                    !(_active.ProviderId == "youtube" && _youtubeRoute.IsActive))
                    throw new InvalidOperationException("The active station has no native gain control.");
                var newStationGain = MacroActionCatalog.DecibelsToGain(stationDb);
                context.Remember($"station-gain:{station.Id}", station.Volume);
                context.Remember($"station-game-gain:{station.Id}", station.GameVolume);
                station.Volume = newStationGain;
                station.GameVolume = newStationGain;
                if (_active?.Id == station.Id)
                {
                    if (_active.ProviderId == "youtube")
                    {
                        if (_youtubePlayerReady) await ApplyYouTubeListeningGainAsync(_active);
                    }
                    else await _mpvProvider!.SetVolumeAsync(_config.MasterVolume * station.Volume, cancellationToken);
                    await ApplyActiveGameMusicGainAsync(cancellationToken);
                }
                await _store.SaveAsync(_config, cancellationToken);
                return $"{station.Name} gain set to {stationDb:0.#} dB";
            case ActionKind.RestorePreviousStationGain:
                if (station is null) throw new InvalidOperationException("Choose an existing station.");
                if (_active?.Id == station.Id && _mpvProvider is null &&
                    !(_active.ProviderId == "youtube" && _youtubeRoute.IsActive))
                    throw new InvalidOperationException("The active station has no native gain control.");
                if (!context.TryPeek<double>($"station-gain:{station.Id}", out var previousStationGain))
                    throw new InvalidOperationException("No previous station gain is available for this macro run.");
                if (context.TryPeek<double>($"station-game-gain:{station.Id}", out var previousGameStationGain))
                {
                    station.GameVolume = previousGameStationGain;
                    context.TryRestore<double>($"station-game-gain:{station.Id}", out _);
                }
                context.TryRestore<double>($"station-gain:{station.Id}", out _);
                station.Volume = previousStationGain;
                if (_active?.Id == station.Id)
                {
                    if (_active.ProviderId == "youtube")
                    {
                        if (_youtubePlayerReady) await ApplyYouTubeListeningGainAsync(_active);
                    }
                    else await _mpvProvider!.SetVolumeAsync(_config.MasterVolume * station.Volume, cancellationToken);
                    await ApplyActiveGameMusicGainAsync(cancellationToken);
                }
                await _store.SaveAsync(_config, cancellationToken);
                return $"{station.Name} gain restored to {previousStationGain:P0}";
            case ActionKind.MuteAll:
                if (_mpvProvider is null) throw new InvalidOperationException("No native music output is connected for muting.");
                await _mpvProvider.SetVolumeAsync(0, cancellationToken);
                if (_gameMpvProvider is not null) await _gameMpvProvider.SetVolumeAsync(0, cancellationToken);
                context.Remember("mute-gain", _config.MasterVolume);
                context.Remember("mute-game-gain", _config.GameMasterVolume);
                _config.MasterVolume = 0;
                _config.GameMasterVolume = 0;
                MasterVolume.Value = 0;
                GameMasterVolume.Value = 0;
                await _store.SaveAsync(_config, cancellationToken);
                return "Music muted";
            case ActionKind.RestoreMusic:
                if (_mpvProvider is null) throw new InvalidOperationException("No native music output is connected for restoration.");
                if (!context.TryPeek<double>("mute-gain", out var priorMuteGain))
                    throw new InvalidOperationException("No previous music level is available for this macro run.");
                await _mpvProvider.SetVolumeAsync(priorMuteGain * (_active?.Volume ?? 1), cancellationToken);
                if (context.TryPeek<double>("mute-game-gain", out var priorGameMuteGain))
                {
                    if (_gameMpvProvider is not null)
                        await _gameMpvProvider.SetVolumeAsync(priorGameMuteGain * (_active?.GameVolume ?? 1) * (_outputHealth?.ProtectionGain ?? 1), cancellationToken);
                    _config.GameMasterVolume = priorGameMuteGain;
                    GameMasterVolume.Value = priorGameMuteGain;
                    context.TryRestore<double>("mute-game-gain", out _);
                }
                context.TryRestore<double>("mute-gain", out _);
                _config.MasterVolume = priorMuteGain;
                MasterVolume.Value = priorMuteGain;
                await _store.SaveAsync(_config, cancellationToken);
                return $"Music restored to {priorMuteGain:P0}";
            case ActionKind.Duck:
            case ActionKind.SetMasterGain:
                if (action.Value is not { } decibels || decibels < -60 || decibels > 12) throw new InvalidOperationException("Choose a music gain from -60 to +12 dB.");
                if (_mpvProvider is null) throw new InvalidOperationException("No native music output is connected for gain control.");
                var gain = MacroActionCatalog.DecibelsToGain(decibels);
                context.Remember("master-gain", _config.MasterVolume);
                context.Remember("game-master-gain", _config.GameMasterVolume);
                await _mpvProvider.SetVolumeAsync(gain * (_active?.Volume ?? 1), cancellationToken);
                if (_gameMpvProvider is not null) await _gameMpvProvider.SetVolumeAsync(gain * (_active?.GameVolume ?? 1) * (_outputHealth?.ProtectionGain ?? 1), cancellationToken);
                _config.MasterVolume = gain;
                _config.GameMasterVolume = gain;
                MasterVolume.Value = gain;
                GameMasterVolume.Value = gain;
                await _store.SaveAsync(_config, cancellationToken);
                return action.Kind == ActionKind.Duck ? $"Ducking active at {decibels:0.#} dB" : $"Music gain set to {decibels:0.#} dB";
            case ActionKind.RestorePreviousMasterGain:
                if (_mpvProvider is null) throw new InvalidOperationException("No native music output is connected for gain restoration.");
                if (!context.TryPeek<double>("master-gain", out var prior)) throw new InvalidOperationException("No previous music gain is available for this macro run.");
                await _mpvProvider.SetVolumeAsync(prior * (_active?.Volume ?? 1), cancellationToken);
                if (context.TryPeek<double>("game-master-gain", out var priorGame))
                {
                    if (_gameMpvProvider is not null)
                        await _gameMpvProvider.SetVolumeAsync(priorGame * (_active?.GameVolume ?? 1) * (_outputHealth?.ProtectionGain ?? 1), cancellationToken);
                    _config.GameMasterVolume = priorGame;
                    GameMasterVolume.Value = priorGame;
                    context.TryRestore<double>("game-master-gain", out _);
                }
                context.TryRestore<double>("master-gain", out _);
                _config.MasterVolume = prior;
                MasterVolume.Value = prior;
                await _store.SaveAsync(_config, cancellationToken);
                return $"Music gain restored to {prior:P0}";
            case ActionKind.SetHeadsetMasterGain:
                if (action.Value is not { } headsetDb || !double.IsFinite(headsetDb) || headsetDb < -60 || headsetDb > 12)
                    throw new InvalidOperationException("Choose a headset gain from -60 to +12 dB.");
                var headsetGain = MacroActionCatalog.DecibelsToGain(headsetDb);
                context.Remember("headset-only-master-gain", _config.MasterVolume);
                if (_mpvProvider is not null) await _mpvProvider.SetVolumeAsync(headsetGain * (_active?.Volume ?? 1), cancellationToken);
                _config.MasterVolume = headsetGain;
                ShowHeadsetMasterLevel(headsetGain);
                await _store.SaveAsync(_config, cancellationToken);
                return $"Headset master set to {headsetGain:P0}";
            case ActionKind.SetGameMasterGain:
                if (action.Value is not { } gameDb || !double.IsFinite(gameDb) || gameDb < -60 || gameDb > 12)
                    throw new InvalidOperationException("Choose a game gain from -60 to +12 dB.");
                var gameGain = MacroActionCatalog.DecibelsToGain(gameDb);
                context.Remember("game-only-master-gain", _config.GameMasterVolume);
                _config.GameMasterVolume = gameGain;
                ShowGameMasterLevel(gameGain);
                await ApplyActiveGameMusicGainAsync(cancellationToken);
                await _store.SaveAsync(_config, cancellationToken);
                return HasActiveGameMusicFeed() ? $"Game master set to {gameGain:P0}" : $"Game master saved at {gameGain:P0}; game output is not connected";
            case ActionKind.RestorePreviousHeadsetMasterGain:
                if (!context.TryPeek<double>("headset-only-master-gain", out var priorHeadset))
                    throw new InvalidOperationException("No previous headset level is available for this macro run.");
                if (_mpvProvider is not null) await _mpvProvider.SetVolumeAsync(priorHeadset * (_active?.Volume ?? 1), cancellationToken);
                _config.MasterVolume = priorHeadset;
                ShowHeadsetMasterLevel(priorHeadset);
                context.TryRestore<double>("headset-only-master-gain", out _);
                await _store.SaveAsync(_config, cancellationToken);
                return $"Headset master restored to {priorHeadset:P0}";
            case ActionKind.RestorePreviousGameMasterGain:
                if (!context.TryPeek<double>("game-only-master-gain", out var priorGameOnly))
                    throw new InvalidOperationException("No previous game level is available for this macro run.");
                _config.GameMasterVolume = priorGameOnly;
                ShowGameMasterLevel(priorGameOnly);
                await ApplyActiveGameMusicGainAsync(cancellationToken);
                context.TryRestore<double>("game-only-master-gain", out _);
                await _store.SaveAsync(_config, cancellationToken);
                return $"Game master restored to {priorGameOnly:P0}";
            case ActionKind.SetRadioMode:
                _config.DefaultPlaybackMode = PlaybackMode.Radio; RadioMode.IsChecked = true;
                await _store.SaveAsync(_config, cancellationToken);
                return "Radio mode preference saved; local stations resume at their elapsed playlist position";
            case ActionKind.SetPlayerMode:
                _config.DefaultPlaybackMode = PlaybackMode.Player; PlayerMode.IsChecked = true;
                await _store.SaveAsync(_config, cancellationToken);
                return "Player mode preference saved; stations resume at their saved position";
            case ActionKind.SetTransitionDuration:
                if (action.DelayMilliseconds is < 50 or > 10000) throw new InvalidOperationException("Crossfade duration must be 50–10000 ms.");
                _config.CrossfadeSeconds = action.DelayMilliseconds / 1000d;
                await _store.SaveAsync(_config, cancellationToken);
                return $"Crossfade duration set to {_config.CrossfadeSeconds:0.##} s";
            case ActionKind.CycleStations:
                var nextStation = StationCyclePolicy.Next(_config.Profile.Stations, _active?.Id)
                    ?? throw new InvalidOperationException("There are no enabled stations to cycle through.");
                await ActivateAsync(nextStation);
                if (_active?.Id != nextStation.Id) throw new InvalidOperationException($"Could not tune {nextStation.Name}; the previous station remains on air.");
                return $"Cycled to {nextStation.Name}";
            case ActionKind.PlayCurrentStation:
                if (_active is null) throw new InvalidOperationException("Select a station before running Play Current Station.");
                if (!_active.Runtime.WasPlaying) await ToggleActiveAsync();
                return $"Play requested for {_active.Name}; the selected station did not change";
            case ActionKind.PauseCurrentStation:
                if (_active is null) throw new InvalidOperationException("Select a station before running Pause Current Station.");
                await PauseActiveAsync();
                return $"Pause requested for {_active.Name}; the selected station did not change";
            case ActionKind.ActivateStation:
            case ActionKind.FadeToStation:
            case ActionKind.PlayStation:
            case ActionKind.PauseStation:
            case ActionKind.ToggleStation:
                if (station is null || !station.Enabled) throw new InvalidOperationException("Referenced station is unavailable.");
                if (action.Kind == ActionKind.FadeToStation && (_active?.ProviderId != "mpv" || station.ProviderId != "mpv"))
                    throw new InvalidOperationException("Crossfade is available for local/native stations. This provider needs a supported one-at-a-time transition.");
                var wasAlreadyActive = _active?.Id == station.Id;
                var wasPlayingBeforeTune = wasAlreadyActive && station.Runtime.WasPlaying;
                await ActivateAsync(station, action.Kind == ActionKind.FadeToStation,
                    action.Kind == ActionKind.FadeToStation && action.DelayMilliseconds > 0 ? TimeSpan.FromMilliseconds(action.DelayMilliseconds) : null,
                    action.Kind == ActionKind.FadeToStation ? action.Curve : null,
                    action.Kind == ActionKind.PauseStation);
                if (_active?.Id != station.Id) throw new InvalidOperationException($"{station.Name} did not become the active station; the previous station remains on air.");
                if (_mpvProvider is null && _externalProvider?.Snapshot.Health != ProviderHealth.Ready &&
                    !(station.ProviderId == "youtube" && _youtubeReady))
                    throw new InvalidOperationException($"{station.Name}'s player is unavailable.");
                if (action.Kind == ActionKind.PlayStation && !station.Runtime.WasPlaying &&
                    !(station.ProviderId == "youtube" && !_youtubePlayerReady)) await ToggleActiveAsync();
                if (action.Kind == ActionKind.PauseStation && station.Runtime.WasPlaying &&
                    !(station.ProviderId == "youtube" && !_youtubePlayerReady)) await PauseActiveAsync();
                if (action.Kind == ActionKind.ToggleStation && wasAlreadyActive && wasPlayingBeforeTune) await ToggleActiveAsync();
                return $"{station.Name} player {(station.Runtime.WasPlaying ? "playing" : "selected")}";
            case ActionKind.Next:
                if (_mpvProvider is not null && _active?.PlaylistSongs.Count > 0) await NextSongAsync();
                else if (_mpvProvider is not null) { await _mpvProvider.NextAsync(cancellationToken); await SyncGameToHeadsetAsync(_mpvProvider); }
                else if (_externalProvider is not null) await _externalProvider.NextAsync(cancellationToken);
                else throw new InvalidOperationException("No controllable player is active.");
                return "Next track requested";
            case ActionKind.Previous:
                if (_mpvProvider is not null && _active?.PlaylistSongs.Count > 0) await PreviousSongAsync();
                else if (_mpvProvider is not null) { await _mpvProvider.PreviousAsync(cancellationToken); await SyncGameToHeadsetAsync(_mpvProvider); }
                else if (_externalProvider is not null) await _externalProvider.PreviousAsync(cancellationToken);
                else throw new InvalidOperationException("No controllable player is active.");
                return "Previous track requested";
            case ActionKind.Seek:
                if (action.Value is not { } seconds) throw new InvalidOperationException("Seek requires a position in seconds.");
                if (_mpvProvider is not null) await SeekTimelineAsync(seconds);
                else if (_externalProvider is not null) await _externalProvider.SeekAsync(seconds, cancellationToken);
                else throw new InvalidOperationException("No controllable player is active.");
                return $"Seeked to {seconds:0} seconds";
            case ActionKind.OpenPage:
                switch (action.Argument?.ToLowerInvariant())
                {
                    case "dashboard": Dashboard_Click(this, new RoutedEventArgs()); break;
                    case "stations": Stations_Click(this, new RoutedEventArgs()); break;
                    case "macros": Macros_Click(this, new RoutedEventArgs()); break;
                    case "audio": Audio_Click(this, new RoutedEventArgs()); break;
                    case "settings": Settings_Click(this, new RoutedEventArgs()); break;
                    case "diagnostics": Diagnostics_Click(this, new RoutedEventArgs()); break;
                    default: throw new InvalidOperationException("Choose a WARDOGS Radio page.");
                }
                return $"Opened {action.Argument}";
            case ActionKind.LaunchApplication:
                if (string.IsNullOrWhiteSpace(action.Argument) || !File.Exists(action.Argument)) throw new InvalidOperationException("The configured application was not found.");
                Process.Start(new ProcessStartInfo(action.Argument) { UseShellExecute = true });
                return "Application launched";
            case ActionKind.OpenVoicemeeter:
                var voicemeeterPath = FindVoicemeeterExecutable(_vm.Probe().Edition) ?? throw new FileNotFoundException("Voicemeeter application was not found.");
                Process.Start(new ProcessStartInfo(voicemeeterPath) { UseShellExecute = true });
                return "Voicemeeter opened";
            default:
                throw new NotSupportedException($"{action.Kind} is not connected to a verified audio route yet.");
        }
    }

    async Task PauseActiveAsync()
    {
        if (_active is null || !_active.Runtime.WasPlaying) return;
        if (_mpvProvider is not null)
        {
            await _mpvProvider.PauseAsync();
            if (_gameMpvProvider is not null)
            {
                try { await _gameMpvProvider.PauseAsync(); }
                catch (Exception error)
                {
                    await StopGameOutputAsync();
                    GameMpvOutputState.Text = "Game music stopped: " + error.Message;
                }
            }
        }
        else if (_externalProvider is not null) await _externalProvider.PauseAsync();
        else if (_active.ProviderId == "youtube")
        {
            await YouTubeCommandAsync("pause()");
            Footer.Text = "YOUTUBE PAUSE REQUESTED · Waiting for the visible player.";
            return;
        }
        else throw new InvalidOperationException("This station has no connected playback control.");
        _active.Runtime.WasPlaying = false;
        _active.Runtime.IsOnAir = false;
        PlayButton.Content = "▶  PLAY";
        RefreshCollections();
    }

    async Task ToggleActiveAsync()
    {
        if (_active is null) return;
        if (_active.Runtime.WasPlaying) { await PauseActiveAsync(); return; }
        if (_mpvProvider is not null)
        {
            if (_localPlaylistEnded && _active.PlaylistSongs.Count > 0)
            {
                await SelectLocalSongAsync(0);
                _localPlaylistEnded = false;
            }
            _localStartupForensics?.Mark("play command issued");
            await _mpvProvider.PlayAsync();
            _localStartupForensics?.RecordReportedState(
                await _mpvProvider.ReadVolumeAsync(), await _mpvProvider.ReadMuteAsync(), (await _mpvProvider.RefreshAsync()).IsPlaying);
            _localStartupForensics?.Mark("MPV reported IsPlaying");
            if (_gameMpvProvider is not null)
            {
                try { await _gameMpvProvider.PlayAsync(); await SyncGameToHeadsetAsync(_mpvProvider); }
                catch (Exception error)
                {
                    await StopGameOutputAsync();
                    GameMpvOutputState.Text = "Game music stopped: " + error.Message;
                }
            }
        }
        else if (_externalProvider is not null) await _externalProvider.PlayAsync();
        else if (_active.ProviderId == "youtube")
        {
            await YouTubeCommandAsync("play()");
            Footer.Text = "YOUTUBE PLAY REQUESTED · Waiting for the visible player.";
            return;
        }
        else throw new InvalidOperationException("This station cannot play yet. Check its source and provider setup.");
        _active.Runtime.WasPlaying = true;
        _active.Runtime.IsOnAir = true;
        PlayButton.Content = "Ⅱ  PAUSE";
        RefreshCollections();
    }

    void ManageMacros_Click(object s, RoutedEventArgs e)
    {
        var prior = _config.Profile.Macros.ToList();
        var editor = new MacroDesignerWindow(_config.Profile, TestMacroInEditorAsync, CaptureControllerAsync) { Owner = this };
        if (editor.ShowDialog() != true) return;
        _ = PersistMacroEditsAsync(prior);
    }

    void CreateMacro_Click(object s, RoutedEventArgs e)
    {
        var prior = _config.Profile.Macros.ToList();
        var editor = new MacroDesignerWindow(_config.Profile, TestMacroInEditorAsync, CaptureControllerAsync) { Owner = this };
        editor.CreateNew();
        if (editor.ShowDialog() != true) return;
        _ = PersistMacroEditsAsync(prior);
    }

    void EditMacro_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.Tag is not RadioMacro macro) return;
        var prior = _config.Profile.Macros.ToList();
        var editor = new MacroDesignerWindow(_config.Profile, TestMacroInEditorAsync, CaptureControllerAsync, macro.Id) { Owner = this };
        if (editor.ShowDialog() != true) return;
        _ = PersistMacroEditsAsync(prior);
    }

    void MacroIssue_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.Tag is not MacroCard card) return;
        var prior = _config.Profile.Macros.ToList();
        var editor = new MacroDesignerWindow(_config.Profile, TestMacroInEditorAsync, CaptureControllerAsync, card.Macro.Id) { Owner = this };
        editor.Loaded += (_, _) => editor.FocusIssue(card.HealthSummary);
        if (editor.ShowDialog() == true) _ = PersistMacroEditsAsync(prior);
    }

    async void TestMacro_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.Tag is not RadioMacro macro) return;
        var card = MacroCardFor(macro);
        if (card.Tag != "READY") { Footer.Text = $"MACRO TEST BLOCKED · {card.HealthSummary}"; return; }
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await TestMacroInEditorAsync(macro);
            Footer.Text = result.Completed ? $"TEST RUN PASS · {macro.Name.ToUpperInvariant()} · {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms." : $"TEST RUN FAIL · {macro.Name.ToUpperInvariant()} · {result.Steps.Last(x => !x.Success).Message}";
        }
        catch (Exception ex) { Footer.Text = $"TEST RUN FAIL · {macro.Name.ToUpperInvariant()} · {ex.Message}"; }
    }

    async Task<MacroRunResult> TestMacroInEditorAsync(RadioMacro macro)
    {
        // A test is live, but its Hold/Toggle session must not reuse a dashboard or binding session.
        var testEngine = new MacroExecutionEngine(this);
        var press = await testEngine.TriggerAsync(macro, MacroTrigger.Press);
        if (!press.Completed || macro.Activation == MacroActivation.Press) return press;
        var followUp = macro.Activation == MacroActivation.Toggle
            ? await testEngine.TriggerAsync(macro, MacroTrigger.Press)
            : await testEngine.TriggerAsync(macro, MacroTrigger.Release);
        return new MacroRunResult(macro.Id, MacroTrigger.Press, press.Completed && followUp.Completed,
            press.Duration + followUp.Duration, press.Steps.Concat(followUp.Steps).ToList());
    }

    async Task PersistMacroEditsAsync(IReadOnlyList<RadioMacro> prior)
    {
        var recoveryIssues = new List<string>();
        foreach (var old in prior.Where(x => _macroEngine.IsActive(x.Id)))
        {
            try
            {
                var trigger = old.Activation == MacroActivation.Toggle ? MacroTrigger.Press : MacroTrigger.Release;
                var result = await _macroEngine.TriggerAsync(old, trigger);
                if (!result.Completed) recoveryIssues.Add($"{old.Name}: {result.Steps.LastOrDefault(x => !x.Success)?.Message}");
            }
            catch (Exception ex) { recoveryIssues.Add($"{old.Name}: {ex.Message}"); }
        }
        foreach (var old in prior)
            if (!_macroEngine.IsActive(old.Id)) { _macroEngine.Forget(old.Id); _heldMacroSources.Forget(old.Id); }
        try
        {
            await _store.SaveAsync(_config);
            RegisterHotkeys();
            RefreshCollections();
            Footer.Text = recoveryIssues.Count == 0 ? "MACRO DEFINITIONS AND HOTKEYS SAVED." :
                "MACROS SAVED · RESTORE NEEDS ATTENTION: " + string.Join(" · ", recoveryIssues);
        }
        catch (Exception ex) { Footer.Text = $"MACRO SAVE FAILED · {ex.Message}"; }
    }

    async void Previous_Click(object s, RoutedEventArgs e)
    {
        if (_active is null) return;
        try
        {
            if (_mpvProvider is not null && _active.PlaylistSongs.Count > 0) await PreviousSongAsync();
            else if (_mpvProvider is not null) { await _mpvProvider.PreviousAsync(); await SyncGameToHeadsetAsync(_mpvProvider); }
            else if (_externalProvider is not null) await _externalProvider.PreviousAsync();
            else if (_active.ProviderId == "youtube") await YouTubeCommandAsync("previous()");
            else throw new InvalidOperationException("No connected player can skip tracks for this station.");
            Footer.Text = "Previous track requested.";
        }
        catch (Exception ex) { Footer.Text = ex.Message; }
    }

    async void Next_Click(object s, RoutedEventArgs e)
    {
        if (_active is null) return;
        try
        {
            if (_mpvProvider is not null && _active.PlaylistSongs.Count > 0) await NextSongAsync();
            else if (_mpvProvider is not null) { await _mpvProvider.NextAsync(); await SyncGameToHeadsetAsync(_mpvProvider); }
            else if (_externalProvider is not null) await _externalProvider.NextAsync();
            else if (_active.ProviderId == "youtube") await YouTubeCommandAsync("next()");
            else throw new InvalidOperationException("No connected player can skip tracks for this station.");
            Footer.Text = "Next track requested.";
        }
        catch (Exception ex) { Footer.Text = ex.Message; }
    }

    async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsNowPlayingSurfaceExpanded)
        {
            e.Handled = true;
            RequestNowPlayingSurface(false);
            return;
        }
        if (e.Key is not (Key.Return or Key.Space) || e.IsRepeat || !IsActive || !IsEnabled) return;
        if (Keyboard.FocusedElement == AudioAuditionB1Button || Keyboard.FocusedElement == SetupAuditionB1Button) return;
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox { IsEditable: true }) return;
        e.Handled = true;
        await TogglePlaybackFromUserAsync();
    }

    async void Play_Click(object s, RoutedEventArgs e) => await TogglePlaybackFromUserAsync();

    void UpdateRepeatButton()
    {
        var mode = _active?.EffectiveRepeatMode ?? StationRepeatMode.Playlist;
        RepeatButton.Content = mode switch
        {
            StationRepeatMode.Off => "↻ OFF",
            StationRepeatMode.Track => "↻ 1 TRACK",
            _ => "↻ STATION"
        };
        RepeatButton.Background = new System.Windows.Media.SolidColorBrush(mode switch
        {
            StationRepeatMode.Off => System.Windows.Media.Color.FromRgb(32, 42, 39),
            StationRepeatMode.Track => System.Windows.Media.Color.FromRgb(94, 68, 34),
            _ => System.Windows.Media.Color.FromRgb(51, 69, 46)
        });
        RepeatButton.BorderBrush = mode == StationRepeatMode.Track ? FindResource("AmberBrush") as System.Windows.Media.Brush : FindResource("OliveBrush") as System.Windows.Media.Brush;
        RepeatButton.ToolTip = mode switch
        {
            StationRepeatMode.Off => "Repeat off. Playback stops when the playlist ends. Click to repeat the station.",
            StationRepeatMode.Track => "Repeating the current song. Click to turn repeat off.",
            _ => "Repeating the station or playlist. Click to repeat only the current song."
        };
        RepeatButton.IsEnabled = _active?.ProviderId == "mpv" && _mpvProvider is not null ||
            _active?.ProviderId == "youtube" && _youtubePlayerReady;
        AutomationProperties.SetName(RepeatButton, $"Repeat: {mode}. {RepeatButton.ToolTip}");
    }

    async void Repeat_Click(object sender, RoutedEventArgs e)
    {
        if (_active is not { } station || station.ProviderId is not ("mpv" or "youtube")) return;
        var current = station.EffectiveRepeatMode;
        var next = current switch
        {
            StationRepeatMode.Off => StationRepeatMode.Playlist,
            StationRepeatMode.Playlist => StationRepeatMode.Track,
            _ => StationRepeatMode.Off
        };
        try
        {
            if (station.ProviderId == "youtube")
                await YouTubeCommandAsync($"repeat('{next.ToString().ToLowerInvariant()}')");
            else if (_mpvProvider is { } provider)
            {
                var nativeMode = SongPlaylist.HasBoundaries(station) ? StationRepeatMode.Off : next;
                await provider.SetRepeatModeAsync(nativeMode);
                if (_gameMpvProvider is not null) await _gameMpvProvider.SetRepeatModeAsync(nativeMode);
            }
            station.RepeatMode = next;
            station.Loop = next != StationRepeatMode.Off;
            UpdateRepeatButton();
            await _store.SaveAsync(_config);
            Footer.Text = next switch
            {
                StationRepeatMode.Off => "REPEAT OFF · Playback will stop when the station ends.",
                StationRepeatMode.Track => "REPEAT ONE · Current song will repeat.",
                _ => "REPEAT STATION · Playlist will repeat."
            };
        }
        catch (Exception error)
        {
            if (station.ProviderId == "youtube")
            {
                try { await YouTubeCommandAsync($"repeat('{current.ToString().ToLowerInvariant()}')"); } catch { }
            }
            else
            {
                var nativeMode = SongPlaylist.HasBoundaries(station) ? StationRepeatMode.Off : current;
                if (_mpvProvider is not null) try { await _mpvProvider.SetRepeatModeAsync(nativeMode); } catch { }
                if (_gameMpvProvider is not null) try { await _gameMpvProvider.SetRepeatModeAsync(nativeMode); } catch { }
            }
            Footer.Text = $"REPEAT CHANGE FAILED · {error.Message}";
        }
    }

    async Task TogglePlaybackFromUserAsync()
    {
        if (_playbackToggleInFlight) return;
        if (_active is null) { Footer.Text = "Select a station to play."; return; }
        _playbackToggleInFlight = true;
        try
        {
            await ToggleActiveAsync();
            if (_active.ProviderId != "youtube") Footer.Text = _active.Runtime.WasPlaying ? "Playing · Check your headphones and voice chat output." : "Playback paused.";
        }
        catch (Exception ex) { Footer.Text = ex.Message; }
        finally { _playbackToggleInFlight = false; }
    }

    async void Reconnect_Click(object s, RoutedEventArgs e)
    {
        var ok = _vm.TryLogin(out var message);
        _signalStatus = null;
        PopulateMusicStrips();
        if (_config.ClipGuard.LimiterEnabled) ReconcileVoicemeeterLimiter();
        if (ok)
        {
            await InitializeMicrophoneVolumeAsync();
            if (!EnsureYouTubeRouteHealthy(out var routeDetail))
            {
                ShowYouTubeRouteRepairRequired(routeDetail);
                message += " Temporary YouTube route repair still needs attention: " + routeDetail;
            }
            else if (_active is { ProviderId: "youtube" } station && _youtubeRouteRecoveryBlocked == false &&
                YouTubeView.Visibility == Visibility.Collapsed)
            {
                await LoadYouTubeAsync(station, startPaused: false);
                message += " Temporary YouTube route recovery completed; reloading the player.";
            }
        }
        RunDiagnostics();
        RoutingDetail.Text = message;
        HealthText.Text = ok ? "NEEDS SETUP · Check your game voice output" : "NEEDS SETUP · Connect Voicemeeter";
        RefreshSetupWizard();
    }

    void PopulateMusicStrips()
    {
        var status = _vm.Probe();
        var count = status.Edition switch { "Standard" => 3, "Banana" => 5, "Potato" => 8, _ => 0 };
        var physicalCount = status.Edition switch { "Standard" => 2, "Banana" => 3, "Potato" => 5, _ => 0 };
        _loadingMusicStrips = true;
        var choices = new List<MusicStripChoice> { new(null, "Not configured") };
        for (var index = 0; index < count; index++) choices.Add(new(index, index < physicalCount
            ? $"Hardware input {index + 1}"
            : $"Virtual input {index - physicalCount + 1}"));
        MusicStripBox.ItemsSource = choices;
        MusicStripBox.SelectedItem = choices.FirstOrDefault(x => x.Index == _config.MusicStripIndex) ?? choices[0];
        var microphoneChoices = new List<MusicStripChoice> { new(null, "Not configured") };
        for (var index = 0; index < physicalCount; index++) microphoneChoices.Add(new(index, $"Hardware input {index + 1}"));
        MicrophoneStripBox.ItemsSource = microphoneChoices;
        MicrophoneStripBox.SelectedItem = microphoneChoices.FirstOrDefault(x => x.Index == _config.MicrophoneStripIndex) ?? microphoneChoices[0];
        _loadingSetupControls = true;
        SetupMusicStripBox.ItemsSource = choices;
        SetupMusicStripBox.SelectedItem = choices.FirstOrDefault(x => x.Index == _config.MusicStripIndex) ?? choices[0];
        SetupMicStripBox.ItemsSource = microphoneChoices;
        SetupMicStripBox.SelectedItem = microphoneChoices.FirstOrDefault(x => x.Index == _config.MicrophoneStripIndex) ?? microphoneChoices[0];
        _loadingSetupControls = false;
        _loadingMusicStrips = false;
        AudioMicStripNode.Text = _config.MicrophoneStripIndex is { } micIndex ? $"Hardware input {micIndex + 1}" : "Not assigned";
        AudioMusicStripNode.Text = _config.MusicStripIndex is { } musicIndex ? choices.FirstOrDefault(x => x.Index == musicIndex)?.Label ?? $"Strip {musicIndex}" : "Not assigned";
        UpdateMusicStripState();
        RefreshSignalMeters();
    }

    async void MicrophoneStripBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingMusicStrips || MicrophoneStripBox.SelectedItem is not MusicStripChoice choice) return;
        if (_config.MicrophoneStripIndex != choice.Index) InvalidateSignalVerification();
        _config.MicrophoneStripIndex = choice.Index;
        AudioMicStripNode.Text = choice.Label;
        _loadingSetupControls = true;
        SetupMicStripBox.SelectedItem = (SetupMicStripBox.ItemsSource as IEnumerable<MusicStripChoice>)?.FirstOrDefault(x => x.Index == choice.Index);
        _loadingSetupControls = false;
        RefreshSignalMeters();
        ApplyMicrophoneVolumeToMixer(out _);
        await _store.SaveAsync(_config);
    }

    async void SetupMicStripBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSetupControls || SetupMicStripBox.SelectedItem is not MusicStripChoice choice) return;
        if (_config.MicrophoneStripIndex != choice.Index) InvalidateSignalVerification();
        _config.MicrophoneStripIndex = choice.Index;
        _loadingMusicStrips = true;
        MicrophoneStripBox.SelectedItem = (MicrophoneStripBox.ItemsSource as IEnumerable<MusicStripChoice>)?.FirstOrDefault(x => x.Index == choice.Index);
        _loadingMusicStrips = false;
        RefreshSignalMeters();
        ApplyMicrophoneVolumeToMixer(out _);
        await _store.SaveAsync(_config);
    }

    async void SetupMusicStripBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSetupControls || SetupMusicStripBox.SelectedItem is not MusicStripChoice choice) return;
        if (_config.MusicStripIndex != choice.Index) InvalidateSignalVerification();
        _config.MusicStripIndex = choice.Index;
        AudioMusicStripNode.Text = choice.Label;
        _loadingMusicStrips = true;
        MusicStripBox.SelectedItem = (MusicStripBox.ItemsSource as IEnumerable<MusicStripChoice>)?.FirstOrDefault(x => x.Index == choice.Index);
        _loadingMusicStrips = false;
        UpdateMusicStripState();
        RefreshSignalMeters();
        ReconcileVoicemeeterLimiter();
        await _store.SaveAsync(_config);
    }

    bool HeadsetPlayerTargetsSelectedOutput()
    {
        var endpointGuid = _config.MonitorDeviceId?.Split('{').LastOrDefault()?.TrimEnd('}');
        return !string.IsNullOrWhiteSpace(endpointGuid) &&
            _config.MpvAudioDeviceName?.Contains(endpointGuid, StringComparison.OrdinalIgnoreCase) == true;
    }

    void LoadClipGuardControls()
    {
        _loadingClipGuardControls = true;
        try
        {
            _config.ClipGuard.Normalize();
            ClipGuardModeBox.ItemsSource = Enum.GetValues<ClipGuardMode>();
            ClipGuardPresetBox.ItemsSource = Enum.GetValues<ClipGuardPreset>();
            ClipGuardModeBox.SelectedItem = _config.ClipGuard.Mode;
            ClipGuardPresetBox.SelectedItem = _config.ClipGuard.Preset;
            ClipGuardCeilingSlider.Value = _config.ClipGuard.SafetyCeilingDbfs;
            ClipGuardReductionSlider.Value = _config.ClipGuard.MaximumReductionDb;
            ClipGuardNearClipSlider.Value = _config.ClipGuard.NearClipThresholdDbfs;
            ClipGuardAttackSlider.Value = _config.ClipGuard.AttackMilliseconds;
            ClipGuardRecoveryDelaySlider.Value = _config.ClipGuard.RecoveryDelayMilliseconds;
            ClipGuardRecoveryRateSlider.Value = _config.ClipGuard.RecoveryDbPerSecond;
            ClipGuardPeakHoldSlider.Value = _config.ClipGuard.PeakHoldMilliseconds;
            ClipGuardLatchSlider.Value = _config.ClipGuard.ClipLatchMilliseconds;
            ClipGuardAutoGain.IsChecked = _config.ClipGuard.AutoGainEnabled;
            ClipGuardLimiter.IsChecked = _config.ClipGuard.LimiterEnabled;
        }
        finally { _loadingClipGuardControls = false; }
        UpdateClipGuardControls();
    }

    void UpdateClipGuardControls()
    {
        var settings = _config.ClipGuard;
        ClipGuardCeilingText.Text = $"Safety ceiling {settings.SafetyCeilingDbfs:0.0} dBFS · near clip {settings.NearClipThresholdDbfs:0.0} dBFS";
        ClipGuardReductionText.Text = $"Maximum runtime reduction {settings.MaximumReductionDb:0.0} dB · user game level is unchanged";
        ClipGuardLimitStatus.Text = _voicemeeterStripLimiter.Probe(_config.MusicStripIndex).Detail;
    }

    async void ClipGuardSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingClipGuardControls || !IsLoaded) return;
        if (ClipGuardModeBox.SelectedItem is ClipGuardMode mode) _config.ClipGuard.Mode = mode;
        _config.ClipGuard.AutoGainEnabled = ClipGuardAutoGain.IsChecked == true;
        _config.ClipGuard.LimiterEnabled = ClipGuardLimiter.IsChecked == true;
        _config.ClipGuard.SafetyCeilingDbfs = ClipGuardCeilingSlider.Value;
        _config.ClipGuard.MaximumReductionDb = ClipGuardReductionSlider.Value;
        _config.ClipGuard.NearClipThresholdDbfs = ClipGuardNearClipSlider.Value;
        _config.ClipGuard.AttackMilliseconds = (int)Math.Round(ClipGuardAttackSlider.Value);
        _config.ClipGuard.RecoveryDelayMilliseconds = (int)Math.Round(ClipGuardRecoveryDelaySlider.Value);
        _config.ClipGuard.RecoveryDbPerSecond = ClipGuardRecoveryRateSlider.Value;
        _config.ClipGuard.PeakHoldMilliseconds = (int)Math.Round(ClipGuardPeakHoldSlider.Value);
        _config.ClipGuard.ClipLatchMilliseconds = (int)Math.Round(ClipGuardLatchSlider.Value);
        _config.ClipGuard.Preset = ClipGuardPreset.Custom;
        _config.ClipGuard.Normalize();
        LoadClipGuardControls();
        ReconcileVoicemeeterLimiter();
        await _store.SaveAsync(_config);
        RefreshSignalMeters();
    }

    async void ClipGuardPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingClipGuardControls || !IsLoaded || ClipGuardPresetBox.SelectedItem is not ClipGuardPreset preset) return;
        _config.ClipGuard.ApplyPreset(preset);
        LoadClipGuardControls();
        ReconcileVoicemeeterLimiter();
        await _store.SaveAsync(_config);
    }

    void ClipGuardMode_Changed(object sender, SelectionChangedEventArgs e) => ClipGuardSettings_Changed(sender, e);
    void ClipGuardCeiling_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ClipGuardSettings_Changed(sender, e);
    void ClipGuardReduction_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ClipGuardSettings_Changed(sender, e);
    void ClipGuardNearClip_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ClipGuardSettings_Changed(sender, e);
    void ClipGuardAttack_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ClipGuardSettings_Changed(sender, e);
    void ClipGuardRecoveryDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ClipGuardSettings_Changed(sender, e);
    void ClipGuardRecoveryRate_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ClipGuardSettings_Changed(sender, e);
    void ClipGuardPeakHold_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ClipGuardSettings_Changed(sender, e);
    void ClipGuardLatch_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ClipGuardSettings_Changed(sender, e);

    void ReconcileVoicemeeterLimiter()
    {
        var restored = _voicemeeterStripLimiter.TryRestore(out var detail);
        _config.ClipGuard.LimiterEnabled = false;
        _loadingClipGuardControls = true;
        try { ClipGuardLimiter.IsChecked = false; }
        finally { _loadingClipGuardControls = false; }
        ClipGuardLimitStatus.Text = restored
            ? "STABILIZATION MODE · Clip Guard is telemetry-only; no Voicemeeter limiter is controlled."
            : "STABILIZATION MODE · Could not release a prior limiter lease: " + detail;
    }

    void ClipGuardClearLatch_Click(object sender, RoutedEventArgs e)
    {
        _clipGuard.ClearClipLatch();
        RefreshSignalMeters();
    }

    void BroadcastLevelTest_Click(object sender, RoutedEventArgs e)
    {
        if (!_broadcastLevelTest.IsRunning)
        {
            _broadcastLevelTestResult = null;
            _broadcastLevelTest.Start(DateTimeOffset.UtcNow);
            BroadcastLevelTestButton.Content = "STOP BROADCAST LEVEL TEST";
            BroadcastLevelTestResultText.Text = "LISTENING · Play a loud song and speak normally. WARDOGS is observing the real music, microphone, and B1 meters; it will not change levels.";
            BroadcastLevelTestApplyButton.Visibility = Visibility.Collapsed;
            return;
        }

        _broadcastLevelTestResult = _broadcastLevelTest.Stop(_config.ClipGuard, DateTimeOffset.UtcNow);
        BroadcastLevelTestButton.Content = "START BROADCAST LEVEL TEST";
        UpdateBroadcastLevelTestUi();
    }

    async void BroadcastLevelTestApply_Click(object sender, RoutedEventArgs e)
    {
        if (_broadcastLevelTestResult is not { HasRecommendation: true } result) return;
        var prior = _config.GameMasterVolume;
        var requested = Math.Clamp(prior * Math.Pow(10, -result.RecommendedGameMusicReductionDb / 20d), 0, 1);
        try
        {
            _config.GameMasterVolume = requested;
            ShowGameMasterLevel(requested);
            if (_active is { } station)
            {
                if (station.ProviderId == "youtube" && _youtubeGameFeed is not null) _youtubeGameFeed.SetVolume(GamePlayerGain(station));
                else if (_gameMpvProvider is not null) await _gameMpvProvider.SetVolumeAsync(GamePlayerGain(station));
            }
            await _store.SaveAsync(_config);
            BroadcastLevelTestApplyButton.Visibility = Visibility.Collapsed;
            BroadcastLevelTestResultText.Text = $"Applied the test recommendation: Game Master changed from {prior:P0} to {requested:P0} ({result.RecommendedGameMusicReductionDb:0.0} dB). Clip Guard runtime protection remains separate.";
        }
        catch (Exception error)
        {
            _config.GameMasterVolume = prior;
            ShowGameMasterLevel(prior);
            Footer.Text = "COULD NOT APPLY BROADCAST LEVEL RECOMMENDATION · " + error.Message;
        }
    }

    // During stabilization, master and station gain are the only playback-gain
    // authorities. Clip Guard samples are display telemetry, never an actuator.
    double GamePlayerGain(Station station) => Math.Clamp(_config.GameMasterVolume * station.GameVolume, 0, 1);

    bool HasActiveGameMusicFeed() => _active?.ProviderId == "youtube"
        ? _youtubeGameFeed is not null
        : _gameMpvProvider is not null;

    async Task ApplyActiveGameMusicGainAsync(CancellationToken cancellationToken = default)
    {
        if (_active is not { } station) return;
        if (station.ProviderId == "youtube")
        {
            _youtubeGameFeed?.SetVolume(GamePlayerGain(station));
            return;
        }
        if (_gameMpvProvider is not null)
            await _gameMpvProvider.SetVolumeAsync(GamePlayerGain(station), cancellationToken);
    }

    void UpdateOutputHealthUi(OutputHealthSnapshot health)
    {
        var gamePeak = health.GameBus.PeakDbfs is { } db && !double.IsNegativeInfinity(db) ? $"{db:0.0} dBFS" : "—";
        var headroom = health.GameBus.DigitalHeadroomDb is { } room ? $" · headroom {room:0.0} dB" : "";
        var telemetryVerified = _outputTelemetry.CanControlClipGuard;
        ClipGuardDashboardBadge.Text = telemetryVerified ? $"CLIP GUARD · {health.State.ToString().ToUpperInvariant()}" :
            _outputTelemetry.Confidence == OutputTelemetryConfidence.Conflicting ? "OUTPUT TELEMETRY MISMATCH" : "PROTECTION MONITORING NEEDS VERIFICATION";
        ClipGuardDashboardDetail.Text = !telemetryVerified ? _outputTelemetry.Detail :
            $"B1 {gamePeak}{headroom} · telemetry-only during playback stabilization · {health.Diagnosis}";
        ClipGuardStateText.Text = telemetryVerified ? $"{health.State.ToString().ToUpperInvariant()} · B1 {gamePeak} · peak hold {(health.GamePeakHoldDbfs is { } hold && !double.IsNegativeInfinity(hold) ? hold.ToString("0.0") + " dBFS" : "—")}" :
            $"{_outputTelemetry.Confidence.ToString().ToUpperInvariant()} · automatic protection paused";
        ClipGuardRuntimeText.Text = "STABILIZATION MODE · Clip Guard reports telemetry only. " +
            "It does not change player gain, YouTube feed gain, Voicemeeter limiter, routing, or station lifecycle.";
        OutputMusicMeter.Value = Math.Clamp(VoicemeeterSignalMonitor.BarValue(new SignalLevel(health.Music.Available, (float)health.Music.LinearPeak)), 0, 100);
        OutputMicrophoneMeter.Value = Math.Clamp(VoicemeeterSignalMonitor.BarValue(new SignalLevel(health.Microphone.Available, (float)health.Microphone.LinearPeak)), 0, 100);
        OutputGameMeter.Value = Math.Clamp(VoicemeeterSignalMonitor.BarValue(new SignalLevel(health.GameBus.Available, (float)health.GameBus.LinearPeak)), 0, 100);
        UpdatePeakMarker(OutputMusicPeakMarker, health.Music.Available, health.MusicPeakHoldDbfs);
        UpdatePeakMarker(OutputMicrophonePeakMarker, health.Microphone.Available, health.MicrophonePeakHoldDbfs);
        UpdatePeakMarker(OutputGamePeakMarker, health.GameBus.Available, health.GamePeakHoldDbfs);
        OutputMusicText.Text = DescribeLevel("Music", health.Music, health.MusicPeakHoldDbfs);
        OutputMicrophoneText.Text = DescribeLevel("Microphone", health.Microphone, health.MicrophonePeakHoldDbfs);
        var protectionHeadroom = health.GameBus.PeakDbfs is { } gameDb && !double.IsNegativeInfinity(gameDb)
            ? gameDb <= _config.ClipGuard.SafetyCeilingDbfs
                ? $" · protection headroom {_config.ClipGuard.SafetyCeilingDbfs - gameDb:0.0} dB"
                : $" · exceeds safety ceiling by {gameDb - _config.ClipGuard.SafetyCeilingDbfs:0.0} dB"
            : "";
        OutputGameText.Text = telemetryVerified
            ? DescribeLevel("Game mix / B1", health.GameBus, health.GamePeakHoldDbfs) + protectionHeadroom + $" · {health.State.ToString().ToUpperInvariant()}"
            : "Game mix / B1: protection telemetry needs verification · " + _outputTelemetry.Detail;
        OutputHealthDiagnosis.Text = telemetryVerified ? health.Diagnosis : _outputTelemetry.Detail;
        OutputProtectionDetails.Text = $"Requested game music: {_config.GameMasterVolume:P0} × {(_active?.GameVolume ?? 1):P0} · effective {GamePlayerGain(_active ?? new Station { GameVolume = 1 }):P0} · telemetry-only\nSession peak: {DisplayLevel(health.SessionPeakDbfs)} · near clips {health.NearClipEvents} · clips {health.ClipEvents}";
        var latest = health.RecentEvents.LastOrDefault();
        if (latest is not null && latest.Timestamp != _lastOutputEventAt)
        {
            _lastOutputEventAt = latest.Timestamp;
            OutputEventsText.Text = string.Join(Environment.NewLine, health.RecentEvents.TakeLast(6)
                .Select(x => $"{x.Timestamp:HH:mm:ss}  {x.Kind}: {x.Detail}"));
        }
        UpdateBroadcastLevelTestUi();
    }

    static string DescribeLevel(string name, AudioLevelSnapshot level, double? hold)
    {
        var peak = DisplayLevel(level.PeakDbfs);
        var headroom = level.DigitalHeadroomDb is { } room ? $" · headroom {room:0.0} dB" : "";
        var peakHold = hold is { } held && !double.IsNegativeInfinity(held) ? $" · hold {held:0.0} dBFS" : "";
        return level.Available ? $"{name}: {peak}{headroom}{peakHold}" : $"{name}: METER UNAVAILABLE";
    }

    static string DisplayLevel(double? value) => value is { } db && !double.IsNegativeInfinity(db) ? $"{db:0.0} dBFS" : "—";

    static void UpdatePeakMarker(FrameworkElement marker, bool available, double? heldDbfs)
    {
        marker.Visibility = available && heldDbfs is { } db && !double.IsNegativeInfinity(db)
            ? Visibility.Visible : Visibility.Collapsed;
        if (marker.Visibility != Visibility.Visible || marker.Parent is not Canvas canvas) return;
        // The meter spans -60 through 0 dBFS, matching SignalMonitor.BarValue.
        var percent = Math.Clamp((heldDbfs!.Value + 60) / 60 * 100, 0, 100);
        Canvas.SetLeft(marker, Math.Max(0, canvas.ActualWidth * percent / 100 - marker.Width / 2));
    }

    void UpdateBroadcastLevelTestUi()
    {
        if (_broadcastLevelTest.IsRunning && _outputHealth is { } live)
        {
            BroadcastLevelTestLiveText.Text = $"Music {DisplayLevel(live.Music.PeakDbfs)} · Mic {DisplayLevel(live.Microphone.PeakDbfs)} · B1 {DisplayLevel(live.GameBus.PeakDbfs)} · {live.State}";
            return;
        }
        BroadcastLevelTestLiveText.Text = _broadcastLevelTestResult is { } result
            ? $"Result · Music {DisplayLevel(result.MusicPeakDbfs)} · Mic {DisplayLevel(result.MicrophonePeakDbfs)} · B1 {DisplayLevel(result.GamePeakDbfs)} · {result.State}"
            : "Start a test to observe real meter peaks. No audio is generated or rerouted.";
        if (_broadcastLevelTestResult is { } completed)
        {
            BroadcastLevelTestResultText.Text = completed.Summary + " WARDOGS verifies the local Voicemeeter path only; the game may apply its own downstream voice processing.";
            BroadcastLevelTestApplyButton.Visibility = completed.HasRecommendation ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    void RefreshSignalMeters()
    {
        if (_youtubeRoute.IsActive && DateTime.UtcNow >= _nextYouTubeRouteHealthCheck)
        {
            _nextYouTubeRouteHealthCheck = DateTime.UtcNow.AddSeconds(2);
            try
            {
                if (!_youtubeRoute.TryCheckHealth(out var issue))
                {
                    if (EndYouTubeRoute("The temporary route stopped after its health check failed. " + issue))
                    {
                        _youtubeHeadsetRouteError = issue;
                        Footer.Text = "YOUTUBE HEADSET ROUTE STOPPED · " + issue + " Windows output restored.";
                    }
                }
            }
            catch (Exception error)
            {
                _youtubeRouteRecoveryBlocked = true;
                _youtubeHeadsetRouteError = error.Message;
                Footer.Text = "YOUTUBE ROUTE NEEDS ATTENTION · " + error.Message;
            }
        }
        if (_signalStatus is null || DateTime.UtcNow >= _signalStatusExpires)
        {
            _signalStatus = _vm.Probe();
            _signalStatusExpires = DateTime.UtcNow.AddSeconds(2);
        }
        var status = _signalStatus;
        var signals = new VoicemeeterSignalMonitor(_vm);
        var microphone = status.Connected ? signals.ReadStrip(status.Edition, _config.MicrophoneStripIndex) : new SignalLevel(false, 0);
        var music = status.Connected ? signals.ReadStrip(status.Edition, _config.MusicStripIndex) : new SignalLevel(false, 0);
        var game = status.Connected ? signals.ReadBus(status.Edition, _config.GameBus) : new SignalLevel(false, 0);
        var gameEndpointAvailable = _gameBusEndpointPeakMeter.TryRead(_gameOutputEndpointId, out var gameEndpointPeak);
        var gameEndpoint = new SignalLevel(gameEndpointAvailable, gameEndpointPeak);
        _outputTelemetry = OutputTelemetryAssessor.Assess(game.Available, game.Peak, gameEndpoint.Available, gameEndpoint.Peak);
        // Full raw scans query 160 Remote API levels. They are repair evidence
        // for the Diagnostics page, not a permanent normal-playback workload.
        if (status.Connected && DiagnosticsView.Visibility == Visibility.Visible && DateTime.UtcNow >= _nextMeterForensicsCapture)
        {
            _nextMeterForensicsCapture = DateTime.UtcNow.AddSeconds(1);
            try { _meterForensics = signals.CaptureForensics(status.Edition); }
            catch { /* Diagnostics reports the last valid snapshot; meter polling never interrupts playback. */ }
        }
        // The Remote API B1 path is the Clip Guard control sensor only after its
        // independent Windows B1 observation agrees. On a mismatch, present it as
        // unavailable to force neutral gain instead of inventing a SAFE result.
        var controlGame = _outputTelemetry.CanControlClipGuard ? game : new SignalLevel(false, 0);
        _outputHealth = _clipGuard.Sample(_config.ClipGuard,
            AudioLevelSnapshot.FromLinear(music.Available, music.Peak),
            AudioLevelSnapshot.FromLinear(microphone.Available, microphone.Peak),
            AudioLevelSnapshot.FromLinear(controlGame.Available, controlGame.Peak),
            canAutomaticallyAttenuateMusic: false, now: DateTimeOffset.UtcNow);
        _broadcastLevelTest.Observe(_outputHealth);
        var directHeadset = HeadsetPlayerTargetsSelectedOutput();
        // This is the selected physical listening endpoint, regardless of whether
        // the station reaches it directly or through Voicemeeter A1.
        var headsetAvailable = _headsetPeakMeter.TryRead(_config.MonitorDeviceId, out var headsetPeak);
        var monitor = new SignalLevel(headsetAvailable, headsetPeak);
        UpdateNowPlayingVisualizer(music, monitor);
        if (_active?.ProviderId == "youtube" && monitor.Peak > .001f)
            _youtubeStartupForensics?.Mark("T10 headset endpoint signal");
        if (_youtubeGameFeed is { HasRecentSignal: true }) _youtubeStartupForensics?.Mark("T10 B1 signal");
        _localStartupForensics?.RecordPeaks(monitor, music, gameEndpoint);
        _sawMicSignal |= microphone.Available && microphone.Peak > .005f;
        var musicPlayerPlaying = _active?.ProviderId == "youtube"
            ? _youtubeGameFeed is not null && _active.Runtime.WasPlaying
            : _config.GameMpvAudioDeviceName is null
                ? _mpvProvider?.Snapshot.IsPlaying == true : _gameMpvProvider?.Snapshot.IsPlaying == true;
        _sawMusicSignal |= music.Available && music.Peak > .005f && musicPlayerPlaying;
        _sawGameSignal |= game.Available && game.Peak > .005f;
        _sawGameEndpointSignal |= gameEndpoint.Available && gameEndpoint.Peak > .005f;
        _sawMonitorSignal |= monitor.Available && monitor.Peak > .005f;
        if (DateTime.UtcNow < _nextSignalPresentation) return;
        _nextSignalPresentation = DateTime.UtcNow.AddMilliseconds(200);
        var dashboardVisible = DashboardView.Visibility == Visibility.Visible;
        var audioVisible = AudioView.Visibility == Visibility.Visible;
        var setupVisible = SetupView.Visibility == Visibility.Visible;
        if (audioVisible) UpdateOutputHealthUi(_outputHealth);
        UpdateGameVoiceBadge(status.Connected, game, gameEndpoint);
        var microphoneBar = VoicemeeterSignalMonitor.BarValue(microphone);
        var musicBar = VoicemeeterSignalMonitor.BarValue(music);
        var gameBar = VoicemeeterSignalMonitor.BarValue(game);
        var monitorBar = VoicemeeterSignalMonitor.BarValue(monitor);
        if (setupVisible)
        {
            SetupMicMeter.Value = microphoneBar;
            SetupMusicMeter.Value = musicBar;
            SetupGameMeter.Value = gameBar;
            SetupB1EndpointMeter.Value = VoicemeeterSignalMonitor.BarValue(gameEndpoint);
            SetupMonitorMeter.Value = monitorBar;
            SetupMicSelectionMeter.Value = microphoneBar;
            SetupMonitorSelectionMeter.Value = monitorBar;
            SetupB1EndpointLabel.Text = _gameOutputEndpointName is null ? "VOICEMEETER OUT B1 · WINDOWS ENDPOINT NOT FOUND"
                : !gameEndpoint.Available ? "VOICEMEETER OUT B1 · CAPTURE UNAVAILABLE"
                : gameEndpoint.Peak > .005f ? "VOICEMEETER OUT B1 · CAPTURED SIGNAL" : "VOICEMEETER OUT B1 · NO SIGNAL";
        }
        if (audioVisible)
        {
            AudioMicMeter.Value = microphoneBar;
            AudioGameMeter.Value = gameBar;
            AudioHeadsetMeter.Value = monitorBar;
            AudioListeningNodeText.Text = _youtubeRoute.IsActive
                ? "YouTube → Voicemeeter VAIO/A1 → selected headphones"
                : directHeadset ? "Selected Windows headphones · direct output" : "Voicemeeter A1 listening output";
            GameRouteText.Text = _gameOutputEndpointName is null ? $"{_config.GameBus} output device not found"
                : $"{_config.GameBus} · {_gameOutputEndpointName} · {(gameEndpoint.Peak > .005f ? "captured signal" : "no signal yet")}";
        }
        if (dashboardVisible)
        {
            MicMeter.Value = microphoneBar;
            MusicMeter.Value = musicBar;
            GameMeter.Value = gameBar;
            MonitorMeter.Value = monitorBar;
            MicMeterLabel.Text = !status.Connected ? "MICROPHONE · CONNECT VOICEMEETER" : _config.MicrophoneStripIndex is null ? "MICROPHONE · CHOOSE INPUT" : !microphone.Available ? "MICROPHONE · METER UNAVAILABLE" : microphone.Peak > .005f ? "MICROPHONE · SIGNAL DETECTED" : "MICROPHONE · NO SIGNAL (SPEAK TO TEST)";
            HeadsetPlayerSignal.Text = _active?.ProviderId == "youtube"
                ? _youtubePlayerReady ? _active.Runtime.WasPlaying ? "YOUTUBE PLAYER · playing" : "YOUTUBE PLAYER · ready / paused" : "YOUTUBE PLAYER · loading"
                : _mpvProvider is null ? _active is null ? "HEADSET PLAYER · no station" : "HEADSET PLAYER · no local music player"
                : _mpvProvider.Snapshot.IsPlaying ? "HEADSET PLAYER · playing" : "HEADSET PLAYER · ready / paused";
            GamePlayerSignal.Text = _config.GameMpvAudioDeviceName is null ? "GAME MUSIC · output off"
                : _active?.ProviderId == "youtube" ? _youtubeGameFeed is null ? "GAME MUSIC · YouTube route unavailable" : _youtubeGameFeed.Fault is not null ? "GAME MUSIC · YouTube route stopped: " + _youtubeGameFeed.Fault : _youtubeGameFeed.HasRecentSignal && music.Available && music.Peak <= VoicemeeterSignalMonitor.VisualSignalFloor ? "GAME FEED CAPTURED AUDIO BUT OUTPUT ROUTE IS SILENT" : _youtubeGameFeed.HasRecentSignal && music.Available ? "GAME MUSIC · source and selected Voicemeeter input show signal" : _youtubeGameFeed.HasRecentSignal ? "GAME MUSIC · source captured; configured Voicemeeter input meter unavailable" : "GAME MUSIC · waiting for YouTube audio"
                : _gameMpvProvider is null ? "GAME MUSIC · configured but not connected" : _gameMpvProvider.Snapshot.IsPlaying ? "GAME MUSIC · playing" : "GAME MUSIC · ready / paused";
            var musicInputLabel = _config.GameMpvAudioDeviceName is null ? "VOICEMEETER MUSIC INPUT" : "GAME MUSIC INPUT";
            MusicMeterLabel.Text = !status.Connected ? $"{musicInputLabel} · CONNECT VOICEMEETER" : _active?.ProviderId == "youtube" && _youtubeGameFeed is null ? "GAME MUSIC INPUT · YOUTUBE ROUTE UNAVAILABLE" : _config.GameMpvAudioDeviceName is null && _config.MpvAudioDeviceName is null ? $"{musicInputLabel} · PLAYER ROUTED DIRECTLY TO HEADSET" : _config.MusicStripIndex is null ? $"{musicInputLabel} · CHOOSE INPUT" : !music.Available ? $"{musicInputLabel} · METER UNAVAILABLE" : music.Peak > .005f ? $"{musicInputLabel} · SIGNAL DETECTED" : $"{musicInputLabel} · NO SIGNAL";
            GameMeterLabel.Text = _outputTelemetry.Confidence == OutputTelemetryConfidence.Conflicting ? "GAME VOICE BUS · OUTPUT TELEMETRY MISMATCH" : !game.Available ? "GAME VOICE BUS · METER UNAVAILABLE" : game.Peak > .005f ? "GAME VOICE BUS · SIGNAL DETECTED" : "GAME VOICE BUS · NO SIGNAL";
            MonitorMeterLabel.Text = string.IsNullOrWhiteSpace(_config.MonitorDeviceId) ? "HEADSET OUTPUT · CHOOSE HEADPHONES" : !monitor.Available ? "HEADSET OUTPUT · WINDOWS METER UNAVAILABLE" : monitor.Peak > .005f ? "HEADSET OUTPUT · SIGNAL DETECTED" : "HEADSET OUTPUT · NO SIGNAL";
        }
        if (SetupView.Visibility == Visibility.Visible && DateTime.UtcNow >= _nextSetupSignalRefresh)
        {
            _nextSetupSignalRefresh = DateTime.UtcNow.AddSeconds(1);
            RefreshSetupWizard();
        }
    }

    async void MusicStripBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingMusicStrips || MusicStripBox.SelectedItem is not MusicStripChoice choice) return;
        if (_config.MusicStripIndex != choice.Index) InvalidateSignalVerification();
        _config.MusicStripIndex = choice.Index;
        _loadingSetupControls = true;
        SetupMusicStripBox.SelectedItem = (SetupMusicStripBox.ItemsSource as IEnumerable<MusicStripChoice>)?.FirstOrDefault(x => x.Index == choice.Index);
        _loadingSetupControls = false;
        UpdateMusicStripState();
        RefreshSignalMeters();
        ReconcileVoicemeeterLimiter();
        await _store.SaveAsync(_config);
        RefreshCollections();
    }

    void UpdateMusicStripState()
    {
        if (_config.MusicStripIndex is not { } strip)
        {
            MusicStripState.Text = "Choose the input where WARDOGS music appears in Voicemeeter.";
            return;
        }
        var routes = new VoicemeeterRouteController(_vm);
        var game = routes.TryRead(strip, _config.GameBus, out var gameOn) ? (gameOn ? "ON" : "OFF") : "UNAVAILABLE";
        var monitor = routes.TryRead(strip, "A1", out var monitorOn) ? (monitorOn ? "ON" : "OFF") : "UNAVAILABLE";
        var label = (MusicStripBox.SelectedItem as MusicStripChoice)?.Label ?? "Music input";
        MusicStripState.Text = _config.GameMpvAudioDeviceName is null
            ? $"{label} · Game voice: {game} · A1 listening: {monitor}"
            : $"{label} · Game voice: {game} · A1: {monitor} (OFF avoids doubling direct headset music)";
    }

    async void TestMusic_Click(object s, RoutedEventArgs e)
    {
        if (_config.MusicStripIndex is not { } strip) { Footer.Text = "MUSIC TEST · Select the Voicemeeter strip receiving the game music output first."; return; }
        Footer.Text = $"MUSIC TEST · PLAY A SONG NOW; watching Voicemeeter strip {strip} for three seconds…";
        var seen = await ObserveStripSignalAsync(strip);
        Footer.Text = seen ? $"MUSIC INPUT DETECTED · Strip {strip} moved. Confirm its game bus route separately." : $"NO MUSIC INPUT SEEN · Strip {strip} stayed quiet. Check the game music output device and Voicemeeter input.";
        RefreshSetupWizard();
    }

    async void TestMicrophone_Click(object s, RoutedEventArgs e)
    {
        if (_config.MicrophoneStripIndex is not { } strip) { Footer.Text = "MIC TEST · Select the physical Voicemeeter microphone strip first."; SetupMicTestState.Text = "Choose and connect a microphone first."; return; }
        Footer.Text = $"MIC TEST · SPEAK NOW; watching Voicemeeter strip {strip} for three seconds…";
        SetupMicTestState.Text = "Listening for your microphone for three seconds…";
        var seen = await ObserveStripSignalAsync(strip);
        Footer.Text = seen ? $"MIC INPUT DETECTED · Strip {strip} moved. Confirm B1 separately." : $"NO MIC INPUT SEEN · Strip {strip} stayed quiet. Check the selected physical Voicemeeter input.";
        SetupMicTestState.Text = seen ? "PASS · Microphone signal detected." : "NO SIGNAL · Speak into the selected mic, check its Voicemeeter input, and test again.";
        RefreshSetupWizard();
    }

    async Task<bool> ObserveStripSignalAsync(int strip)
    {
        var monitor = new VoicemeeterSignalMonitor(_vm);
        var edition = _vm.Probe().Edition;
        if (edition is null) return false;
        var end = DateTime.UtcNow.AddSeconds(3);
        var seen = false;
        while (DateTime.UtcNow < end)
        {
            var reading = monitor.ReadStrip(edition, strip);
            seen |= reading.Available && reading.Peak > .005f;
            await Task.Delay(100);
        }
        if (strip == _config.MicrophoneStripIndex) _sawMicSignal |= seen;
        var selectedMusicPlaying = _active?.ProviderId == "youtube"
            ? _youtubeGameFeed is { Fault: null } && _active.Runtime.WasPlaying
            : _config.GameMpvAudioDeviceName is null
                ? _mpvProvider?.Snapshot.IsPlaying == true
                : _gameMpvProvider?.Snapshot.IsPlaying == true;
        if (strip == _config.MusicStripIndex && selectedMusicPlaying) _sawMusicSignal |= seen;
        return seen;
    }
    void SetupRouteMic_Click(object sender, RoutedEventArgs e)
    {
        if (_config.MicrophoneStripIndex is not { } strip) { Footer.Text = "Choose the microphone strip first."; return; }
        EnableWizardRoutes((strip, _config.GameBus));
    }

    void SetupRouteMusic_Click(object sender, RoutedEventArgs e)
    {
        if (_config.MusicStripIndex is not { } strip) { Footer.Text = "Choose the music strip first."; return; }
        if (HeadsetPlayerTargetsSelectedOutput()) EnableWizardRoutes((strip, _config.GameBus));
        else EnableWizardRoutes((strip, _config.GameBus), (strip, "A1"));
    }

    async void SetupAutomaticMicrophone_Click(object sender, RoutedEventArgs e)
    {
        if (SetupMicrophoneBox.SelectedItem is not WindowsAudioEndpoint microphone)
        {
            Footer.Text = "CHOOSE YOUR MICROPHONE ON STEP 2 FIRST.";
            return;
        }
        await ConfigureAutomaticMicrophoneAsync(microphone);
    }

    async Task<bool> ConfigureAutomaticMicrophoneAsync(WindowsAudioEndpoint microphone)
    {
        var status = _vm.Probe();
        if (!status.Connected || status.Edition != "Banana")
        {
            Footer.Text = "AUTO MICROPHONE SETUP NEEDS A CONNECTED VOICEMEETER BANANA ENGINE.";
            return false;
        }
        var device = _vm.ListAudioDevices(true)
            .Where(x => x.InterfaceType == 3 && x.Name.Equals(microphone.Name, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (device is null)
        {
            Footer.Text = "VOICEMEETER CANNOT MATCH THAT MICROPHONE TO AN AVAILABLE WDM INPUT.";
            return false;
        }
        if (device.HardwareId.StartsWith("BTHHFENUM", StringComparison.OrdinalIgnoreCase) &&
            !RadioDialogWindow.Confirm(this, "Bluetooth microphone changes headphone audio",
                "Opening this Bluetooth headset microphone can switch the same headset from high-quality stereo music to lower-quality call audio. A separate microphone keeps stereo playback available.\n\nConnect this Bluetooth mic anyway?",
                "USE BLUETOOTH MIC"))
        {
            Footer.Text = "MIC ROUTE UNCHANGED · Choose a separate microphone on Step 2 to keep stereo headset music.";
            return false;
        }
        var ownedStrip = _config.AutoMicrophoneStrip;
        var strip = ownedStrip ?? Enumerable.Range(0, 3).FirstOrDefault(index =>
            _vm.TryGetParameterString($"Strip[{index}].device.name", out var name) && string.IsNullOrWhiteSpace(name), -1);
        if (strip < 0)
        {
            Footer.Text = "ALL PHYSICAL VOICEMEETER INPUTS ARE ASSIGNED. AUTO SETUP WILL NOT REPLACE AN EXISTING DEVICE.";
            return false;
        }
        var meter = new VoicemeeterSignalMonitor(_vm).ReadStrip(status.Edition, strip);
        if (!meter.Available || ownedStrip is null && meter.Peak > .005f)
        {
            Footer.Text = "THE FREE PHYSICAL INPUT HAS LIVE SIGNAL OR NO READABLE METER; AUTO SETUP WILL NOT TAKE IT OVER.";
            return false;
        }
        var routes = new VoicemeeterRouteController(_vm);
        if (!routes.TryRead(strip, "A1", out var priorA1) || !routes.TryRead(strip, _config.GameBus, out var priorB1))
        {
            Footer.Text = "CANNOT READ MICROPHONE ROUTES; NOTHING CHANGED.";
            return false;
        }
        var priorStrip = _config.MicrophoneStripIndex;
        _vm.TryGetParameterString($"Strip[{strip}].device.name", out var priorDevice);
        if (ownedStrip is not null && string.Equals(priorDevice, device.Name, StringComparison.OrdinalIgnoreCase))
        {
            _config.MicrophoneDeviceId = microphone.Id;
            await _store.SaveAsync(_config);
            await InitializeMicrophoneVolumeAsync();
            Footer.Text = $"MICROPHONE ALREADY CONNECTED · {device.Name} → {_config.GameBus}.";
            return true;
        }
        var driver = device.InterfaceName.ToLowerInvariant();
        var connected = false;
        try
        {
            if (!_vm.TrySetParameterString($"Strip[{strip}].device.{driver}", device.Name))
                throw new InvalidOperationException("Voicemeeter refused the microphone device assignment.");
            connected = true;
            if (!_vm.TrySetParameterFloat($"Strip[{strip}].A1", 0) || !_vm.TrySetParameterFloat($"Strip[{strip}].{_config.GameBus}", 1))
                throw new InvalidOperationException("Voicemeeter refused microphone routing.");
            var assigned = false;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(100);
                if (_vm.TryGetParameterString($"Strip[{strip}].device.name", out var current) && current.Contains(device.Name, StringComparison.OrdinalIgnoreCase))
                {
                    assigned = true;
                    break;
                }
            }
            if (!assigned) throw new InvalidOperationException("Voicemeeter did not report the selected microphone on its physical input.");
            if (ownedStrip is null)
            {
                _config.AutoMicrophoneStrip = strip;
                _config.AutoMicrophonePreviousDeviceName = priorDevice;
                _config.AutoMicrophonePreviousA1 = priorA1;
                _config.AutoMicrophonePreviousB1 = priorB1;
                _config.AutoMicrophonePreviousStripIndex = priorStrip;
            }
            _config.MicrophoneStripIndex = strip;
            _config.MicrophoneDeviceId = microphone.Id;
            InvalidateSignalVerification();
            await _store.SaveAsync(_config);
            PopulateMusicStrips();
            await InitializeMicrophoneVolumeAsync();
            RefreshSignalMeters();
            Footer.Text = $"MICROPHONE ASSIGNED TO VOICEMEETER HARDWARE INPUT {strip + 1} → {_config.GameBus}; A1 SELF-MONITORING OFF. SPEAK TO VERIFY ITS LIVE METER.";
            RefreshSetupWizard();
            return true;
        }
        catch (Exception error)
        {
            if (connected) _vm.TrySetParameterString($"Strip[{strip}].device.{driver}", priorDevice);
            _vm.TrySetParameterFloat($"Strip[{strip}].A1", priorA1 ? 1 : 0);
            _vm.TrySetParameterFloat($"Strip[{strip}].{_config.GameBus}", priorB1 ? 1 : 0);
            Footer.Text = "AUTO MICROPHONE SETUP FAILED · Previous route restored where possible · " + error.Message;
            return false;
        }
    }

    async void SetupAutomaticMusic_Click(object sender, RoutedEventArgs e)
    {
        var status = _vm.Probe();
        if (!status.Connected || status.Edition != "Banana")
        {
            Footer.Text = "AUTO MUSIC SETUP NEEDS A CONNECTED VOICEMEETER BANANA ENGINE.";
            return;
        }
        if (SetupMonitorBox.SelectedItem is not WindowsAudioEndpoint headset)
        {
            Footer.Text = "CHOOSE YOUR HEADPHONES ON STEP 4 FIRST.";
            return;
        }
        const int auxStrip = 4;
        if (_config.AutoMusicRouteStrip is { } savedStrip && savedStrip != auxStrip)
        {
            Footer.Text = "RESTORE THE PREVIOUS AUTO MUSIC ROUTE BEFORE CHANGING STRIPS.";
            return;
        }
        var devices = (GameMpvOutputBox.ItemsSource as IEnumerable<MpvAudioDevice>)?.ToList() ?? [];
        var headsetId = headset.Id.Split('{').LastOrDefault()?.TrimEnd('}');
        var headsetDevice = devices.FirstOrDefault(x => !string.IsNullOrWhiteSpace(headsetId) && x.Name.Contains(headsetId, StringComparison.OrdinalIgnoreCase));
        var gameDevice = devices.FirstOrDefault(x => x.Description.Contains("Voicemeeter AUX Input", StringComparison.OrdinalIgnoreCase));
        if (headsetDevice is null || gameDevice is null || headsetDevice.Name == gameDevice.Name)
        {
            Footer.Text = "AUTO MUSIC SETUP CANNOT MATCH THE SELECTED HEADSET AND VOICEMEETER AUX IN MPV. REFRESH MPV OUTPUTS.";
            return;
        }
        var routeController = new VoicemeeterRouteController(_vm);
        if (_config.AutoMusicRouteStrip == auxStrip &&
            _config.MpvAudioDeviceName == headsetDevice.Name &&
            _config.GameMpvAudioDeviceName == gameDevice.Name &&
            routeController.TryRead(auxStrip, _config.GameBus, out var alreadyOnB1) && alreadyOnB1 &&
            routeController.TryRead(auxStrip, "A1", out var alreadyOnA1) && !alreadyOnA1)
        {
            Footer.Text = "MUSIC IS ALREADY CONNECTED · Your selected headphones play the station; Voicemeeter AUX sends a separate feed to B1. No route changed.";
            RefreshSetupWizard();
            return;
        }
        var monitor = new VoicemeeterSignalMonitor(_vm);
        var auxLevel = monitor.ReadStrip(status.Edition, auxStrip);
        if (!auxLevel.Available || auxLevel.Peak > .005f)
        {
            Footer.Text = auxLevel.Available ? "VOICEMEETER AUX ALREADY HAS LIVE AUDIO. AUTO SETUP WILL NOT TAKE OVER AN ACTIVE INPUT." : "VOICEMEETER AUX METER IS UNAVAILABLE; AUTO SETUP DID NOT CHANGE ROUTES.";
            return;
        }
        var routes = routeController;
        if (!routes.TryRead(auxStrip, "A1", out var priorA1) || !routes.TryRead(auxStrip, _config.GameBus, out var priorB1))
        {
            Footer.Text = "CANNOT READ EXISTING VOICEMEETER ROUTES; NOTHING CHANGED.";
            return;
        }
        var formerHeadset = _config.MpvAudioDeviceName;
        var formerGame = _config.GameMpvAudioDeviceName;
        var formerStrip = _config.MusicStripIndex;
        var formerAutoStrip = _config.AutoMusicRouteStrip;
        var formerAutoA1 = _config.AutoMusicPreviousA1;
        var formerAutoB1 = _config.AutoMusicPreviousB1;
        var changedA1 = false;
        var changedB1 = false;
        try
        {
            if (priorA1)
            {
                if (!_vm.TrySetParameterFloat($"Strip[{auxStrip}].A1", 0)) throw new InvalidOperationException("Could not turn off AUX monitoring to prevent double headset playback.");
                changedA1 = true;
            }
            if (!priorB1)
            {
                if (!_vm.TrySetParameterFloat($"Strip[{auxStrip}].{_config.GameBus}", 1)) throw new InvalidOperationException("Could not enable AUX to game bus.");
                changedB1 = true;
            }
            if (_mpvProvider is not null) await _mpvProvider.SetAudioDeviceAsync(headsetDevice.Name);
            _config.AutoMusicRouteStrip ??= auxStrip;
            _config.AutoMusicPreviousA1 ??= priorA1;
            _config.AutoMusicPreviousB1 ??= priorB1;
            if (formerAutoStrip is null)
            {
                _config.AutoMusicPreviousHeadsetDeviceName = formerHeadset;
                _config.AutoMusicPreviousGameDeviceName = formerGame;
                _config.AutoMusicPreviousStripIndex = formerStrip;
            }
            _config.MpvAudioDeviceName = headsetDevice.Name;
            _config.GameMpvAudioDeviceName = gameDevice.Name;
            _config.MusicStripIndex = auxStrip;
            InvalidateSignalVerification();
            await _store.SaveAsync(_config);
            await LoadMpvOutputsAsync();
            PopulateMusicStrips();
            if (_active is { ProviderId: "mpv" } station && _mpvProvider is { } player)
            {
                await LoadGameOutputAsync(station, player);
                if (_gameMpvProvider is null) throw new InvalidOperationException("MPV could not open the Voicemeeter AUX output.");
                if (station.Runtime.WasPlaying) await _gameMpvProvider.PlayAsync();
            }
            Footer.Text = "MUSIC ROUTED · Headset plays directly; separate game player feeds Voicemeeter AUX → B1. Play a station and verify moving music/B1 meters.";
            RefreshSignalMeters();
            RefreshSetupWizard();
        }
        catch (Exception error)
        {
            await StopGameOutputAsync();
            if (changedA1) _vm.TrySetParameterFloat($"Strip[{auxStrip}].A1", priorA1 ? 1 : 0);
            if (changedB1) _vm.TrySetParameterFloat($"Strip[{auxStrip}].{_config.GameBus}", priorB1 ? 1 : 0);
            _config.MpvAudioDeviceName = formerHeadset;
            _config.GameMpvAudioDeviceName = formerGame;
            _config.MusicStripIndex = formerStrip;
            _config.AutoMusicRouteStrip = formerAutoStrip;
            _config.AutoMusicPreviousA1 = formerAutoA1;
            _config.AutoMusicPreviousB1 = formerAutoB1;
            if (formerAutoStrip is null)
            {
                _config.AutoMusicPreviousHeadsetDeviceName = null;
                _config.AutoMusicPreviousGameDeviceName = null;
                _config.AutoMusicPreviousStripIndex = null;
            }
            if (_mpvProvider is not null) try { await _mpvProvider.SetAudioDeviceAsync(formerHeadset ?? "auto"); } catch { }
            await _store.SaveAsync(_config);
            Footer.Text = "AUTO MUSIC SETUP FAILED · Previous outputs and routes restored where possible · " + error.Message;
            RefreshSetupWizard();
        }
    }

    void EnableWizardRoutes(params (int Strip, string Route)[] targets)
    {
        var routes = new VoicemeeterRouteController(_vm);
        var changed = new List<(int Strip, string Route)>();
        try
        {
            foreach (var (strip, route) in targets)
            {
                if (!routes.TryRead(strip, route, out var enabled)) throw new InvalidOperationException($"Could not read strip {strip} {route}; no further route changed.");
                if (enabled) continue;
                routes.Change(strip, route, true, _setupRouteContext);
                changed.Add((strip, route));
            }
            _setupChangedRoutes.AddRange(changed);
            if (changed.Count > 0) InvalidateSignalVerification();
            Footer.Text = changed.Count == 0 ? "REQUESTED ROUTES WERE ALREADY ON." : "VOICEMEETER ROUTES ENABLED · Test live signal and game/headphone receive.";
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<string>();
            foreach (var (strip, route) in changed.AsEnumerable().Reverse())
                try { routes.Restore(strip, route, _setupRouteContext); }
                catch (Exception rollback) { _setupChangedRoutes.Add((strip, route)); rollbackErrors.Add(rollback.Message); }
            Footer.Text = rollbackErrors.Count == 0 ? $"ROUTE CHANGE FAILED · {error.Message}" :
                $"ROUTE CHANGE FAILED · RESTORE NEEDS ATTENTION · {string.Join("; ", rollbackErrors)}";
        }
        UpdateMusicStripState();
        RefreshSetupWizard();
    }

    async void RestoreRoute_Click(object s, RoutedEventArgs e)
    {
        var routes = new VoicemeeterRouteController(_vm);
        if (_config.AutoMicrophoneStrip is { } micStrip)
        {
            if (!_vm.TryGetParameterString($"Strip[{micStrip}].device.name", out var currentMic))
            {
                Footer.Text = "MICROPHONE ROUTE RESTORE FAILED · Cannot read current input.";
                return;
            }
            if (!string.IsNullOrWhiteSpace(currentMic) && SetupMicrophoneBox.SelectedItem is WindowsAudioEndpoint selectedMic &&
                !currentMic.Contains(selectedMic.Name, StringComparison.OrdinalIgnoreCase))
            {
                Footer.Text = "MICROPHONE INPUT WAS CHANGED ELSEWHERE. AUTO RESTORE WILL NOT OVERWRITE IT.";
                return;
            }
            if (!_vm.TrySetParameterString($"Strip[{micStrip}].device.wdm", _config.AutoMicrophonePreviousDeviceName ?? "") ||
                _config.AutoMicrophonePreviousA1 is { } micA1 && !_vm.TrySetParameterFloat($"Strip[{micStrip}].A1", micA1 ? 1 : 0) ||
                _config.AutoMicrophonePreviousB1 is { } micB1 && !_vm.TrySetParameterFloat($"Strip[{micStrip}].{_config.GameBus}", micB1 ? 1 : 0))
            {
                Footer.Text = "MICROPHONE ROUTE RESTORE FAILED · Saved prior state kept for retry.";
                return;
            }
            _config.MicrophoneStripIndex = _config.AutoMicrophonePreviousStripIndex;
            _config.AutoMicrophoneStrip = null;
            _config.AutoMicrophonePreviousDeviceName = null;
            _config.AutoMicrophonePreviousA1 = null;
            _config.AutoMicrophonePreviousB1 = null;
            _config.AutoMicrophonePreviousStripIndex = null;
            await _store.SaveAsync(_config);
        }
        if (_config.AutoMusicRouteStrip is { } autoStrip)
        {
            await StopGameOutputAsync();
            if (_config.AutoMusicPreviousA1 is { } priorA1 && !_vm.TrySetParameterFloat($"Strip[{autoStrip}].A1", priorA1 ? 1 : 0) ||
                _config.AutoMusicPreviousB1 is { } priorB1 && !_vm.TrySetParameterFloat($"Strip[{autoStrip}].{_config.GameBus}", priorB1 ? 1 : 0))
            {
                Footer.Text = "AUTO MUSIC ROUTE RESTORE FAILED · Settings kept for retry.";
                return;
            }
            _config.MpvAudioDeviceName = _config.AutoMusicPreviousHeadsetDeviceName;
            _config.GameMpvAudioDeviceName = _config.AutoMusicPreviousGameDeviceName;
            _config.MusicStripIndex = _config.AutoMusicPreviousStripIndex;
            if (_mpvProvider is not null) try { await _mpvProvider.SetAudioDeviceAsync(_config.MpvAudioDeviceName ?? "auto"); }
                catch (Exception error) { Footer.Text = "HEADSET DEVICE RESTORE NEEDS ATTENTION · " + error.Message; return; }
            _config.AutoMusicRouteStrip = null;
            _config.AutoMusicPreviousA1 = null;
            _config.AutoMusicPreviousB1 = null;
            _config.AutoMusicPreviousHeadsetDeviceName = null;
            _config.AutoMusicPreviousGameDeviceName = null;
            _config.AutoMusicPreviousStripIndex = null;
            await _store.SaveAsync(_config);
            await LoadMpvOutputsAsync();
            PopulateMusicStrips();
        }
        foreach (var (strip, route) in _setupChangedRoutes.AsEnumerable().Reverse().ToList())
        {
            try { routes.Restore(strip, route, _setupRouteContext); _setupChangedRoutes.Remove((strip, route)); }
            catch (Exception error) { Footer.Text = $"ROUTE RESTORE NEEDS ATTENTION · {error.Message}"; RefreshSetupWizard(); return; }
        }
        Footer.Text = "WIZARD ROUTES RESTORED TO THEIR PRIOR STATES.";
        InvalidateSignalVerification();
        UpdateMusicStripState();
        RefreshSetupWizard();
    }

    async void RefreshAudioEndpoints_Click(object s, RoutedEventArgs e) => await LoadAudioEndpointsAsync();

    async void RefreshMpvOutputs_Click(object sender, RoutedEventArgs e) => await LoadMpvOutputsAsync();

    async Task LoadMpvOutputsAsync()
    {
        try
        {
            IReadOnlyList<MpvAudioDevice> devices;
            if (_mpvProvider is not null) devices = await _mpvProvider.ListAudioDevicesAsync();
            else
            {
                await using var probe = new MpvProvider(new MpvLocator(), _config.MpvPath);
                devices = await probe.ListAudioDevicesAsync();
            }
            _loadingAudioRouteControls = true;
            MpvOutputBox.ItemsSource = devices;
            SetupMpvOutputBox.ItemsSource = devices;
            var gameChoices = new List<MpvAudioDevice> { new("off", "OFF · No separate game-music feed") };
            gameChoices.AddRange(devices.Where(x => x.Name != "auto"));
            GameMpvOutputBox.ItemsSource = gameChoices;
            SetupGameMpvOutputBox.ItemsSource = gameChoices;
            var selectedName = _config.MpvAudioDeviceName ?? "auto";
            MpvOutputBox.SelectedItem = devices.FirstOrDefault(x => x.Name == selectedName);
            SetupMpvOutputBox.SelectedItem = devices.FirstOrDefault(x => x.Name == selectedName);
            MpvOutputState.Text = MpvOutputBox.SelectedItem is MpvAudioDevice selected
                ? $"APPLIED OUTPUT · {selected.Description} · {selected.Name}"
                : $"Previously chosen MPV device '{selectedName}' is unavailable. Reapply an available device.";
            SetupMpvOutputState.Text = MpvOutputState.Text;
            GameMpvOutputBox.SelectedItem = gameChoices.FirstOrDefault(x => x.Name == (_config.GameMpvAudioDeviceName ?? "off"));
            SetupGameMpvOutputBox.SelectedItem = GameMpvOutputBox.SelectedItem;
            GameMpvOutputState.Text = _config.GameMpvAudioDeviceName is null
                ? "Game music output is off. Only your headset plays."
                : string.IsNullOrWhiteSpace(_config.MpvAudioDeviceName)
                    ? "Game output saved, but a specific headset output is required before it can play."
                : _config.GameMpvAudioDeviceName == _config.MpvAudioDeviceName
                    ? "Game output matches headset output and cannot play. Choose a different device."
                : GameMpvOutputBox.SelectedItem is MpvAudioDevice game
                    ? _gameMpvProvider is null
                        ? $"Game output saved · {game.Description}. Tune a local station and verify its Voicemeeter strip and game bus."
                        : $"Game music player active · {game.Description}. Verify its Voicemeeter strip and game bus."
                    : $"Saved game output '{_config.GameMpvAudioDeviceName}' is unavailable. Game music will stay off until repaired.";
        }
        catch (Exception error)
        {
            MpvOutputState.Text = "MPV OUTPUT LIST UNAVAILABLE · " + error.Message;
            SetupMpvOutputState.Text = MpvOutputState.Text;
            GameMpvOutputState.Text = "GAME OUTPUT LIST UNAVAILABLE · " + error.Message;
        }
        finally { _loadingAudioRouteControls = false; }
    }

    async void ApplyMpvOutput_Click(object sender, RoutedEventArgs e) => await ApplyMpvOutputAsync(MpvOutputBox.SelectedItem as MpvAudioDevice);
    async void SetupApplyMpvOutput_Click(object sender, RoutedEventArgs e) => await ApplyMpvOutputAsync(SetupMpvOutputBox.SelectedItem as MpvAudioDevice);
    async void MpvOutputBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAudioRouteControls || !IsLoaded || MpvOutputBox.SelectedItem is not MpvAudioDevice device) return;
        await ApplyMpvOutputAsync(device);
    }

    async void SetupMpvOutputBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAudioRouteControls || !IsLoaded || SetupMpvOutputBox.SelectedItem is not MpvAudioDevice device) return;
        await ApplyMpvOutputAsync(device);
    }

    async void GameMpvOutputBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAudioRouteControls || !IsLoaded || GameMpvOutputBox.SelectedItem is not MpvAudioDevice device) return;
        if (device.Name == "off") await DisableGameMpvOutputAsync();
        else await ApplyGameMpvOutputAsync(device);
    }

    async void SetupGameMpvOutputBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAudioRouteControls || !IsLoaded || SetupGameMpvOutputBox.SelectedItem is not MpvAudioDevice device) return;
        if (device.Name == "off") await DisableGameMpvOutputAsync();
        else await ApplyGameMpvOutputAsync(device);
    }

    void RestoreGameOutputSelection()
    {
        _loadingAudioRouteControls = true;
        try
        {
            GameMpvOutputBox.SelectedItem = (GameMpvOutputBox.ItemsSource as IEnumerable<MpvAudioDevice>)?
                .FirstOrDefault(x => x.Name == (_config.GameMpvAudioDeviceName ?? "off"));
            SetupGameMpvOutputBox.SelectedItem = GameMpvOutputBox.SelectedItem;
        }
        finally { _loadingAudioRouteControls = false; }
    }

    void RestoreHeadsetOutputSelection()
    {
        _loadingAudioRouteControls = true;
        try
        {
            MpvOutputBox.SelectedItem = (MpvOutputBox.ItemsSource as IEnumerable<MpvAudioDevice>)?
                .FirstOrDefault(x => x.Name == (_config.MpvAudioDeviceName ?? "auto"));
        }
        finally { _loadingAudioRouteControls = false; }
    }

    async Task ApplyMpvOutputAsync(MpvAudioDevice? device)
    {
        if (device is null) { Footer.Text = "SELECT AN MPV AUDIO OUTPUT FIRST."; RestoreHeadsetOutputSelection(); return; }
        if (device.Name == _config.GameMpvAudioDeviceName)
        {
            Footer.Text = "HEADSET AND GAME MUSIC MUST USE DIFFERENT OUTPUT DEVICES.";
            RestoreHeadsetOutputSelection();
            return;
        }
        if (device.Name == "auto" && _config.GameMpvAudioDeviceName is not null)
        {
            Footer.Text = "KEEP A SPECIFIC HEADSET OUTPUT WHILE GAME MUSIC IS CONFIGURED. DEFAULT COULD OVERLAP THE GAME DEVICE.";
            RestoreHeadsetOutputSelection();
            return;
        }
        try
        {
            if (_mpvProvider is not null && (_config.MpvAudioDeviceName ?? "auto") != device.Name)
                await _mpvProvider.SetAudioDeviceAsync(device.Name);
            var next = device.Name == "auto" ? null : device.Name;
            if (_config.MpvAudioDeviceName != next) InvalidateSignalVerification();
            _config.MpvAudioDeviceName = next;
            _loadingAudioRouteControls = true;
            try
            {
                MpvOutputBox.SelectedItem = device;
                SetupMpvOutputBox.SelectedItem = device;
                if (next is not null)
                {
                    var output = (MonitorBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.FirstOrDefault(x =>
                        !x.IsInput && x.Id.Split('{').LastOrDefault() is { } guid &&
                        device.Name.Contains(guid.TrimEnd('}'), StringComparison.OrdinalIgnoreCase));
                    if (output is not null)
                    {
                        _config.MonitorDeviceId = output.Id;
                        MonitorBox.SelectedItem = output;
                        MonitorRouteText.Text = output.Name + " · active MPV output";
                    }
                }
            }
            finally { _loadingAudioRouteControls = false; }
            await _store.SaveAsync(_config);
            MpvOutputState.Text = $"APPLIED OUTPUT · {device.Description} · {device.Name}";
            SetupMpvOutputState.Text = MpvOutputState.Text;
            Footer.Text = $"MPV OUTPUT APPLIED · {device.Description}. Confirm headphone and Voicemeeter routing by listening and watching live meters.";
            RefreshSetupWizard();
        }
        catch (Exception error)
        {
            Footer.Text = $"MPV OUTPUT NOT CHANGED · {error.Message}";
            RestoreHeadsetOutputSelection();
        }
    }

    async void ApplyGameMpvOutput_Click(object sender, RoutedEventArgs e) => await ApplyGameMpvOutputAsync(GameMpvOutputBox.SelectedItem as MpvAudioDevice);

    async Task ApplyGameMpvOutputAsync(MpvAudioDevice? device)
    {
        if (device is null || device.Name is "auto" or "off")
        {
            Footer.Text = "SELECT A SPECIFIC GAME MUSIC OUTPUT FIRST.";
            RestoreGameOutputSelection();
            return;
        }
        if (string.IsNullOrWhiteSpace(_config.MpvAudioDeviceName))
        {
            Footer.Text = "APPLY A SPECIFIC HEADSET OUTPUT FIRST. DEFAULT MAY CHANGE TO THE SAME DEVICE AS GAME MUSIC.";
            RestoreGameOutputSelection();
            return;
        }
        if (device.Name == (_config.MpvAudioDeviceName ?? "auto"))
        {
            Footer.Text = "HEADSET AND GAME MUSIC MUST USE DIFFERENT OUTPUT DEVICES.";
            RestoreGameOutputSelection();
            return;
        }
        if (_config.GameMpvAudioDeviceName != device.Name) InvalidateSignalVerification();
        _config.GameMpvAudioDeviceName = device.Name;
        ShowGameMasterLevel(_config.GameMasterVolume);
        await _store.SaveAsync(_config);
        if (_active is { ProviderId: "mpv" } station && _mpvProvider is { } headset)
        {
            await LoadGameOutputAsync(station, headset);
            if (_gameMpvProvider is not null && station.Runtime.WasPlaying)
            {
                try { await _gameMpvProvider.PlayAsync(); await SyncGameToHeadsetAsync(headset); }
                catch (Exception error) { await StopGameOutputAsync(); GameMpvOutputState.Text = "Game music stopped: " + error.Message; }
            }
        }
        else if (_active is { ProviderId: "youtube" } youtubeStation)
        {
            await StopGameOutputAsync();
            await StartYouTubeGameFeedAsync(youtubeStation);
        }
        else GameMpvOutputState.Text = $"Game output saved · {device.Description}. Tune a local station to test it.";
        UpdateMusicStripState();
        _loadingAudioRouteControls = true;
        SetupGameMpvOutputBox.SelectedItem = (SetupGameMpvOutputBox.ItemsSource as IEnumerable<MpvAudioDevice>)?.FirstOrDefault(x => x.Name == device.Name);
        _loadingAudioRouteControls = false;
        RefreshSetupWizard();
    }

    async void DisableGameMpvOutput_Click(object sender, RoutedEventArgs e) => await DisableGameMpvOutputAsync();

    async Task DisableGameMpvOutputAsync()
    {
        await StopGameOutputAsync();
        InvalidateSignalVerification();
        _config.GameMpvAudioDeviceName = null;
        ShowGameMasterLevel(_config.GameMasterVolume);
        _loadingAudioRouteControls = true;
        GameMpvOutputBox.SelectedItem = (GameMpvOutputBox.ItemsSource as IEnumerable<MpvAudioDevice>)?.FirstOrDefault(x => x.Name == "off");
        SetupGameMpvOutputBox.SelectedItem = (SetupGameMpvOutputBox.ItemsSource as IEnumerable<MpvAudioDevice>)?.FirstOrDefault(x => x.Name == "off");
        _loadingAudioRouteControls = false;
        await _store.SaveAsync(_config);
        GameMpvOutputState.Text = "Game music output is off. Only your headset plays.";
        Footer.Text = "GAME MUSIC OUTPUT OFF · Headset playback continues.";
        UpdateMusicStripState();
        RefreshSetupWizard();
    }

    async Task LoadAudioEndpointsAsync()
    {
        try
        {
            var endpoints = await _audioEndpoints.DiscoverAsync();
            var inputs = endpoints.Where(x => x.IsInput).ToList();
            var outputs = endpoints.Where(x => !x.IsInput).ToList();
            _loadingAudioRouteControls = true;
            MicrophoneBox.ItemsSource = inputs;
            MonitorBox.ItemsSource = outputs;
            MicrophoneBox.SelectedItem = inputs.FirstOrDefault(x => x.Id == _config.MicrophoneDeviceId);
            MonitorBox.SelectedItem = outputs.FirstOrDefault(x => x.Id == _config.MonitorDeviceId);
            _loadingSetupControls = true;
            SetupMicrophoneBox.ItemsSource = inputs;
            SetupMonitorBox.ItemsSource = outputs;
            SetupMicrophoneBox.SelectedItem = inputs.FirstOrDefault(x => x.Id == _config.MicrophoneDeviceId);
            SetupMonitorBox.SelectedItem = outputs.FirstOrDefault(x => x.Id == _config.MonitorDeviceId);
            _loadingSetupControls = false;
            _loadingAudioRouteControls = false;
            MicrophoneRouteText.Text = MicrophoneBox.SelectedItem is WindowsAudioEndpoint mic ? mic.Name : "Not selected";
            MonitorRouteText.Text = MonitorBox.SelectedItem is WindowsAudioEndpoint monitor ? monitor.Name : "Not selected";
            var gameOutput = endpoints.FirstOrDefault(x => x.IsInput && x.Name.Contains($"Out {_config.GameBus}", StringComparison.OrdinalIgnoreCase));
            _gameOutputEndpointName = gameOutput?.Name;
            _gameOutputEndpointId = gameOutput?.Id;
            GameRouteText.Text = gameOutput is null
                ? $"{_config.GameBus} output device not found"
                : $"{_config.GameBus} · {gameOutput.Name} · needs verification";
            Footer.Text = $"{inputs.Count} INPUT AND {outputs.Count} OUTPUT AUDIO ENDPOINT(S) DISCOVERED.";
            RefreshSetupWizard();
        }
        catch (Exception ex)
        {
            Footer.Text = "AUDIO ENDPOINT DISCOVERY FAILED · " + ex.Message;
        }
        finally { _loadingAudioRouteControls = false; _loadingSetupControls = false; }
    }

    void PickAudioEndpoint(ComboBox selector, bool microphone)
    {
        var devices = (selector.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.ToList() ?? [];
        if (devices.Count == 0)
        {
            RadioDialogWindow.Inform(this, "No audio devices found", "Refresh the Windows device list, then try again.");
            return;
        }
        var picker = new AudioDevicePickerWindow(devices, microphone, (selector.SelectedItem as WindowsAudioEndpoint)?.Id) { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedEndpoint is { } selected)
            selector.SelectedItem = devices.FirstOrDefault(x => x.Id == selected.Id);
    }

    void AudioPickMicrophone_Click(object sender, RoutedEventArgs e) => PickAudioEndpoint(MicrophoneBox, true);
    void AudioPickMonitor_Click(object sender, RoutedEventArgs e) => PickAudioEndpoint(MonitorBox, false);
    void SetupPickMicrophone_Click(object sender, RoutedEventArgs e) => PickAudioEndpoint(SetupMicrophoneBox, true);
    void SetupPickMonitor_Click(object sender, RoutedEventArgs e) => PickAudioEndpoint(SetupMonitorBox, false);

    async void MicrophoneBox_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loadingAudioRouteControls || !IsLoaded || MicrophoneBox.SelectedItem is not WindowsAudioEndpoint microphone) return;
        if (!await ConfigureAutomaticMicrophoneAsync(microphone))
        {
            _loadingAudioRouteControls = true;
            MicrophoneBox.SelectedItem = (MicrophoneBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.FirstOrDefault(x => x.Id == _config.MicrophoneDeviceId);
            _loadingAudioRouteControls = false;
            return;
        }
        MicrophoneRouteText.Text = microphone.Name + " · connected to Voicemeeter B1";
        _loadingSetupControls = true;
        SetupMicrophoneBox.SelectedItem = (SetupMicrophoneBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.FirstOrDefault(x => x.Id == microphone.Id);
        _loadingSetupControls = false;
        RefreshSetupWizard();
    }

    async void MonitorBox_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loadingAudioRouteControls || !IsLoaded || MonitorBox.SelectedItem is not WindowsAudioEndpoint monitor) return;
        var guid = monitor.Id.Split('{').LastOrDefault()?.TrimEnd('}');
        var mpvDevice = (MpvOutputBox.ItemsSource as IEnumerable<MpvAudioDevice>)?.FirstOrDefault(x =>
            !string.IsNullOrWhiteSpace(guid) && x.Name.Contains(guid, StringComparison.OrdinalIgnoreCase));
        if (mpvDevice is null)
        {
            Footer.Text = "HEADSET NOT SWITCHED · MPV could not match this Windows output. Refresh the device list or choose its exact MPV output under Advanced.";
            _loadingAudioRouteControls = true;
            MonitorBox.SelectedItem = (MonitorBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.FirstOrDefault(x => x.Id == _config.MonitorDeviceId);
            _loadingAudioRouteControls = false;
            return;
        }
        await ApplyMpvOutputAsync(mpvDevice);
        if (_config.MpvAudioDeviceName != mpvDevice.Name)
        {
            _loadingAudioRouteControls = true;
            MonitorBox.SelectedItem = (MonitorBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.FirstOrDefault(x => x.Id == _config.MonitorDeviceId);
            _loadingAudioRouteControls = false;
            return;
        }
        if (_config.MonitorDeviceId != monitor.Id) InvalidateSignalVerification();
        _config.MonitorDeviceId = monitor.Id;
        MonitorRouteText.Text = monitor.Name + " · active MPV output";
        _loadingSetupControls = true;
        SetupMonitorBox.SelectedItem = (SetupMonitorBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.FirstOrDefault(x => x.Id == monitor.Id);
        _loadingSetupControls = false;
        await _store.SaveAsync(_config);
        RefreshSetupWizard();
    }

    async void SetupMicrophoneBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSetupControls || !IsLoaded || SetupMicrophoneBox.SelectedItem is not WindowsAudioEndpoint microphone) return;
        if (!await ConfigureAutomaticMicrophoneAsync(microphone))
        {
            _loadingSetupControls = true;
            SetupMicrophoneBox.SelectedItem = (SetupMicrophoneBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.FirstOrDefault(x => x.Id == _config.MicrophoneDeviceId);
            _loadingSetupControls = false;
            return;
        }
        _loadingAudioRouteControls = true;
        MicrophoneBox.SelectedItem = (MicrophoneBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.FirstOrDefault(x => x.Id == microphone.Id);
        _loadingAudioRouteControls = false;
        MicrophoneRouteText.Text = microphone.Name + " · connected to Voicemeeter B1";
        RefreshSetupWizard();
    }

    async void SetupMonitorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSetupControls || !IsLoaded || SetupMonitorBox.SelectedItem is not WindowsAudioEndpoint monitor) return;
        var guid = monitor.Id.Split('{').LastOrDefault()?.TrimEnd('}');
        var mpvDevice = (SetupMpvOutputBox.ItemsSource as IEnumerable<MpvAudioDevice>)?.FirstOrDefault(x =>
            !string.IsNullOrWhiteSpace(guid) && x.Name.Contains(guid, StringComparison.OrdinalIgnoreCase));
        if (mpvDevice is null)
        {
            Footer.Text = "HEADPHONES NOT SWITCHED · MPV could not match this output. Refresh device lists or use the exact player output below.";
            RestoreSetupMonitorSelection();
            return;
        }
        await ApplyMpvOutputAsync(mpvDevice);
        if (_config.MpvAudioDeviceName != mpvDevice.Name) { RestoreSetupMonitorSelection(); return; }
        if (_config.MonitorDeviceId != monitor.Id) InvalidateSignalVerification();
        _config.MonitorDeviceId = monitor.Id;
        _loadingAudioRouteControls = true;
        MonitorBox.SelectedItem = (MonitorBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.FirstOrDefault(x => x.Id == monitor.Id);
        _loadingAudioRouteControls = false;
        MonitorRouteText.Text = monitor.Name + " · active MPV output";
        await _store.SaveAsync(_config);
        RefreshSetupWizard();
    }

    void RestoreSetupMonitorSelection()
    {
        _loadingSetupControls = true;
        SetupMonitorBox.SelectedItem = (SetupMonitorBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.FirstOrDefault(x => x.Id == _config.MonitorDeviceId);
        _loadingSetupControls = false;
    }

    static string? FindVoicemeeterExecutable(string? edition)
    {
        var executableNames = edition switch
        {
            "Banana" => new[] { "voicemeeterpro_x64.exe", "voicemeeterpro.exe" },
            "Potato" => new[] { "voicemeeter8x64.exe", "voicemeeter8.exe" },
            _ => new[] { "voicemeeter_x64.exe", "voicemeeter.exe" }
        };
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        return roots.SelectMany(root => executableNames.Select(name => Path.Combine(root, "VB", "Voicemeeter", name))).FirstOrDefault(File.Exists);
    }

    void OpenVm_Click(object s, RoutedEventArgs e)
    {
        var path = FindVoicemeeterExecutable(_vm.Probe().Edition);
        if (path is not null) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); else Footer.Text = "VOICEMEETER APPLICATION WAS NOT FOUND.";
    }

    async void SaveSettings_Click(object s, RoutedEventArgs e)
    {
        _config.MpvPath = MpvPathBox.Text;
        MpvProviderState.Text = new MpvLocator().Find(_config.MpvPath) is null ? "NEEDS SETUP" : "READY";
        _config.MasterVolume = MasterVolume.Value;
        _config.GameMasterVolume = GameMasterVolume.Value;
        _config.MicrophoneVolume = MicrophoneVolume.Value;
        _config.CrossfadeEnabled = CrossfadeCheck.IsChecked == true;
        _config.CrossfadeSeconds = CrossfadeDurationSlider.Value;
        if (CrossfadeCurveBox.SelectedItem is TransitionCurve curve) _config.Curve = curve;
        _config.DefaultPlaybackMode = RadioMode.IsChecked == true ? PlaybackMode.Radio :
            RestartTrackMode.IsChecked == true ? PlaybackMode.RestartTrack : PlaybackMode.Player;
        await _store.SaveAsync(_config);
        Footer.Text = "SETTINGS SAVED ATOMICALLY.";
    }

    async void PlaybackMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingPlaybackSettings || !IsLoaded || RadioMode is null || PlayerMode is null || RestartTrackMode is null) return;
        _config.DefaultPlaybackMode = RadioMode.IsChecked == true ? PlaybackMode.Radio :
            RestartTrackMode.IsChecked == true ? PlaybackMode.RestartTrack : PlaybackMode.Player;
        await _store.SaveAsync(_config);
        Footer.Text = $"PLAYBACK MODE SAVED · {_config.DefaultPlaybackMode}.";
    }

    async void CrossfadeSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingPlaybackSettings || !IsLoaded || CrossfadeCheck is null) return;
        _config.CrossfadeEnabled = CrossfadeCheck.IsChecked == true;
        await _store.SaveAsync(_config);
        Footer.Text = _config.CrossfadeEnabled ? "LOCAL-STATION CROSSFADES ON." : "LOCAL-STATION CROSSFADES OFF.";
    }

    void CrossfadeDuration_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingPlaybackSettings || !IsLoaded) return;
        _config.CrossfadeSeconds = e.NewValue;
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
    }

    async void CrossfadeCurve_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPlaybackSettings || !IsLoaded || CrossfadeCurveBox.SelectedItem is not TransitionCurve curve) return;
        _config.Curve = curve;
        await _store.SaveAsync(_config);
        Footer.Text = $"CROSSFADE CURVE SAVED · {curve}.";
    }

    async void MpvPathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loadingPlaybackSettings || !IsLoaded) return;
        _config.MpvPath = MpvPathBox.Text.Trim();
        MpvProviderState.Text = new MpvLocator().Find(_config.MpvPath) is null ? "NEEDS SETUP" : "READY";
        await _store.SaveAsync(_config);
        Footer.Text = "MPV PATH SAVED · Applies to the next player launch.";
    }

    void ShowHeadsetMasterLevel(double value)
    {
        _syncingMasterVolumeSliders = true;
        try { MasterVolume.Value = value; DashboardHeadsetMasterVolume.Value = value; }
        finally { _syncingMasterVolumeSliders = false; }
        MasterVolumeText.Text = $"{Math.Round(value * 100):0}% · Changes headset music live and saves automatically.";
        DashboardHeadsetMasterVolumeText.Text = $"{Math.Round(value * 100):0}% · Station music you hear";
    }

    void ShowGameMasterLevel(double value)
    {
        _syncingMasterVolumeSliders = true;
        try { GameMasterVolume.Value = value; DashboardGameMasterVolume.Value = value; }
        finally { _syncingMasterVolumeSliders = false; }
        GameMasterVolumeText.Text = $"{Math.Round(value * 100):0}% · Changes game music live when connected and saves automatically.";
        DashboardGameMasterVolumeText.Text = _config.GameMpvAudioDeviceName is null
            ? $"{Math.Round(value * 100):0}% · Game output off; level saved for later"
            : $"{Math.Round(value * 100):0}% · Game music feed";
    }

    void ShowMicrophoneVolume(double value)
    {
        value = Math.Clamp(double.IsFinite(value) ? value : 1, 0, MicrophoneLevel.Maximum);
        _syncingMicrophoneVolumeSliders = true;
        try
        {
            MicrophoneVolume.Value = value;
            DashboardMicrophoneVolume.Value = value;
            AudioMicrophoneVolume.Value = value;
            SetupMicrophoneVolume.Value = value;
        }
        finally { _syncingMicrophoneVolumeSliders = false; }
        var label = $"{Math.Round(value * 100):0}%";
        MicrophoneVolumeText.Text = $"{label} · Voicemeeter mic-strip gain; boosts above 100% can clip.";
        DashboardMicrophoneVolumeText.Text = $"{label} · Voicemeeter mic → game voice";
        AudioMicrophoneVolumeText.Text = $"{label} · Applies to the selected Voicemeeter mic strip immediately";
        SetupMicrophoneVolumeText.Text = $"{label} · Changes the Voicemeeter mic strip immediately";
    }

    async Task InitializeMicrophoneVolumeAsync()
    {
        if (_config.MicrophoneStripIndex is null || !_vm.Probe().Connected) return;
        if (!_config.MicrophoneVolumeInitialized)
        {
            if (!_vm.TryGetParameterFloat($"Strip[{_config.MicrophoneStripIndex}].Gain", out var currentDb)) return;
            _config.MicrophoneVolume = Math.Clamp(Math.Pow(10, currentDb / 20), 0, MicrophoneLevel.Maximum);
            _config.MicrophoneVolumeInitialized = true;
            ShowMicrophoneVolume(_config.MicrophoneVolume);
            await _store.SaveAsync(_config);
        }
        else if (!ApplyMicrophoneVolumeToMixer(out var error)) Footer.Text = error;
    }

    bool ApplyMicrophoneVolumeToMixer(out string error)
    {
        if (_config.MicrophoneStripIndex is not { } strip)
        {
            error = "Choose a microphone/Voicemeeter input before its volume can apply.";
            return false;
        }
        if (!_vm.TrySetParameterFloat($"Strip[{strip}].Gain", MicrophoneLevel.GainDb(_config.MicrophoneVolume)))
        {
            error = "Voicemeeter mic level did not apply. Reconnect Voicemeeter and retry.";
            return false;
        }
        error = $"MICROPHONE · {Math.Round(_config.MicrophoneVolume * 100):0}% · Voicemeeter strip {strip} gain {MicrophoneLevel.GainDb(_config.MicrophoneVolume):F1} dB";
        return true;
    }

    void MicrophoneVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MicrophoneVolumeText is null || DashboardMicrophoneVolumeText is null ||
            AudioMicrophoneVolumeText is null || SetupMicrophoneVolumeText is null || _syncingMicrophoneVolumeSliders) return;
        ShowMicrophoneVolume(e.NewValue);
        if (!IsLoaded) return;
        _config.MicrophoneVolume = Math.Clamp(e.NewValue, 0, MicrophoneLevel.Maximum);
        _config.MicrophoneVolumeInitialized = true;
        if (ApplyMicrophoneVolumeToMixer(out var status)) Footer.Text = status;
        else Footer.Text = status + " Your choice is saved for when the route is available.";
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
    }

    async void MasterVolume_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MasterVolumeText is null) return;
        if (DashboardHeadsetMasterVolumeText is null || MasterVolumeText is null) return;
        MasterVolumeText.Text = $"{Math.Round(e.NewValue * 100):0}% · Changes headset music live and saves automatically.";
        DashboardHeadsetMasterVolumeText.Text = $"{Math.Round(e.NewValue * 100):0}% · Station music you hear";
        if (_syncingMasterVolumeSliders) return;
        _syncingMasterVolumeSliders = true;
        try
        {
            if (!ReferenceEquals(s, MasterVolume)) MasterVolume.Value = e.NewValue;
            if (!ReferenceEquals(s, DashboardHeadsetMasterVolume)) DashboardHeadsetMasterVolume.Value = e.NewValue;
        }
        finally { _syncingMasterVolumeSliders = false; }
        if (!IsLoaded) return;
        _config.MasterVolume = e.NewValue;
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
        if (_mpvProvider is not { } provider || _active is not { } station)
        {
            if (_active?.ProviderId == "youtube" && _youtubePlayerReady)
            {
                try
                {
                    await ApplyYouTubeListeningGainAsync(_active);
                    var reported = await YouTubeView.CoreWebView2.ExecuteScriptAsync("window.wardogs?.getVolume()");
                    Footer.Text = $"YOUTUBE HEADSET LEVEL · {Math.Round(e.NewValue * 100):0}% master · Player reports {reported}%";
                }
                catch (Exception error) { Footer.Text = "COULD NOT SET YOUTUBE LEVEL · " + error.Message; }
            }
            else if (_active is not null) Footer.Text = "HEADSET MASTER LEVEL SAVED; this provider has no connected volume control.";
            return;
        }
        try
        {
            await provider.SetVolumeAsync(e.NewValue * station.Volume);
            var reported = await provider.ReadVolumeAsync();
            var muted = await provider.ReadMuteAsync();
            Footer.Text = $"HEADSET MASTER · {Math.Round(e.NewValue * 100):0}% · Engine {Math.Round(reported * 100):0}%{(muted ? " · MUTED" : "")}";
        }
        catch (Exception error) { Footer.Text = "COULD NOT SET LIVE MUSIC LEVEL · " + error.Message; }
    }

    async void GameMasterVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GameMasterVolumeText is null) return;
        if (DashboardGameMasterVolumeText is null || GameMasterVolumeText is null) return;
        GameMasterVolumeText.Text = $"{Math.Round(e.NewValue * 100):0}% · Changes game music live when connected and saves automatically.";
        DashboardGameMasterVolumeText.Text = _config.GameMpvAudioDeviceName is null
            ? $"{Math.Round(e.NewValue * 100):0}% · Game output off; level saved for later"
            : $"{Math.Round(e.NewValue * 100):0}% · Game music feed";
        if (_syncingMasterVolumeSliders) return;
        _syncingMasterVolumeSliders = true;
        try
        {
            if (!ReferenceEquals(sender, GameMasterVolume)) GameMasterVolume.Value = e.NewValue;
            if (!ReferenceEquals(sender, DashboardGameMasterVolume)) DashboardGameMasterVolume.Value = e.NewValue;
        }
        finally { _syncingMasterVolumeSliders = false; }
        if (!IsLoaded) return;
        _config.GameMasterVolume = e.NewValue;
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
        if (_gameMpvProvider is not { } game || _active is not { } station)
        {
            if (_active?.ProviderId == "youtube" && _youtubeGameFeed is not null)
            {
                try { _youtubeGameFeed.SetVolume(e.NewValue * _active.GameVolume * (_outputHealth?.ProtectionGain ?? 1)); Footer.Text = $"YOUTUBE GAME FEED LEVEL · {Math.Round(e.NewValue * 100):0}%"; }
                catch (Exception error) { Footer.Text = "COULD NOT SET YOUTUBE GAME FEED LEVEL · " + error.Message; }
            }
            else if (_active?.ProviderId == "youtube") Footer.Text = "YOUTUBE GAME FEED UNAVAILABLE · " + (_youtubeGameFeedError ?? "Choose and verify a Voicemeeter game output.");
            return;
        }
        try
        {
            await game.SetVolumeAsync(e.NewValue * station.GameVolume * (_outputHealth?.ProtectionGain ?? 1));
            var reported = await game.ReadVolumeAsync();
            var muted = await game.ReadMuteAsync();
            Footer.Text = $"GAME MASTER · {Math.Round(e.NewValue * 100):0}% · Engine {Math.Round(reported * 100):0}%{(muted ? " · MUTED" : "")}";
        }
        catch (Exception error) { Footer.Text = "COULD NOT SET LIVE GAME LEVEL · " + error.Message; }
    }

    void OpenSoundCloudSetup_Click(object s, RoutedEventArgs e) => RadioDialogWindow.Inform(this, "SoundCloud needs setup", "SoundCloud playback needs an app connection that has not been provided. You can save a station as a draft, but it cannot play yet.");
    void OpenAppleSetup_Click(object s, RoutedEventArgs e) => RadioDialogWindow.Inform(this, "Apple Music needs setup", "Apple Music playback needs owner-managed MusicKit access and account authorization. You can save a station as a draft, but it cannot play yet.");

    void RunDiagnostics_Click(object s, RoutedEventArgs e) => RunDiagnostics();

    async void ScanExternalDiagnostics_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var checks = _diagnostics.Collect(_config, _mpvProvider?.Snapshot, _youtubeReady, _outputHealth).ToList();
            var sessions = await new PowerShellMediaSessionBackend().DiscoverAsync();
            if (sessions.Count == 0) checks.Add(new DiagnosticItem("External Audio", "NOT TESTED", "No Windows media sessions found", "Start a supported media application, then retry. Process-loopback capture is intentionally not replaced with system-audio capture."));
            foreach (var session in sessions) checks.Add(new DiagnosticItem("External Audio", session.CanCapture ? "PASS" : "WARNING", session.ApplicationName, $"Session: {session.SessionId}; controls: play={session.CanPlay}, pause={session.CanPause}, seek={session.CanSeek}; process capture: unavailable."));
            DiagnosticList.ItemsSource = checks;
            Footer.Text = $"EXTERNAL AUDIO DIAGNOSTICS · {sessions.Count} SESSION(S) FOUND.";
        }
        catch (Exception ex) { DiagnosticList.ItemsSource = new[] { new DiagnosticItem("External Audio", "FAIL", "Media-session scan failed", ex.Message) }; Footer.Text = "EXTERNAL AUDIO DIAGNOSTICS FAILED."; }
    }

    void UpdateGameVoiceBadge(bool connected, SignalLevel mixer, SignalLevel endpoint)
    {
        RouteStatus.Text = !connected ? $"{_config.GameBus}: DISCONNECTED"
            : _gameOutputEndpointId is null ? $"{_config.GameBus}: OUTPUT MISSING"
            : endpoint.Available && endpoint.Peak > .005f ? $"{_config.GameBus}: LIVE OUTPUT"
            : mixer.Available && mixer.Peak > .005f ? $"{_config.GameBus}: MIXER SIGNAL ONLY"
            : $"{_config.GameBus}: READY / QUIET";
        RouteStatus.ToolTip = "Live output means audio was captured from the Voicemeeter B1 Windows device. It does not prove a game received it; confirm that separately in Setup Wizard.";
    }

    void RunDiagnostics()
    {
        var checks = _diagnostics.Collect(_config, _mpvProvider?.Snapshot, _youtubeReady, _outputHealth).ToList();
        AddLimiterDiagnostic(checks);
        if (_b1AuditionErrorDetail is not null)
            checks.Add(new DiagnosticItem("B1 listening test", "WARNING",
                "The B1 listening test could not open; regular routing was left unchanged", _b1AuditionErrorDetail));
        if (_youtubePlayerErrorDetail is not null)
            checks.Add(new DiagnosticItem("YouTube playback", "WARNING",
                "The selected YouTube video could not play", _youtubePlayerErrorDetail));
        if (_youtubeStartupForensics is { } startup)
            checks.Add(new DiagnosticItem("YouTube startup timing", "INFO",
                startup.StationName, startup.Describe()));
        if (_localStartupForensics is { } localStartup)
            checks.Add(new DiagnosticItem("Local playback forensics", "INFO",
                localStartup.StationName, localStartup.Describe()));
        var vm = _vm.Probe();
        VmStatus.Text = vm.Connected ? "VM: CONNECTED" : vm.Installed ? "VM: INSTALLED" : "VM: MISSING";
        var meterMonitor = new VoicemeeterSignalMonitor(_vm);
        var mixer = vm.Connected ? meterMonitor.ReadBus(vm.Edition, _config.GameBus) : new SignalLevel(false, 0);
        if (vm.Connected && DiagnosticsView.Visibility == Visibility.Visible)
        {
            try { _meterForensics = meterMonitor.CaptureForensics(vm.Edition); }
            catch (Exception error) { checks.Add(new DiagnosticItem("Voicemeeter raw meter forensics", "WARNING", "Raw Remote API scan failed", error.Message)); }
        }
        var endpointAvailable = _gameBusEndpointPeakMeter.TryRead(_gameOutputEndpointId, out var endpointPeak);
        if (_gameBusEndpointPeakMeter.LastError is { } captureError)
            checks.Add(new DiagnosticItem("B1 output capture", "WARNING",
                "WARDOGS could not capture the B1 Windows output for its live verification meter", captureError));
        AddMeterForensicsDiagnostics(checks, vm, endpointAvailable, endpointPeak);
        DiagnosticList.ItemsSource = checks;
        UpdateGameVoiceBadge(vm.Connected, mixer, new SignalLevel(endpointAvailable, endpointPeak));
        GameRouteText.Text = _gameOutputEndpointName is null ? $"{_config.GameBus} output device not found" : $"{_config.GameBus} · {_gameOutputEndpointName} · check its live endpoint meter";
        MicrophoneRouteText.Text = MicrophoneBox.SelectedItem is WindowsAudioEndpoint mic ? mic.Name + " · Voicemeeter input → B1" : "Not selected";
        MonitorRouteText.Text = MonitorBox.SelectedItem is WindowsAudioEndpoint monitor
            ? monitor.Name + (_active?.ProviderId == "youtube" ? " · YouTube listening via Voicemeeter A1" : " · direct local player output")
            : "Not selected";
        RoutingDetail.Text = vm.Detail;
        OperationalStatus.Text = vm.Connected
            ? endpointAvailable && endpointPeak > .005f ? "Voicemeeter B1 output has live signal · Confirm game receive separately" : "Voicemeeter connected · Check B1 output and game receive"
            : vm.Installed ? "Voicemeeter installed · Connect it to continue" : "Voicemeeter not found";
        HealthText.Text = vm.Installed ? "ATTENTION · SETUP AND VERIFICATION REQUIRED" : "DEGRADED — SETUP REQUIRED";
    }

    static string RawPeak(RawMeterPath? path) => path is null ? "not mapped" :
        $"linear={path.Peak:0.000000}; dBFS={(path.Dbfs is { } db && !double.IsNegativeInfinity(db) ? db.ToString("0.0") : "-∞")}; available={path.Available}; result={path.ResultCode}; type={path.Type}; channels={path.FirstChannel}-{path.FirstChannel + path.ChannelCount - 1}";

    static string RawScan(RawLevelChannel channel) =>
        $"T{channel.Type}:C{channel.Channel}={channel.Peak:0.000000}/{(channel.Dbfs is { } db && !double.IsNegativeInfinity(db) ? db.ToString("0.0") : "-∞")}dBFS;avail={channel.Available};rc={channel.ResultCode}";

    string RouteState(int? strip, string route)
    {
        if (strip is not { } index) return "not configured";
        return _vm.TryGetParameterFloat($"Strip[{index}].{route}", out var value)
            ? value > .5f ? "ON" : "OFF" : "unavailable";
    }

    void AddMeterForensicsDiagnostics(ICollection<DiagnosticItem> checks, VoicemeeterStatus vm,
        bool windowsB1Available, float windowsB1Peak)
    {
        if (_meterForensics is not { } report)
        {
            checks.Add(new DiagnosticItem("Voicemeeter raw meter forensics", "NOT TESTED",
                "No connected raw meter snapshot", "Connect Voicemeeter and run Diagnostics while audio is playing."));
            return;
        }

        var selected = _config.MusicStripIndex is { } selectedIndex ? report.Strip(selectedIndex) : null;
        var b1 = report.Bus("B1");
        var feed = _youtubeGameFeed;
        var metadata = $"edition={vm.Edition ?? "unknown"}; MusicStripIndex={_config.MusicStripIndex?.ToString() ?? "none"}; " +
            $"MicrophoneStripIndex={_config.MicrophoneStripIndex?.ToString() ?? "none"}; GameBus={_config.GameBus}; " +
            $"GameMpvAudioDeviceName={_config.GameMpvAudioDeviceName ?? "off"}; MpvAudioDeviceName={_config.MpvAudioDeviceName ?? "default"}\n" +
            $"YouTube feed endpoint={feed?.OutputEndpointName ?? "none"} ({feed?.OutputEndpointId ?? "—"}); capturedPeak={feed?.CapturedPeak.ToString("0.000000") ?? "—"}; " +
            $"recent={feed?.HasRecentSignal.ToString() ?? "false"}; requested/effective gain={feed?.RequestedGain.ToString("0.000") ?? "—"}/{feed?.EffectiveGain.ToString("0.000") ?? "—"}\n" +
            $"selected strip A1={RouteState(_config.MusicStripIndex, "A1")}; B1={RouteState(_config.MusicStripIndex, "B1")}; selected raw: {RawPeak(selected)}\n" +
            $"Remote API B1: {RawPeak(b1)}; Windows Voicemeeter Out {_config.GameBus}: linear={windowsB1Peak:0.000000}; " +
            $"available={windowsB1Available}; headset Windows peak={(_headsetPeakMeter.TryRead(_config.MonitorDeviceId, out var headsetPeak) ? headsetPeak.ToString("0.000000") : "unavailable")}\n" +
            $"telemetry confidence={_outputTelemetry.Confidence}; {_outputTelemetry.Detail}";
        checks.Add(new DiagnosticItem("Voicemeeter raw meter forensics", _outputTelemetry.Confidence == OutputTelemetryConfidence.Conflicting ? "WARNING" : "INFO",
            "Direct strip/bus meter snapshot", metadata));
        checks.Add(new DiagnosticItem("Voicemeeter raw mapped paths", "INFO", "Every configured Banana strip and bus block",
            string.Join(Environment.NewLine, report.Strips.Concat(report.Buses).Select(path => $"{path.Name}: {RawPeak(path)}"))));
        checks.Add(new DiagnosticItem("Voicemeeter raw channel scan", "INFO", "API types 0-3, channels 0-39",
            string.Join(Environment.NewLine, report.ChannelScan.Select(RawScan))));
    }

    void AddLimiterDiagnostic(ICollection<DiagnosticItem> checks)
    {
        if (!_voicemeeterStripLimiter.IsActive) return;
        var capability = _voicemeeterStripLimiter.Probe(_config.MusicStripIndex);
        checks.Add(new DiagnosticItem("Voicemeeter limiter", "ACTIVE",
            $"WARDOGS owns the selected music-strip limiter at {_config.ClipGuard.SafetyCeilingDbfs:0.0} dB",
            capability.Detail + " The prior mixer limiter value is retained in memory and will be restored when disabled or on clean exit."));
    }

    async void ExportDiagnostics_Click(object s, RoutedEventArgs e)
    {
        var path = await _diagnostics.ExportAsync(_config, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDOGS Radio", "Diagnostics"), _mpvProvider?.Snapshot, _youtubeReady, _outputHealth);
        if (_meterForensics is { } report)
        {
            var vm = _vm.Probe();
            var endpointAvailable = _gameBusEndpointPeakMeter.TryRead(_gameOutputEndpointId, out var endpointPeak);
            var selected = _config.MusicStripIndex is { } index ? report.Strip(index) : null;
            var feed = _youtubeGameFeed;
            var text = Environment.NewLine + "RAW VOICEMEETER METER FORENSICS" + Environment.NewLine +
                $"edition={vm.Edition}; selectedStrip={_config.MusicStripIndex}; gameBus={_config.GameBus}; " +
                $"remoteB1={RawPeak(report.Bus("B1"))}; windowsB1={endpointPeak:0.000000}; windowsB1Available={endpointAvailable}; " +
                $"confidence={_outputTelemetry.Confidence}; feedEndpoint={feed?.OutputEndpointName ?? "none"}; captured={feed?.CapturedPeak.ToString("0.000000") ?? "—"}" + Environment.NewLine +
                string.Join(Environment.NewLine, report.Strips.Concat(report.Buses).Select(path => $"{path.Name}: {RawPeak(path)}")) + Environment.NewLine +
                string.Join(Environment.NewLine, report.ChannelScan.Select(RawScan)) + Environment.NewLine;
            await File.AppendAllTextAsync(path, text);
        }
        Footer.Text = "DIAGNOSTIC REPORT EXPORTED · " + path;
    }

    void RefreshSetupWizard()
    {
        if (SetupVmState is null) return;
        var vm = _signalStatus ?? _vm.Probe();
        var mpvAvailable = new MpvLocator().Find(_config.MpvPath) is not null;
        SetupSystemChecks.Text = $"Voicemeeter: {(vm.Connected ? "Connected" : vm.Installed ? "Installed · connect next" : "Needs setup")}\nLocal music player: {(mpvAvailable ? "Ready" : "Needs setup")}\nYouTube player: {(_youtubeReady ? "Ready" : "Needs setup")}\nSoundCloud / Apple Music: Account setup unavailable until owner access is supplied";
        SetupVmState.Text = vm.Connected ? $"Connected · Voicemeeter {vm.Edition}" : vm.Installed ? "Voicemeeter found · press Connect to continue" : "Voicemeeter not found · install or start Voicemeeter Banana";
        var microphone = SetupMicrophoneBox.SelectedItem as WindowsAudioEndpoint;
        var monitor = SetupMonitorBox.SelectedItem as WindowsAudioEndpoint;
        SetupMicChoiceState.Text = microphone is null ? "Choose a microphone." : $"{microphone.Name} · {(_config.MicrophoneStripIndex is null ? "not connected to Voicemeeter" : "connected to Voicemeeter input")}";
        SetupDeviceState.Text = microphone is null || monitor is null
            ? "Choose your microphone and headphones. Each selection connects immediately; no Windows default device is changed."
            : $"Microphone: {microphone.Name} → Voicemeeter {_config.GameBus} · Headset player: {monitor.Name}.";
        SetupProviderState.Text = $"Local music: {(mpvAvailable ? "Ready" : "Needs setup")} · YouTube: {(_youtubeReady ? "Ready" : "Needs setup")} · External music apps: {_externalSessions.Count} found · SoundCloud: needs account access · Apple Music: needs account access";
        SetupStationsState.Text = $"{_config.Profile.Stations.Count} station(s) saved · {_config.Profile.Stations.Count(x => x.Enabled && ((x.PlaylistFiles?.Count ?? 0) > 0 || !string.IsNullOrWhiteSpace(x.Source)))} with a source";
        SetupControlsState.Text = $"{_config.Profile.Macros.Count} macro(s) saved. Test a binding in the Macro editor; physical controller input requires a live test.";
        SetupRoutingDevices.Text = $"Mic: {microphone?.Name ?? "not chosen"} → Voicemeeter {_config.GameBus}. Local listening: {monitor?.Name ?? "not chosen"}. Voicemeeter—not WARDOGS—mixes the mic and separate game-music strips.";
        SetupGameOutputEndpointName.Text = _gameOutputEndpointName is null
            ? $"Voicemeeter Out {_config.GameBus} was not found in Windows recording devices. Refresh devices before choosing it in your game."
            : $"{_gameOutputEndpointName} · choose this as the microphone in your game or voice app.";
        var route = new VoicemeeterRouteController(_vm);
        var micB1 = _config.MicrophoneStripIndex is { } micStrip && route.TryRead(micStrip, _config.GameBus, out var micToGame) && micToGame;
        var musicB1 = _config.MusicStripIndex is { } musicStrip && route.TryRead(musicStrip, _config.GameBus, out var musicToGame) && musicToGame;
        var musicA1 = _config.MusicStripIndex is { } listeningStrip && route.TryRead(listeningStrip, "A1", out var musicToMonitor) && musicToMonitor;
        var splitOutput = _config.GameMpvAudioDeviceName is not null;
        var directHeadset = HeadsetPlayerTargetsSelectedOutput();
        var youtubeListening = _active?.ProviderId == "youtube" && _youtubeRoute.IsActive;
        SetupGameRouteState.Text = $"{(_config.GameMpvAudioDeviceName is null ? "Second player OFF" : "Second player → " + (SetupGameMpvOutputBox.SelectedItem as MpvAudioDevice)?.Description)} · Music strip {(_config.MusicStripIndex?.ToString() ?? "not selected")}: B1 {(musicB1 ? "ON" : "OFF / UNKNOWN")}, A1 {(musicA1 ? "ON · may double headphone music" : "OFF")}.";
        SetupVerifyDescription.Text = $"Play a station and speak. Watch the game-voice mix, its Windows output, and your listening output. Moving bars show where sound reached; confirm your game receives {_config.GameBus} separately.";
        SetupListeningMeterLabel.Text = youtubeListening ? "Selected headphones · YouTube via Voicemeeter A1"
            : directHeadset ? "Selected headphones · Windows output" : "Voicemeeter listening output A1";
        SetupRouteMusicButton.Content = directHeadset ? "SEND GAME MUSIC TO B1" : "SEND MUSIC TO B1 + A1";
        SetupStripState.Text = _config.MicrophoneStripIndex is null || _config.MusicStripIndex is null
            ? "Select both Voicemeeter strips. Watch their meters move while speaking and playing a song."
            : $"Mic strip {_config.MicrophoneStripIndex}: B1 {(micB1 ? "ON" : "OFF")} · Music strip {_config.MusicStripIndex}: B1 {(musicB1 ? "ON" : "OFF")}, A1 {(musicA1 ? "ON" : "OFF")}.";
        SetupBusState.Text = youtubeListening
            ? $"Mic → {_config.GameBus}: {(micB1 ? "ON" : "OFF / UNKNOWN")} · Game music → {_config.GameBus}: {(musicB1 ? "ON" : "OFF / UNKNOWN")}. YouTube listening reaches your headphones through Voicemeeter A1; the B1 test temporarily mutes that direct listening path."
            : directHeadset
            ? $"Mic → {_config.GameBus}: {(micB1 ? "ON" : "OFF / UNKNOWN")} · Game music → {_config.GameBus}: {(musicB1 ? "ON" : "OFF / UNKNOWN")} · Music → A1: {(musicA1 ? "ON (may double headset music)" : "OFF")}. Headset music plays directly to its device."
            : $"Mic → {_config.GameBus}: {(micB1 ? "ON" : "OFF / UNKNOWN")} · Music → {_config.GameBus}: {(musicB1 ? "ON" : "OFF / UNKNOWN")} · Music → A1: {(musicA1 ? "ON" : "OFF / UNKNOWN")}. Set A1 hardware output to your headphones in Voicemeeter.";
        SetupChainTestState.Text = $"Mic: {(_sawMicSignal ? "PASS" : "NEEDS TEST")} · Music: {(_sawMusicSignal ? "PASS" : "NEEDS TEST")} · B1 mixer: {(_sawGameSignal ? "PASS" : "NEEDS TEST")} · B1 Windows output: {(_sawGameEndpointSignal ? "PASS" : "NEEDS TEST")} · Headphones: {(_sawMonitorSignal ? "PASS" : "NEEDS TEST")}";
        var missing = new List<string>();
        if (!vm.Connected) missing.Add("connect Voicemeeter");
        if (microphone is null) missing.Add("choose microphone");
        if (monitor is null) missing.Add("choose headphones");
        if (_config.MicrophoneStripIndex is null) missing.Add("assign mic strip");
        if (_config.MusicStripIndex is null) missing.Add("assign music strip");
        missing.AddRange(SetupMusicReadiness.Missing(_active, _mpvProvider is not null,
            _youtubePlayerReady, _youtubeGameFeed is { Fault: null }, splitOutput, _gameMpvProvider is not null));
        if (_config.MicrophoneStripIndex is not null && _config.MicrophoneStripIndex == _config.MusicStripIndex) missing.Add("use separate mic and music strips");
        if (!micB1) missing.Add("route mic to B1");
        if (!musicB1) missing.Add("route music to B1");
        if (!directHeadset && !musicA1) missing.Add("route music to A1");
        if (!_sawMicSignal) missing.Add("observe mic level");
        if (!_sawMusicSignal) missing.Add("observe music level");
        if (!_sawGameSignal) missing.Add("observe B1 level");
        if (!_sawGameEndpointSignal) missing.Add("observe Voicemeeter Out B1 Windows endpoint level");
        if (!_sawMonitorSignal) missing.Add(directHeadset ? "observe headphone output level" : "observe A1 level");
        if (SetupHeardMusic.IsChecked != true) missing.Add("confirm headphones by listening");
        if (SetupGameHeard.IsChecked != true) missing.Add("confirm game-voice receive");
        SetupReadiness.Text = missing.Count == 0 ? "READY · Live input and bus levels seen, routes enabled, and listening/game receive owner-confirmed." : "STILL NEEDED · " + string.Join(" · ", missing);
    }

    void InvalidateSignalVerification()
    {
        _sawMicSignal = _sawMusicSignal = _sawGameSignal = _sawGameEndpointSignal = _sawMonitorSignal = false;
        _config.SetupComplete = false;
        SetupHeardMusic.IsChecked = false;
        SetupGameHeard.IsChecked = false;
    }

    void SetupConfirmation_Changed(object sender, RoutedEventArgs e) => RefreshSetupWizard();
    void SetupSystemCheck_Click(object sender, RoutedEventArgs e)
    {
        _signalStatus = null;
        RefreshSetupWizard();
        Footer.Text = "SYSTEM CHECK UPDATED · Continue to connect Voicemeeter and choose your devices.";
    }

    async void SetupPlayTestSound_Click(object sender, RoutedEventArgs e)
    {
        if (SetupMonitorBox.SelectedItem is not WindowsAudioEndpoint output)
        {
            Footer.Text = "CHOOSE YOUR HEADPHONES BEFORE PLAYING THE TEST SOUND.";
            return;
        }
        try
        {
            SetupSoundTestState.Text = $"Playing a short test tone through {output.Name}…";
            await DeviceTestSound.PlayAsync(output.Id);
            SetupSoundTestState.Text = $"Test tone played through {output.Name}. Did you hear it? If not, choose another output.";
            Footer.Text = "LISTENING TEST FINISHED · Confirm the sound reached the selected headphones.";
        }
        catch (Exception error)
        {
            SetupSoundTestState.Text = "Test sound failed · " + error.Message;
            Footer.Text = "LISTENING TEST FAILED · Check the selected output device.";
        }
    }

    async void SetupVerifyB1_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_gameOutputEndpointId))
        {
            SetupB1TestState.Text = "Voicemeeter Out B1 was not found among Windows recording devices. Refresh the device list.";
            return;
        }
        SetupB1TestState.Text = "Checking Voicemeeter B1 and its Windows output for three seconds…";
        var mixerSeen = false;
        var endpointSeen = false;
        var end = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < end)
        {
            var status = _vm.Probe();
            var mixer = status.Connected ? new VoicemeeterSignalMonitor(_vm).ReadBus(status.Edition, _config.GameBus) : new SignalLevel(false, 0);
            mixerSeen |= mixer.Available && mixer.Peak > .005f;
            endpointSeen |= _gameBusEndpointPeakMeter.TryRead(_gameOutputEndpointId, out var peak) && peak > .005f;
            await Task.Delay(100);
        }
        _sawGameSignal |= mixerSeen;
        _sawGameEndpointSignal |= endpointSeen;
        SetupB1TestState.Text = mixerSeen && endpointSeen
            ? "PASS · Signal reached Voicemeeter B1 and its Windows output. Confirm your game receives it separately."
            : $"NOT VERIFIED · Mixer {(mixerSeen ? "PASS" : "quiet")}; Windows B1 output {(endpointSeen ? "PASS" : "quiet")}. Play a station or speak, then try again.";
        RefreshSetupWizard();
    }

    async void SetupScanExternal_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _externalSessions = await new PowerShellMediaSessionBackend().DiscoverAsync();
            RefreshSetupWizard();
            Footer.Text = _externalSessions.Count == 0 ? "NO SUPPORTED MUSIC APPS FOUND · Start one and check again." : $"{_externalSessions.Count} MUSIC APP(S) FOUND · Create an External Audio station to control one.";
        }
        catch (Exception error) { Footer.Text = "MUSIC APP CHECK FAILED · " + error.Message; }
    }

    async void SetupCreateStation_Click(object sender, RoutedEventArgs e)
    {
        var editor = new StationEditorWindow(library: _config.MusicLibrary) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not Station station) return;
        station.Order = _config.Profile.Stations.Count;
        _config.Profile.Stations.Add(station);
        MusicLibraryService.ReconcileStationLibrary(_config, station);
        MusicLibraryService.MaterializeStationPlaylist(_config, station);
        await _store.SaveAsync(_config);
        RefreshCollections();
        SetupStationBox.SelectedItem = station;
        RefreshSetupWizard();
        Footer.Text = $"{station.Name} created. Tune it here to test playback.";
    }

    bool B1HoldRequested => _b1PointerHeld || _b1KeyboardHeld;

    async void B1Hold_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button || _b1PointerHeld) return;
        _b1PointerHeld = button.CaptureMouse();
        if (!_b1PointerHeld) { SetB1AuditionUi(false, "Could not capture the button. Try holding Space while it is focused."); return; }
        await StartB1AuditionAsync();
    }

    async void B1Hold_MouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _b1PointerHeld = false;
        if (sender is Button { IsMouseCaptured: true } button) button.ReleaseMouseCapture();
        if (!B1HoldRequested) await StopB1AuditionAsync("Normal headset listening restored.");
    }

    async void B1Hold_LostCapture(object sender, MouseEventArgs e)
    {
        if (!_b1PointerHeld) return;
        _b1PointerHeld = false;
        if (!B1HoldRequested) await StopB1AuditionAsync("Normal headset listening restored.");
    }

    async void B1Hold_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Return)) return;
        e.Handled = true;
        if (_b1KeyboardHeld || e.IsRepeat) return;
        _b1KeyboardHeld = true;
        await StartB1AuditionAsync();
    }

    async void B1Hold_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Return)) return;
        e.Handled = true;
        _b1KeyboardHeld = false;
        if (!B1HoldRequested) await StopB1AuditionAsync("Normal headset listening restored.");
    }

    async void Window_Deactivated(object? sender, EventArgs e)
    {
        _b1PointerHeld = false;
        _b1KeyboardHeld = false;
        if (AudioAuditionB1Button.IsMouseCaptured) AudioAuditionB1Button.ReleaseMouseCapture();
        if (SetupAuditionB1Button.IsMouseCaptured) SetupAuditionB1Button.ReleaseMouseCapture();
        await StopB1AuditionAsync("Normal headset listening restored.");
    }

    async Task StartB1AuditionAsync()
    {
        await _b1AuditionGate.WaitAsync();
        try
        {
            if (!B1HoldRequested || _b1Audition is not null) return;
            if (string.IsNullOrWhiteSpace(_gameOutputEndpointId) || string.IsNullOrWhiteSpace(_config.MonitorDeviceId))
            {
                SetB1AuditionUi(false, "Choose your headphones and refresh devices until Voicemeeter Out B1 is present.");
                return;
            }
            if (_active?.ProviderId == "youtube" && !_youtubeRoute.IsActive)
            {
                SetB1AuditionUi(false, "B1-only testing for YouTube needs the isolated Voicemeeter headset route. Recheck Audio & Routing and retune the station.");
                return;
            }
            if (_active?.ProviderId != "youtube" && !HeadsetPlayerTargetsSelectedOutput())
            {
                SetB1AuditionUi(false, "Select your physical headphone output, then try holding to test B1 again.");
                return;
            }
            try
            {
                SetB1AuditionUi(false, "Opening the B1 listening path… keep holding the button.");
                if (_active?.ProviderId == "mpv" && _mpvProvider is { } player)
                {
                    await player.SetVolumeAsync(0);
                    _b1AuditionMutedPlayer = player;
                }
                else if (_active?.ProviderId == "youtube") _youtubeRoute.SetAuditioning(true);
                if (!B1HoldRequested) { await StopB1AuditionCoreAsync("Normal headset listening restored."); return; }
                var audition = new B1Audition();
                audition.Start(_gameOutputEndpointId, _config.MonitorDeviceId);
                _b1Audition = audition;
                _b1AuditionErrorDetail = null;
                if (!B1HoldRequested) { await StopB1AuditionCoreAsync("Normal headset listening restored."); return; }
                SetB1AuditionUi(true, "Hearing the real B1 mix at 50% preview level. Release the button to restore normal listening.");
            }
            catch (Exception error)
            {
                _b1AuditionErrorDetail = error.ToString();
                await StopB1AuditionCoreAsync("B1 listening could not open. Check that Voicemeeter Out B1 and your headphones are connected, then try again. Technical details are in Diagnostics.");
            }
        }
        finally { _b1AuditionGate.Release(); }
    }

    void SetB1AuditionUi(bool listening, string message)
    {
        var buttonText = listening ? "RELEASE TO STOP B1 TEST" : "HOLD TO TEST B1";
        SetupAuditionB1Button.Content = buttonText;
        AudioAuditionB1Button.Content = buttonText;
        var background = listening
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 69, 46))
            : (System.Windows.Media.Brush)FindResource("Panel2Brush");
        var border = (System.Windows.Media.Brush)FindResource(listening ? "OliveBrush" : "LineBrush");
        SetupAuditionB1Button.Background = background;
        AudioAuditionB1Button.Background = background;
        SetupAuditionB1Button.BorderBrush = border;
        AudioAuditionB1Button.BorderBrush = border;
        SetupAuditionB1State.Text = message;
        AudioAuditionB1State.Text = message;
    }

    async Task StopB1AuditionAsync(string message)
    {
        await _b1AuditionGate.WaitAsync();
        try
        {
            if (_b1Audition is null && _b1AuditionMutedPlayer is null && !_youtubeRoute.IsAuditioning) return;
            await StopB1AuditionCoreAsync(message);
        }
        finally { _b1AuditionGate.Release(); }
    }

    async Task StopB1AuditionCoreAsync(string message)
    {
        _b1Audition?.Dispose();
        _b1Audition = null;
        if (_youtubeRoute.IsAuditioning)
        {
            try { _youtubeRoute.SetAuditioning(false); }
            catch (Exception error) { message += " Direct listening could not be restored: " + error.Message; }
        }
        var mutedPlayer = _b1AuditionMutedPlayer;
        _b1AuditionMutedPlayer = null;
        if (mutedPlayer is not null && mutedPlayer == _mpvProvider && _active is { ProviderId: "mpv" } station)
        {
            try { await mutedPlayer.SetVolumeAsync(_config.MasterVolume * station.Volume); }
            catch (Exception error) { message += " Headset music level could not be restored: " + error.Message; }
        }
        SetB1AuditionUi(false, message);
    }

    void SetupRecheck_Click(object sender, RoutedEventArgs e) { _signalStatus = null; RefreshSignalMeters(); RunDiagnostics(); RefreshSetupWizard(); }

    async void SetupFinish_Click(object sender, RoutedEventArgs e)
    {
        RefreshSetupWizard();
        if (!SetupReadiness.Text.StartsWith("READY ·", StringComparison.Ordinal))
        {
            Footer.Text = "SETUP UNFINISHED · Complete the live checks or leave the wizard without claiming verification.";
            return;
        }
        _config.SetupComplete = true;
        await _store.SaveAsync(_config);
        Footer.Text = "SETUP VERIFIED · Live meters and owner listening checks completed.";
        Dashboard_Click(sender, e);
    }

    async void SetupTuneStation_Click(object sender, RoutedEventArgs e)
    {
        if (SetupStationBox.SelectedItem is not Station station) { Footer.Text = "Choose a configured station first."; return; }
        if (_active?.Id != station.Id) InvalidateSignalVerification();
        try { await ActivateAsync(station); Footer.Text = $"TUNED {station.Name.ToUpperInvariant()} · Check the live music meter and listen to the output."; }
        catch (Exception error) { Footer.Text = $"STATION TUNE FAILED · {error.Message}"; }
        RefreshSetupWizard();
    }

    void RestoreFromTray()
    {
        if (_closingInProgress || _closingFinalized || _exitRequested) return;
        try
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
        // A failure while cleaning up must never turn a canceled close into an
        // unhandled WPF exception. A later tray action can restore the window.
        catch (InvalidOperationException) { }
    }

    void QueueRestoreFromTray() => Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
        new Action(RestoreFromTray));

    bool EndYouTubeRoute(string failureDetail)
    {
        if (_youtubeRoute.TryEnd(out var message))
        {
            _youtubeStartupForensics?.Mark($"temporary route restore ({_youtubeRoute.LastRestoreDuration.TotalMilliseconds:0} ms)");
            _youtubeRouteRecoveryBlocked = false;
            _youtubeHeadsetRouteError = null;
            return true;
        }
        _youtubeRouteRecoveryBlocked = true;
        _youtubeHeadsetRouteError = message;
        Footer.Text = $"YOUTUBE ROUTE NEEDS RESTORATION · {failureDetail} {message}";
        return false;
    }

    void ExitFromTray()
    {
        if (_closingInProgress) return;
        _exitRequested = true;
        Close();
    }

    async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingFinalized) return;
        e.Cancel = true;
        if (!_exitRequested)
        {
            Hide();
            return;
        }
        if (_closingInProgress) return;
        _closingInProgress = true;
        IsEnabled = false;
        _playbackTimer.Stop();
        _signalTimer.Stop();
        StopNowPlayingSurface();
        _b1PointerHeld = false;
        _b1KeyboardHeld = false;
        await SilenceYouTubeForRouteTeardownAsync();
        await StopB1AuditionAsync("B1 listening preview stopped.");
        try
        {
            if (!_voicemeeterStripLimiter.TryRestore(out var limiterRestore))
                throw new InvalidOperationException(limiterRestore);
            foreach (var cancellation in _macroCancellation.Values.ToList()) cancellation.Cancel();
            var waitStarted = Stopwatch.GetTimestamp();
            while (_macroCancellation.Count > 0 && Stopwatch.GetElapsedTime(waitStarted) < TimeSpan.FromSeconds(5))
                await Task.Delay(25);
            if (_macroCancellation.Count > 0) throw new TimeoutException("A running macro has not stopped yet.");
            foreach (var macro in _config.Profile.Macros.Where(x => _macroEngine.IsActive(x.Id)))
            {
                var trigger = macro.Activation == MacroActivation.Toggle ? MacroTrigger.Press : MacroTrigger.Release;
                var result = await _macroEngine.TriggerAsync(macro, trigger);
                if (!result.Completed) throw new InvalidOperationException($"{macro.Name} could not restore its temporary state: {result.Steps.LastOrDefault(x => !x.Success)?.Message}");
            }
            if (_active is { } closingStation && _mpvProvider is { } closingProvider)
            {
                try
                {
                    var snapshot = await closingProvider.RefreshAsync();
                    closingStation.Runtime.PositionSeconds = snapshot.PositionSeconds;
                    closingStation.Runtime.SequenceIndex = Math.Clamp(closingProvider.CurrentPlaylistIndex, 0, Math.Max(0, closingProvider.LoadedFiles.Count - 1));
                    if (snapshot.DurationSeconds is > 0 && closingProvider.LoadedFiles.Count > closingStation.Runtime.SequenceIndex)
                        CacheDuration(closingProvider.LoadedFiles[closingStation.Runtime.SequenceIndex], snapshot.DurationSeconds.Value);
                    closingStation.Runtime.VirtualStartUtc = (closingStation.ModeOverride ?? _config.DefaultPlaybackMode) == PlaybackMode.Radio
                        ? DateTimeOffset.UtcNow : null;
                }
                catch (Exception error) { Footer.Text = "Could not save the final playback position: " + error.Message; }
            }
            await _store.SaveAsync(_config);
            _hotkeys?.Dispose();
            _headsetPeakMeter.Dispose();
            _gameBusEndpointPeakMeter.Dispose();
            _keyRelease?.Dispose();
            _rawControllers?.Dispose();
            if (_xinputEvents is not null) await _xinputEvents.DisposeAsync();
            try { await StopGameOutputAsync(); }
            finally { EndYouTubeRoute("The app is closing, but the temporary YouTube route still needs restoration."); }
            if (_mpvProvider is not null) await _mpvProvider.DisposeAsync();
            if (_externalProvider is not null) await _externalProvider.DisposeAsync();
            _vm.Dispose();
            _trayIcon.Visible = false;
            _trayMenu.IsOpen = false;
            _trayIcon.Dispose();
            _closingFinalized = true;
            Close();
        }
        catch (Exception ex)
        {
            Footer.Text = $"CLOSE NEEDS ATTENTION · {ex.Message}";
            RadioDialogWindow.Inform(null, "Close needs attention", $"WARDOGS Radio stayed open because it could not safely finish shutdown.\n\n{ex.Message}");
            IsEnabled = true;
            _closingInProgress = false;
            _exitRequested = false;
            QueueRestoreFromTray();
            _playbackTimer.Start();
        }
    }
}
