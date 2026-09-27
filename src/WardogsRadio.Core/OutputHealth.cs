namespace WardogsRadio.Core;

/// <summary>How Clip Guard may react to measured game-output peaks.</summary>
public enum ClipGuardMode { Off, Monitor, Protect }

public enum ClipGuardPreset { Safe, Balanced, Loud, Custom }

public enum OutputHealthState { Unavailable, Safe, Healthy, Hot, NearClip, Clip, Protected }

public enum OutputHealthEventKind
{
    TelemetryUnavailable,
    TelemetryRestored,
    NearClipEntered,
    ClipDetected,
    ProtectionEngaged,
    ProtectionAdjusted,
    ProtectionReleased,
    Stable
}

/// <summary>Persistent user choices. Runtime reduction and session telemetry never live here.</summary>
public sealed class ClipGuardSettings
{
    public ClipGuardMode Mode { get; set; } = ClipGuardMode.Protect;
    public ClipGuardPreset Preset { get; set; } = ClipGuardPreset.Balanced;
    public double SafetyCeilingDbfs { get; set; } = -3;
    public double NearClipThresholdDbfs { get; set; } = -1;
    public double MaximumReductionDb { get; set; } = 9;
    public int AttackMilliseconds { get; set; } = 50;
    public int RecoveryDelayMilliseconds { get; set; } = 1500;
    public double RecoveryDbPerSecond { get; set; } = 1;
    public int PeakHoldMilliseconds { get; set; } = 2000;
    public int ClipLatchMilliseconds { get; set; } = 5000;
    // A strip limiter changes externally-owned Voicemeeter state. It must always be an
    // explicit user choice; the app will save and restore the prior value while leased.
    public bool LimiterEnabled { get; set; }
    public bool AutoGainEnabled { get; set; } = true;

    public void Normalize()
    {
        if (!double.IsFinite(SafetyCeilingDbfs)) SafetyCeilingDbfs = -3;
        SafetyCeilingDbfs = Math.Clamp(SafetyCeilingDbfs, -18, -.25);
        if (!double.IsFinite(NearClipThresholdDbfs)) NearClipThresholdDbfs = -1;
        NearClipThresholdDbfs = Math.Clamp(NearClipThresholdDbfs, SafetyCeilingDbfs + .1, -.01);
        if (!double.IsFinite(MaximumReductionDb)) MaximumReductionDb = 9;
        MaximumReductionDb = Math.Clamp(MaximumReductionDb, 0, 30);
        AttackMilliseconds = Math.Clamp(AttackMilliseconds, 10, 1000);
        RecoveryDelayMilliseconds = Math.Clamp(RecoveryDelayMilliseconds, 0, 30000);
        if (!double.IsFinite(RecoveryDbPerSecond)) RecoveryDbPerSecond = 1;
        RecoveryDbPerSecond = Math.Clamp(RecoveryDbPerSecond, .05, 12);
        PeakHoldMilliseconds = Math.Clamp(PeakHoldMilliseconds, 100, 10000);
        ClipLatchMilliseconds = Math.Clamp(ClipLatchMilliseconds, 250, 30000);
    }

    public void ApplyPreset(ClipGuardPreset preset)
    {
        Preset = preset;
        switch (preset)
        {
            case ClipGuardPreset.Safe: SafetyCeilingDbfs = -6; NearClipThresholdDbfs = -2; break;
            case ClipGuardPreset.Balanced: SafetyCeilingDbfs = -3; NearClipThresholdDbfs = -1; break;
            case ClipGuardPreset.Loud: SafetyCeilingDbfs = -1.5; NearClipThresholdDbfs = -.5; break;
        }
        Normalize();
    }
}

/// <summary>One real meter sample. A missing value is unavailable, never digital silence.</summary>
public readonly record struct AudioLevelSnapshot(bool Available, double LinearPeak)
{
    public static AudioLevelSnapshot Unavailable => new(false, 0);
    public double? PeakDbfs => !Available ? null : LinearPeak <= 0 ? double.NegativeInfinity : 20 * Math.Log10(LinearPeak);
    public double? DigitalHeadroomDb => PeakDbfs is not { } db || double.IsNegativeInfinity(db) ? null : Math.Max(0, -db);
    public static AudioLevelSnapshot FromLinear(bool available, double peak) =>
        available && double.IsFinite(peak) ? new(true, Math.Max(0, peak)) : Unavailable;
}

/// <summary>Composition boundary between saved user gain and transient guard gain.</summary>
public static class ClipGuardMath
{
    public static double EffectiveGameGain(double gameMaster, double stationGame, double protectionGain)
    {
        var requested = double.IsFinite(gameMaster) && double.IsFinite(stationGame) ? Math.Max(0, gameMaster) * Math.Max(0, stationGame) : 0;
        var protection = double.IsFinite(protectionGain) ? Math.Clamp(protectionGain, 0, 1) : 1;
        return requested * protection;
    }
}

public sealed record OutputHealthEvent(DateTimeOffset Timestamp, OutputHealthEventKind Kind, string Detail);

public sealed record OutputHealthSnapshot(
    AudioLevelSnapshot Music,
    AudioLevelSnapshot Microphone,
    AudioLevelSnapshot GameBus,
    double? MusicPeakHoldDbfs,
    double? MicrophonePeakHoldDbfs,
    double? GamePeakHoldDbfs,
    OutputHealthState State,
    bool ClipLatched,
    bool IndependentGameMusicPathAvailable,
    bool AutoProtectionAvailable,
    double ProtectionReductionDb,
    double ProtectionGain,
    double SessionMaximumReductionDb,
    int NearClipEvents,
    int ClipEvents,
    int ProtectionInterventions,
    double? SessionPeakDbfs,
    DateTimeOffset? LastClipAt,
    double? LastClipMusicDbfs,
    double? LastClipMicrophoneDbfs,
    double? LastClipGameDbfs,
    string? LastClipDiagnosis,
    string Diagnosis,
    IReadOnlyList<OutputHealthEvent> RecentEvents);

/// <summary>
/// Pure state machine for peak hold, event latching, classification, and the independent
/// game-music protection layer. Callers own actual player/mixer writes.
/// </summary>
public sealed class ClipGuardController
{
    const double ClipToleranceDbfs = -.1;
    const int EventLimit = 10;
    readonly Queue<OutputHealthEvent> _events = [];
    DateTimeOffset? _lastSampleAt;
    DateTimeOffset? _unsafeSince;
    DateTimeOffset? _safeSince;
    DateTimeOffset? _clipLatchedUntil;
    DateTimeOffset? _lastClipAt;
    double? _lastClipMusic;
    double? _lastClipMicrophone;
    double? _lastClipGame;
    string? _lastClipDiagnosis;
    DateTimeOffset? _musicPeakAt;
    DateTimeOffset? _microphonePeakAt;
    DateTimeOffset? _gamePeakAt;
    double? _musicPeakHold;
    double? _microphonePeakHold;
    double? _gamePeakHold;
    double? _sessionPeak;
    double _reductionDb;
    double _maximumReductionDb;
    int _nearClipEvents;
    int _clipEvents;
    int _protectionInterventions;
    bool? _telemetryAvailable;
    OutputHealthState _lastState = OutputHealthState.Unavailable;

    public double ProtectionGain => Math.Pow(10, -_reductionDb / 20d);

    public void ResetRuntimeState()
    {
        _lastSampleAt = _unsafeSince = _safeSince = _clipLatchedUntil = _lastClipAt = null;
        _musicPeakAt = _microphonePeakAt = _gamePeakAt = null;
        _musicPeakHold = _microphonePeakHold = _gamePeakHold = _sessionPeak = null;
        _lastClipMusic = _lastClipMicrophone = _lastClipGame = null;
        _lastClipDiagnosis = null;
        _reductionDb = _maximumReductionDb = 0;
        _nearClipEvents = _clipEvents = _protectionInterventions = 0;
        _telemetryAvailable = null;
        _lastState = OutputHealthState.Unavailable;
        _events.Clear();
    }

    public void ClearClipLatch() => _clipLatchedUntil = null;

    public OutputHealthSnapshot Sample(ClipGuardSettings settings, AudioLevelSnapshot music,
        AudioLevelSnapshot microphone, AudioLevelSnapshot gameBus, bool canAutomaticallyAttenuateMusic,
        DateTimeOffset now)
    {
        settings.Normalize();
        var elapsedSeconds = _lastSampleAt is { } previous ? Math.Clamp((now - previous).TotalSeconds, 0, 2) : 0;
        _lastSampleAt = now;
        TrackPeak(ref _musicPeakHold, ref _musicPeakAt, music.PeakDbfs, now, settings.PeakHoldMilliseconds);
        TrackPeak(ref _microphonePeakHold, ref _microphonePeakAt, microphone.PeakDbfs, now, settings.PeakHoldMilliseconds);
        TrackPeak(ref _gamePeakHold, ref _gamePeakAt, gameBus.PeakDbfs, now, settings.PeakHoldMilliseconds);

        var available = gameBus.Available;
        if (_telemetryAvailable != available)
        {
            _telemetryAvailable = available;
            AddEvent(now, available ? OutputHealthEventKind.TelemetryRestored : OutputHealthEventKind.TelemetryUnavailable,
                available ? "Game-bus metering restored." : "Game-bus metering unavailable; automatic protection paused.");
        }

        var peak = gameBus.PeakDbfs;
        if (peak is { } gamePeak && !double.IsNegativeInfinity(gamePeak))
            _sessionPeak = _sessionPeak is null ? gamePeak : Math.Max(_sessionPeak.Value, gamePeak);
        var rawState = Classify(gameBus, settings);
        var clipNow = rawState == OutputHealthState.Clip;
        var nearClipNow = rawState == OutputHealthState.NearClip;
        if (clipNow)
        {
            _clipLatchedUntil = now.AddMilliseconds(settings.ClipLatchMilliseconds);
            _lastClipAt = now;
            _lastClipMusic = music.PeakDbfs;
            _lastClipMicrophone = microphone.PeakDbfs;
            _lastClipGame = gameBus.PeakDbfs;
            _lastClipDiagnosis = Diagnose(music, microphone, gameBus, OutputHealthState.Clip);
            _clipEvents++;
            AddEvent(now, OutputHealthEventKind.ClipDetected, $"Game mix reached {DisplayDb(peak)} dBFS.");
        }
        else if (nearClipNow && _lastState != OutputHealthState.NearClip)
        {
            _nearClipEvents++;
            AddEvent(now, OutputHealthEventKind.NearClipEntered, $"Game mix reached {DisplayDb(peak)} dBFS.");
        }

        var overload = peak is { } db && !double.IsNegativeInfinity(db) && db >= settings.SafetyCeilingDbfs;
        // B1 can clip from a microphone alone. Reducing radio music cannot repair
        // that source and would make the requested game level misleading.
        var microphoneOnlyOverload = overload && microphone.PeakDbfs is { } microphoneDb &&
            microphoneDb >= settings.NearClipThresholdDbfs &&
            (music.PeakDbfs is null || music.PeakDbfs < -12);
        var protectionAllowed = available && canAutomaticallyAttenuateMusic && !microphoneOnlyOverload &&
            settings.Mode == ClipGuardMode.Protect && settings.AutoGainEnabled;
        UpdateProtection(settings, overload, peak, protectionAllowed, elapsedSeconds, now);

        var latched = _clipLatchedUntil is { } until && now < until;
        var state = !available ? OutputHealthState.Unavailable :
            _reductionDb > .05 ? OutputHealthState.Protected :
            latched ? OutputHealthState.Clip : rawState;
        if (state == OutputHealthState.Safe && _lastState is not (OutputHealthState.Safe or OutputHealthState.Unavailable))
            AddEvent(now, OutputHealthEventKind.Stable, "Game mix returned to a safe level.");
        _lastState = state;

        return new OutputHealthSnapshot(music, microphone, gameBus, _musicPeakHold, _microphonePeakHold,
            _gamePeakHold, state, latched, canAutomaticallyAttenuateMusic, protectionAllowed, _reductionDb, ProtectionGain,
            _maximumReductionDb, _nearClipEvents, _clipEvents, _protectionInterventions, _sessionPeak,
            _lastClipAt, _lastClipMusic, _lastClipMicrophone, _lastClipGame,
            _lastClipDiagnosis,
            Diagnose(music, microphone, gameBus, state), _events.ToArray());
    }

    static OutputHealthState Classify(AudioLevelSnapshot level, ClipGuardSettings settings)
    {
        if (!level.Available) return OutputHealthState.Unavailable;
        var db = level.PeakDbfs;
        if (db is null || double.IsNegativeInfinity(db.Value)) return OutputHealthState.Safe;
        if (db >= ClipToleranceDbfs) return OutputHealthState.Clip;
        if (db >= settings.NearClipThresholdDbfs) return OutputHealthState.NearClip;
        if (db >= settings.SafetyCeilingDbfs) return OutputHealthState.Hot;
        return db >= -6 ? OutputHealthState.Healthy : OutputHealthState.Safe;
    }

    void UpdateProtection(ClipGuardSettings settings, bool overload, double? peak, bool allowed,
        double elapsedSeconds, DateTimeOffset now)
    {
        if (!allowed)
        {
            if (_reductionDb > .001)
            {
                _reductionDb = 0;
                AddEvent(now, OutputHealthEventKind.ProtectionReleased,
                    "Automatic protection returned to neutral because it is no longer active.");
            }
            _unsafeSince = _safeSince = null;
            return;
        }
        if (overload)
        {
            _safeSince = null;
            _unsafeSince ??= now;
            // A short transient should be visible/latching, but requires a little persistence
            // before the slower automatic layer attenuates music.
            if ((now - _unsafeSince.Value).TotalMilliseconds < 125) return;
            var severity = Math.Max(.25, (peak ?? settings.SafetyCeilingDbfs) - settings.SafetyCeilingDbfs + .25);
            var attackScale = elapsedSeconds <= 0 ? 0 : elapsedSeconds / (settings.AttackMilliseconds / 1000d);
            var step = Math.Max(.08, severity * Math.Clamp(attackScale, .1, 2));
            var prior = _reductionDb;
            _reductionDb = Math.Min(settings.MaximumReductionDb, _reductionDb + step);
            _maximumReductionDb = Math.Max(_maximumReductionDb, _reductionDb);
            if (prior <= .05 && _reductionDb > .05)
            {
                _protectionInterventions++;
                AddEvent(now, OutputHealthEventKind.ProtectionEngaged, $"Attenuating game music by {_reductionDb:0.0} dB.");
            }
            else if (_reductionDb - prior >= .25)
                AddEvent(now, OutputHealthEventKind.ProtectionAdjusted, $"Game music protection is {_reductionDb:0.0} dB.");
            return;
        }

        _unsafeSince = null;
        _safeSince ??= now;
        if (_reductionDb <= .001 || (now - _safeSince.Value).TotalMilliseconds < settings.RecoveryDelayMilliseconds) return;
        var priorReduction = _reductionDb;
        _reductionDb = Math.Max(0, _reductionDb - settings.RecoveryDbPerSecond * elapsedSeconds);
        if (priorReduction > .05 && _reductionDb <= .05)
            AddEvent(now, OutputHealthEventKind.ProtectionReleased, "Game music protection returned to neutral.");
    }

    void TrackPeak(ref double? held, ref DateTimeOffset? heldAt, double? incoming, DateTimeOffset now,
        int holdMilliseconds)
    {
        if (incoming is { } peak && !double.IsNegativeInfinity(peak) && (held is null || peak >= held))
        {
            held = peak;
            heldAt = now;
        }
        else if (heldAt is { } at && now >= at.AddMilliseconds(holdMilliseconds))
        {
            held = incoming;
            heldAt = now;
        }
    }

    void AddEvent(DateTimeOffset at, OutputHealthEventKind kind, string detail)
    {
        if (_events.TryPeek(out var previous) && previous.Kind == kind && (at - previous.Timestamp).TotalMilliseconds < 750) return;
        _events.Enqueue(new OutputHealthEvent(at, kind, detail));
        while (_events.Count > EventLimit) _events.Dequeue();
    }

    static string Diagnose(AudioLevelSnapshot music, AudioLevelSnapshot microphone, AudioLevelSnapshot game,
        OutputHealthState state)
    {
        if (!game.Available) return "Game-bus metering is unavailable; automatic protection is paused.";
        if (state is OutputHealthState.Safe or OutputHealthState.Healthy) return "Game mix has usable headroom.";
        if (state == OutputHealthState.Protected) return "Clip Guard is reducing game music before the final broadcast mix.";
        var musicDb = music.PeakDbfs;
        var microphoneDb = microphone.PeakDbfs;
        if (musicDb is { } musicPeak && microphoneDb is { } micPeak && game.PeakDbfs is { } gamePeak &&
            musicPeak < -3 && micPeak < -3 && gamePeak >= -1)
            return "Combined mix overload: music and microphone are individually below the hot range, but B1 is near full scale.";
        if (microphoneDb is { } hotMicrophone && hotMicrophone >= -1 &&
            (musicDb is null || musicDb < -12))
            return "Microphone is too hot. Reduce microphone level; game-music protection cannot fix microphone-only clipping.";
        if (musicDb is { } hotMusic && hotMusic >= -1) return "Music input is too hot for the game mix.";
        return state == OutputHealthState.Clip ? "Game mix reached the digital ceiling." : "Game mix is close to the protection ceiling.";
    }

    static string DisplayDb(double? value) => value is null || double.IsNegativeInfinity(value.Value) ? "—" : value.Value.ToString("0.0");
}
