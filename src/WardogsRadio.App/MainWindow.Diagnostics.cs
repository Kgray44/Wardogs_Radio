using System.Windows;
using System.Windows.Controls;
using WardogsRadio.Core;
using WardogsRadio.Diagnostics;
using WardogsRadio.Playback;

namespace WardogsRadio.App;

public partial class MainWindow
{
    DiagnosticSnapshot? _diagnosticSnapshot;

    sealed record DiagnosticRow(ReadinessCheck Check)
    {
        public string Heading => $"{(Check.IsLiveVerification && Check.Severity == ReadinessSeverity.Info ? "NOT OBSERVED THIS SESSION" : Check.Severity.ToString().ToUpperInvariant())} · {Check.Title.ToUpperInvariant()}";
        public string Summary => Check.Summary;
        public string? TechnicalDetail => Check.TechnicalDetail;
        public RepairAction? Repair => Check.Repair;
        public bool HasRepair => Check.Repair is not null && Check.Severity is not (ReadinessSeverity.Ready or ReadinessSeverity.Info or ReadinessSeverity.Optional);
        public string RepairLabel => Check.Repair switch
        {
            RepairAction.ConnectVoicemeeter => "REPAIR AUDIO BRIDGE",
            RepairAction.RefreshDevices => "REFRESH DEVICES",
            RepairAction.ChooseMicrophone => "CHOOSE MICROPHONE",
            RepairAction.ChooseHeadphones => "CHOOSE HEADPHONES",
            RepairAction.FixRoute => "FIX ROUTE",
            RepairAction.UseBundledMpv => "USE BUNDLED MPV",
            RepairAction.OpenAudioRouting => "OPEN AUDIO & ROUTING",
            RepairAction.VerifyGameInput => "SHOW GAME INPUT INSTRUCTIONS",
            RepairAction.LocateFile => "OPEN MUSIC LIBRARY",
            _ => "REPAIR"
        };
    }
    sealed record DiagnosticSection(string Title, IReadOnlyList<DiagnosticRow> Rows);

    void ShowDiagnosticSnapshot(DiagnosticSnapshot snapshot)
    {
        _diagnosticSnapshot = snapshot;
        PublishReadiness(snapshot.Readiness);
        var checks = snapshot.Readiness.Checks;
        var sections = new List<DiagnosticSection>();
        void Group(string title, IEnumerable<ReadinessCheck> matching)
        {
            var rows = matching.Select(check => new DiagnosticRow(check)).ToArray();
            if (rows.Length > 0) sections.Add(new(title, rows));
        }
        var actionable = checks.Where(check => check.BlocksCoreReadiness &&
            check.Severity is ReadinessSeverity.NeedsAction or ReadinessSeverity.Warning or ReadinessSeverity.Error or ReadinessSeverity.Unavailable)
            .Select(check => check.Id).ToHashSet();
        Group("REQUIRES ATTENTION", checks.Where(check => actionable.Contains(check.Id)));
        Group("CORE AUDIO PATH", checks.Where(check => !actionable.Contains(check.Id) && check.BlocksCoreReadiness && check.Category is
            ReadinessCategory.Application or ReadinessCategory.Voicemeeter or ReadinessCategory.Microphone or ReadinessCategory.GameVoice));
        Group("PLAYBACK & LISTENING", checks.Where(check => !actionable.Contains(check.Id) && check.BlocksCoreReadiness && check.Category is ReadinessCategory.Playback or ReadinessCategory.Listening));
        Group("LIVE VERIFICATION", checks.Where(check => check.IsLiveVerification));
        Group("LIBRARY & CONFIGURATION", checks.Where(check => check.Category is ReadinessCategory.Library or ReadinessCategory.Configuration));
        Group("OPTIONAL FEATURES", checks.Where(check => !check.BlocksCoreReadiness && !check.IsLiveVerification && check.Category is not (ReadinessCategory.Library or ReadinessCategory.Configuration)));
        DiagnosticGroups.ItemsSource = sections;
        DiagnosticList.ItemsSource = snapshot.Technical;
        DiagnosticSummary.Text = snapshot.Readiness.Overall switch
        {
            OverallReadiness.Ready => "● READY · Core audio path is working",
            OverallReadiness.NeedsVerification => "● NEEDS VERIFICATION · Finish the live checks",
            OverallReadiness.NeedsAttention => "● NEEDS ATTENTION · " + (snapshot.Readiness.FirstAction?.Title ?? "Check the audio path"),
            _ => "● OFFLINE · Voicemeeter is unavailable"
        };
        DiagnosticCounts.Text = $"{snapshot.Readiness.CorePassed} / {snapshot.Readiness.CoreTotal} configuration checks passed · " +
            $"{snapshot.Readiness.LiveVerificationPassed} / {snapshot.Readiness.LiveVerificationTotal} live checks exercised · " +
            $"{checks.Count(check => check.Severity == ReadinessSeverity.Info)} informational · " +
            $"{checks.Count(check => check.Severity == ReadinessSeverity.Optional)} optional";
    }

    async void DiagnosticRepair_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RepairAction action }) return;
        switch (action)
        {
            case RepairAction.ConnectVoicemeeter:
                Reconnect_Click(sender, e);
                return;
            case RepairAction.RefreshDevices:
                await RefreshListeningOutputDevicesAsync();
                break;
            case RepairAction.ChooseMicrophone:
            case RepairAction.ChooseHeadphones:
                Setup_Click(sender, e);
                _setupStep = 1;
                ShowSetupStep();
                return;
            case RepairAction.OpenAudioRouting:
                Audio_Click(sender, e);
                return;
            case RepairAction.FixRoute:
                if (_config.AutoMusicRouteStrip == _bridge.Topology.PreferredMusicStrip &&
                    _config.MusicStripIndex == _bridge.Topology.PreferredMusicStrip &&
                    _config.GameBus == _bridge.Topology.GameBus &&
                    _config.GameMpvAudioDeviceName is not null &&
                    (GameMpvOutputBox.ItemsSource as IEnumerable<MpvAudioDevice>)?.Any(device =>
                        device.Name == _config.GameMpvAudioDeviceName &&
                        device.Description.Contains("Voicemeeter AUX Input", StringComparison.OrdinalIgnoreCase)) == true)
                {
                    var route = await _bridge.RepairConfiguredRouteAsync(_bridge.Topology.PreferredMusicStrip,
                        _bridge.Topology.GameBus, true);
                    Footer.Text = route.Success ? "GAME MUSIC ROUTE REPAIRED · Verify live B1 signal." :
                        "GAME MUSIC ROUTE NEEDS ATTENTION · " + route.Detail;
                    break;
                }
                Setup_Click(sender, e);
                _setupStep = 2;
                ShowSetupStep();
                return;
            case RepairAction.UseBundledMpv:
                _config.MpvPath = null;
                MpvPathBox.Text = "";
                await _store.SaveAsync(_config);
                Footer.Text = "BUNDLED MPV SELECTED · Applies on next player start.";
                break;
            case RepairAction.VerifyGameInput:
                Setup_Click(sender, e);
                _setupStep = 3;
                ShowSetupStep();
                return;
            case RepairAction.LocateFile:
                Library_Click(sender, e);
                return;
        }
        RunDiagnostics();
    }
}
