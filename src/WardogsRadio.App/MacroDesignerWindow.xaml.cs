using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WardogsRadio.Core;
using WardogsRadio.Input;

namespace WardogsRadio.App;

public partial class MacroDesignerWindow : Window
{
    sealed record Phase(string Key, string Label) { public override string ToString() => Label; }
    sealed record ActionRow(int Number, string Summary, string Issue, RadioAction Action);
    sealed record ControllerLabel(string Key, string Label) { public override string ToString() => Label; }
    readonly MacroLibraryDraft _draft;
    readonly ObservableCollection<RadioMacro> _macros;
    readonly List<Station> _stations;
    readonly Func<RadioMacro, Task<MacroRunResult>> _test;
    readonly Func<CancellationToken, Task<ControllerControlEvent?>> _captureController;
    RadioMacro? _selectedMacro;
    RadioAction? _selectedAction;
    RadioAction? _draggedAction;
    Point _dragStart;
    string _iconId = "patrol";
    string _accentColor = "#9FB672";
    bool _loading;

    public MacroDesignerWindow(RadioProfile profile, Func<RadioMacro, Task<MacroRunResult>> test,
        Func<CancellationToken, Task<ControllerControlEvent?>> captureController, Guid? selectId = null)
    {
        InitializeComponent();
        _draft = new(profile);
        _test = test;
        _captureController = captureController;
        _stations = profile.Stations.OrderBy(x => x.Order).ToList();
        _macros = _draft.Macros;
        MacroList.ItemsSource = _macros;
        StationBox.ItemsSource = _stations;
        ActionTypeBox.ItemsSource = MacroActionCatalog.All;
        if (selectId is { } id) MacroList.SelectedItem = _macros.FirstOrDefault(x => x.Id == id);
        else if (_macros.Count > 0) MacroList.SelectedIndex = 0;
    }

    static RadioAction Copy(RadioAction action) => MacroLibraryDraft.CopyAction(action);

    static void Select(ComboBox box, string key) => box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Tag?.ToString(), key, StringComparison.OrdinalIgnoreCase));
    static string SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && _selectedMacro is not null) _selectedMacro.Name = NameBox.Text.Trim();
    }

    public void FocusIssue(string issue)
    {
        if (issue.Contains("Keyboard", StringComparison.OrdinalIgnoreCase)) { KeyboardList.BringIntoView(); return; }
        if (issue.Contains("Controller", StringComparison.OrdinalIgnoreCase)) { ControllerList.BringIntoView(); return; }
        var phase = issue.Contains("Release action", StringComparison.OrdinalIgnoreCase) ? "release" :
            issue.Contains("Off action", StringComparison.OrdinalIgnoreCase) ? "off" : "press";
        PhaseBox.SelectedItem = PhaseBox.Items.OfType<Phase>().FirstOrDefault(x => x.Key == phase);
        var match = System.Text.RegularExpressions.Regex.Match(issue, @"action\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var number)) ActionsList.SelectedIndex = number - 1;
        else ActionsList.SelectedIndex = 0;
    }

    void MacroList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (!PersistCurrent()) { _loading = true; MacroList.SelectedItem = _selectedMacro; _loading = false; return; }
        _selectedMacro = MacroList.SelectedItem as RadioMacro;
        LoadMacro();
    }

    void LoadMacro()
    {
        _loading = true;
        var macro = _selectedMacro;
        NameBox.Text = macro?.Name ?? "";
        DescriptionBox.Text = macro?.Description ?? "";
        _iconId = macro?.IconId ?? "patrol";
        IconPreview.Data = IconCatalog.Get(_iconId).Shape;
        IconName.Text = IconCatalog.Get(_iconId).Name;
        _accentColor = AccentColorPickerWindow.NormalizeColor(macro?.AccentColor);
        UpdateAccent();
        Select(ActivationBox, macro?.Activation.ToString() ?? "Press");
        Select(FailureBox, macro?.FailurePolicy.ToString() ?? "StopOnFailure");
        EnabledCheck.IsChecked = macro?.Enabled ?? true;
        PinCheck.IsChecked = macro?.ShowOnDashboard ?? true;
        KeyboardList.ItemsSource = macro?.KeyboardBindings.ToList();
        RefreshControllerBindings();
        ConfigurePhases();
        TestResult.Text = "Run this macro against the current system before saving.";
        Hint.Text = macro is null ? "Create a macro to begin." : "";
        _loading = false;
    }

    void ConfigurePhases()
    {
        var activation = Enum.TryParse<MacroActivation>(SelectedTag(ActivationBox), out var parsed) ? parsed : MacroActivation.Press;
        PhaseBox.ItemsSource = activation switch
        {
            MacroActivation.Hold or MacroActivation.Momentary => new[] { new Phase("press", "ON PRESS"), new Phase("release", "ON RELEASE") },
            MacroActivation.Toggle => new[] { new Phase("press", "ON STATE"), new Phase("off", "OFF STATE") },
            _ => new[] { new Phase("press", "ON PRESS") }
        };
        PhaseBox.SelectedIndex = 0;
        ActivationHint.Text = activation switch
        {
            MacroActivation.Hold => "Runs press actions once when held; runs release actions when the control is released.",
            MacroActivation.Momentary => "Temporarily changes state while held, then restores on release.",
            MacroActivation.Toggle => "Each press alternates between the On and Off action sets. It starts Off after launch.",
            _ => "Runs the action list once each time the control is pressed."
        };
        RefreshActions();
    }

    List<RadioAction> CurrentActions() => _selectedMacro is null ? [] : (PhaseBox.SelectedItem as Phase)?.Key switch
    {
        "release" => _selectedMacro.ReleaseActions,
        "off" => _selectedMacro.OffActions,
        _ => _selectedMacro.Actions
    };

    void RefreshActions(RadioAction? select = null)
    {
        _loading = true;
        var actions = CurrentActions();
        ActionsList.ItemsSource = actions.Select((action, i) => new ActionRow(i + 1, MacroActionCatalog.Summarize(action, _stations), ActionIssue(action), action)).ToList();
        ActionsList.SelectedItem = ActionsList.Items.OfType<ActionRow>().FirstOrDefault(x => x.Action == select);
        _selectedAction = select;
        LoadAction();
        _loading = false;
        if (_selectedAction is not null) ActionConfig.BringIntoView();
    }

    string ActionIssue(RadioAction action)
    {
        var draft = new RadioMacro { Actions = [Copy(action)] };
        var issue = MacroDefinitionValidator.Validate(draft, new RadioProfile { Stations = _stations }).FirstOrDefault();
        if (issue is not null) return issue[(issue.IndexOf(':') + 1)..].Trim();
        if (MacroActionCatalog.RequiresStation(action.Kind))
        {
            var station = _stations.FirstOrDefault(x => x.Id == action.StationId);
            if (station?.Enabled == false) return "Station disabled";
            if (string.IsNullOrWhiteSpace(station?.Source)) return "Station needs a source";
        }
        return "";
    }

    void ActivationBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selectedMacro is null) return;
        if (!PersistAction()) return;
        _selectedMacro.Activation = Enum.TryParse<MacroActivation>(SelectedTag(ActivationBox), out var activation) ? activation : MacroActivation.Press;
        ConfigurePhases();
    }

    void PhaseBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (!PersistAction()) return;
        RefreshActions();
    }

    void ActionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (!PersistAction()) { _loading = true; ActionsList.SelectedItem = ActionsList.Items.OfType<ActionRow>().FirstOrDefault(x => x.Action == _selectedAction); _loading = false; return; }
        _selectedAction = (ActionsList.SelectedItem as ActionRow)?.Action;
        _loading = true;
        LoadAction();
        _loading = false;
        if (_selectedAction is not null) ActionConfig.BringIntoView();
    }

    static T? Ancestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T found) return found;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    void ActionsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(ActionsList);
        _draggedAction = Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is ActionRow row ? row.Action : null;
    }

    void ActionsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedAction is null) return;
        var position = e.GetPosition(ActionsList);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var action = _draggedAction;
        _draggedAction = null;
        if (!PersistAction()) return;
        DragDrop.DoDragDrop(ActionsList, new DataObject("WardogsMacroAction", action), DragDropEffects.Move);
    }

    void ActionsList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData("WardogsMacroAction") is RadioAction action && CurrentActions().Contains(action)
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    void ActionsList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("WardogsMacroAction") is not RadioAction action) return;
        var list = CurrentActions();
        if (!list.Contains(action)) return;
        var target = Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as ActionRow;
        if (target is null || target.Action == action) return;
        var destination = list.IndexOf(target.Action);
        list.Remove(action);
        list.Insert(destination, action);
        RefreshActions(action);
        e.Handled = true;
    }

    void LoadAction()
    {
        ActionConfig.Visibility = _selectedAction is null ? Visibility.Collapsed : Visibility.Visible;
        if (_selectedAction is null) return;
        ActionTypeBox.SelectedItem = MacroActionCatalog.Get(_selectedAction.Kind);
        StationBox.SelectedItem = _stations.FirstOrDefault(x => x.Id == _selectedAction.StationId);
        var definition = MacroActionCatalog.Get(_selectedAction.Kind);
        ValueBox.Text = MacroActionCatalog.HasDuration(_selectedAction.Kind) ? _selectedAction.DelayMilliseconds.ToString(CultureInfo.InvariantCulture) : _selectedAction.Value?.ToString(CultureInfo.InvariantCulture) ?? "";
        Select(CurveBox, _selectedAction.Curve.ToString());
        ArgumentBox.Text = _selectedAction.Argument ?? "";
        UpdateActionFields();
    }

    void ActionTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selectedAction is null || ActionTypeBox.SelectedItem is not MacroActionDefinition definition) return;
        if (!PersistAction()) return;
        _selectedAction.Kind = definition.Kind;
        _selectedAction.StationId = null;
        _selectedAction.Value = null;
        _selectedAction.DelayMilliseconds = 0;
        _selectedAction.Curve = TransitionCurve.EqualPower;
        _selectedAction.Argument = null;
        _loading = true;
        LoadAction();
        _loading = false;
        RefreshActions(_selectedAction);
    }

    void UpdateActionFields()
    {
        if (_selectedAction is null) return;
        var definition = MacroActionCatalog.Get(_selectedAction.Kind);
        StationFields.Visibility = MacroActionCatalog.RequiresStation(_selectedAction.Kind) ? Visibility.Visible : Visibility.Collapsed;
        ValueFields.Visibility = MacroActionCatalog.RequiresGain(_selectedAction.Kind) || MacroActionCatalog.HasDuration(_selectedAction.Kind) || definition.Parameter == MacroActionParameter.Seconds ? Visibility.Visible : Visibility.Collapsed;
        CurveFields.Visibility = _selectedAction.Kind == ActionKind.FadeToStation ? Visibility.Visible : Visibility.Collapsed;
        ArgumentFields.Visibility = definition.Parameter is MacroActionParameter.ApplicationPath or MacroActionParameter.Page ? Visibility.Visible : Visibility.Collapsed;
        ValueLabel.Text = _selectedAction.Kind == ActionKind.FadeToStation ? "Duration (ms; 0 uses normal transition)" :
            MacroActionCatalog.HasDuration(_selectedAction.Kind) ? "Duration (ms)" :
            definition.Parameter == MacroActionParameter.Decibels ? "Gain (dB, -60 to +12)" : "Position (seconds)";
        ArgumentLabel.Text = definition.Parameter == MacroActionParameter.Page ? "Page (dashboard, stations, macros, audio, settings, diagnostics)" : "Application path";
        ActionHelp.Text = definition.Description;
    }

    bool PersistAction()
    {
        if (_loading || _selectedAction is null) return true;
        var definition = MacroActionCatalog.Get(_selectedAction.Kind);
        if (MacroActionCatalog.RequiresStation(_selectedAction.Kind)) _selectedAction.StationId = (StationBox.SelectedItem as Station)?.Id;
        if (MacroActionCatalog.HasDuration(_selectedAction.Kind))
        {
            if (!int.TryParse(ValueBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) || ms < 0 || ms > 600_000) { Hint.Text = "Enter a duration from 0 to 600,000 ms."; return false; }
            _selectedAction.DelayMilliseconds = ms;
        }
        if (_selectedAction.Kind == ActionKind.FadeToStation)
            _selectedAction.Curve = Enum.TryParse<TransitionCurve>(SelectedTag(CurveBox), out var curve) ? curve : TransitionCurve.EqualPower;
        if (definition.Parameter is MacroActionParameter.Decibels or MacroActionParameter.Seconds)
        {
            if (!double.TryParse(ValueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) { Hint.Text = "Enter a valid number for this action."; return false; }
            if (definition.Parameter == MacroActionParameter.Decibels && (value < -60 || value > 12)) { Hint.Text = "Gain must be between -60 and +12 dB."; return false; }
            if (definition.Parameter == MacroActionParameter.Seconds && value < 0) { Hint.Text = "Position cannot be negative."; return false; }
            _selectedAction.Value = value;
        }
        if (definition.Parameter is MacroActionParameter.ApplicationPath or MacroActionParameter.Page) _selectedAction.Argument = ArgumentBox.Text.Trim();
        Hint.Text = "";
        return true;
    }

    bool PersistCurrent()
    {
        if (_selectedMacro is null || _loading) return true;
        if (!PersistAction()) return false;
        _selectedMacro.Name = NameBox.Text.Trim();
        _selectedMacro.Description = DescriptionBox.Text.Trim();
        _selectedMacro.IconId = _iconId;
        _selectedMacro.AccentColor = _accentColor;
        _selectedMacro.Enabled = EnabledCheck.IsChecked == true;
        _selectedMacro.ShowOnDashboard = PinCheck.IsChecked == true;
        _selectedMacro.Activation = Enum.TryParse<MacroActivation>(SelectedTag(ActivationBox), out var activation) ? activation : MacroActivation.Press;
        _selectedMacro.FailurePolicy = Enum.TryParse<MacroFailurePolicy>(SelectedTag(FailureBox), out var failure) ? failure : MacroFailurePolicy.StopOnFailure;
        _selectedMacro.Hotkey = _selectedMacro.KeyboardBindings.FirstOrDefault();
        return true;
    }

    void ChooseIcon_Click(object sender, RoutedEventArgs e)
    {
        var picker = new IconPickerWindow(_iconId) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedIconId is not { } id) return;
        _iconId = id;
        IconPreview.Data = IconCatalog.Get(id).Shape;
        IconName.Text = IconCatalog.Get(id).Name;
    }

    void UpdateAccent()
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_accentColor)!);
        AccentSwatch.Background = brush;
        IconPreview.Fill = brush;
        AccentLabel.Text = _accentColor.ToUpperInvariant();
    }

    void ChooseColor_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMacro is null) return;
        var picker = new AccentColorPickerWindow(_accentColor) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedColor is not { } color) return;
        _accentColor = color;
        UpdateAccent();
    }

    void Add_Click(object sender, RoutedEventArgs e) => CreateNew();
    public void CreateNew()
    {
        if (!PersistCurrent()) return;
        var macro = _draft.Create();
        MacroList.SelectedItem = macro;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (!PersistCurrent() || _selectedMacro is null) return;
        var duplicate = _draft.Duplicate(_selectedMacro);
        MacroList.SelectedItem = duplicate;
    }

    void MoveMacroUp_Click(object sender, RoutedEventArgs e) => MoveMacro(-1);
    void MoveMacroDown_Click(object sender, RoutedEventArgs e) => MoveMacro(1);
    void MoveMacro(int offset)
    {
        if (_selectedMacro is null || !PersistCurrent()) return;
        _draft.Move(_selectedMacro, offset);
        MacroList.SelectedItem = _selectedMacro;
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMacro is null) return;
        var old = _selectedMacro;
        if (!RadioDialogWindow.Confirm(this, "Delete macro?", $"Delete {old.Name}? Your stations will stay as they are.", "DELETE MACRO")) return;
        _selectedMacro = null;
        _draft.Delete(old);
        MacroList.SelectedItem = _macros.FirstOrDefault();
    }

    void AddAction_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMacro is null || !PersistAction()) return;
        var picker = new ActionPickerWindow { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedAction is not { } definition) return;
        var action = new RadioAction { Kind = definition.Kind, StationId = MacroActionCatalog.RequiresStation(definition.Kind) ? _stations.FirstOrDefault()?.Id : null,
            Value = definition.Kind == ActionKind.Duck ? -24 : definition.Parameter == MacroActionParameter.Decibels ? -9 : definition.Parameter == MacroActionParameter.Seconds ? 0 : null,
            DelayMilliseconds = definition.Kind == ActionKind.FadeToStation ? 500 : definition.Parameter == MacroActionParameter.DurationMilliseconds ? 250 : 0 };
        CurrentActions().Add(action); RefreshActions(action);
    }

    void MoveActionUp_Click(object sender, RoutedEventArgs e) => MoveAction(-1);
    void MoveActionDown_Click(object sender, RoutedEventArgs e) => MoveAction(1);
    void MoveAction(int offset)
    {
        if (_selectedAction is null || !PersistAction()) return;
        var list = CurrentActions(); var index = list.IndexOf(_selectedAction); var destination = index + offset;
        if (destination < 0 || destination >= list.Count) return;
        (list[index], list[destination]) = (list[destination], list[index]);
        RefreshActions(_selectedAction);
    }
    void DuplicateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAction is null || !PersistAction()) return;
        var list = CurrentActions(); var copy = Copy(_selectedAction); list.Insert(list.IndexOf(_selectedAction) + 1, copy); RefreshActions(copy);
    }
    void RemoveAction_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAction is null) return;
        CurrentActions().Remove(_selectedAction); RefreshActions();
    }

    void AddKey_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMacro is null) return;
        var capture = new KeyCaptureWindow { Owner = this };
        if (capture.ShowDialog() != true || capture.CapturedKey is not { } key) return;
        if (_selectedMacro.KeyboardBindings.Contains(key, StringComparer.OrdinalIgnoreCase)) { Hint.Text = "That key is already assigned to this macro."; return; }
        if (_macros.Any(x => x.Id != _selectedMacro.Id && x.KeyboardBindings.Contains(key, StringComparer.OrdinalIgnoreCase)) || _stations.Any(x => string.Equals(x.Hotkey, key, StringComparison.OrdinalIgnoreCase)))
        { Hint.Text = $"{key} is already assigned to another control."; return; }
        _selectedMacro.KeyboardBindings.Add(key); KeyboardList.ItemsSource = _selectedMacro.KeyboardBindings.ToList(); Hint.Text = "";
    }
    void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMacro is null || KeyboardList.SelectedItem is not string key) return;
        _selectedMacro.KeyboardBindings.Remove(key); KeyboardList.ItemsSource = _selectedMacro.KeyboardBindings.ToList();
    }

    void RefreshControllerBindings()
    {
        var devices = new XInputControllerService().Enumerate().ToList();
        ControllerList.ItemsSource = _selectedMacro?.ControllerBindings.Select(key => new ControllerLabel(key, ControllerBindingCodec.Display(key, devices))).ToList();
    }
    async void AddController_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMacro is null) return;
        var selectedMacro = _selectedMacro;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var captureWindow = new ControllerCaptureWindow { Owner = this };
        captureWindow.Closed += (_, _) => timeout.Cancel();
        captureWindow.Show();
        ControllerControlEvent? input;
        try { input = await _captureController(timeout.Token); }
        catch (Exception ex) { Hint.Text = ex.Message; input = null; }
        if (captureWindow.IsVisible) captureWindow.Close();
        if (!ReferenceEquals(_selectedMacro, selectedMacro)) { Hint.Text = "Selection changed while capturing. No binding was added."; return; }
        if (input is null) { Hint.Text = "No controller button was captured."; return; }
        if (_macros.Any(x => x.ControllerBindings.Contains(input.BindingKey, StringComparer.OrdinalIgnoreCase)))
        { Hint.Text = "That controller control is already bound to a macro."; return; }
        selectedMacro.ControllerBindings.Add(input.BindingKey);
        RefreshControllerBindings();
        Hint.Text = $"Bound {input.DeviceName} · {input.ControlName}.";
    }
    void RemoveController_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMacro is null || ControllerList.SelectedItem is not ControllerLabel binding) return;
        _selectedMacro.ControllerBindings.Remove(binding.Key); RefreshControllerBindings();
    }

    async void TestMacro_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMacro is null || !PersistCurrent()) return;
        TestResult.Text = "Running against the current system…";
        try
        {
            var result = await _test(_selectedMacro);
            TestResult.Text = string.Join(Environment.NewLine, result.Steps.Select(x => $"{(x.Success ? "PASS" : "FAIL")}  {MacroActionCatalog.Get(x.Kind).Name}: {x.Message}")) + Environment.NewLine + result.Summary;
        }
        catch (Exception ex) { TestResult.Text = "FAIL  " + ex.Message; }
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!PersistCurrent()) return;
        var staged = new RadioProfile { Stations = _stations, Macros = _macros.ToList() };
        var invalid = _macros.SelectMany(x => MacroDefinitionValidator.Validate(x, staged).Select(message => $"{x.Name}: {message}")).FirstOrDefault();
        if (invalid is not null) { Hint.Text = invalid; Hint.BringIntoView(); return; }
        if (_macros.Any(x => string.IsNullOrWhiteSpace(x.Name))) { Hint.Text = "Every macro needs a name."; return; }
        if (_macros.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1)) { Hint.Text = "Macro names must be unique."; return; }
        var allKeys = _macros.SelectMany(x => x.KeyboardBindings.Select(key => (key, x.Name))).ToList();
        if (allKeys.GroupBy(x => x.key, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1)) { Hint.Text = "A keyboard key is assigned to more than one macro."; return; }
        if (allKeys.Any(x => _stations.Any(s => string.Equals(s.Hotkey, x.key, StringComparison.OrdinalIgnoreCase)))) { Hint.Text = "A keyboard key conflicts with a station binding."; return; }
        _draft.Commit();
        DialogResult = true;
    }
}
