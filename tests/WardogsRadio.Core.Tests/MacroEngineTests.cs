using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class MacroEngineTests
{
    sealed class Handler : IMacroActionHandler
    {
        public List<ActionKind> Seen { get; } = [];
        public double Gain = .8;
        public Task<string> ExecuteAsync(RadioAction action, MacroExecutionContext context, CancellationToken ct)
        {
            Seen.Add(action.Kind);
            if (action.Kind == ActionKind.Duck) throw new InvalidOperationException("Device unavailable");
            if (action.Kind == ActionKind.SetMasterGain)
            {
                context.Remember("gain", Gain);
                Gain = action.Value ?? throw new InvalidOperationException("Gain missing");
            }
            if (action.Kind == ActionKind.RestorePreviousMasterGain)
            {
                if (!context.TryRestore<double>("gain", out var prior)) throw new InvalidOperationException("Nothing to restore");
                Gain = prior;
            }
            return Task.FromResult("Applied");
        }
    }

    [Fact]
    public async Task ActionsRunInOrderAndDelayIsReal()
    {
        var handler = new Handler(); var engine = new MacroExecutionEngine(handler);
        var macro = new RadioMacro { Actions = [new() { Kind = ActionKind.Next }, new() { Kind = ActionKind.Delay, DelayMilliseconds = 35 }, new() { Kind = ActionKind.Previous }] };
        var run = await engine.TriggerAsync(macro, MacroTrigger.Press);
        Assert.True(run.Completed);
        Assert.Equal([ActionKind.Next, ActionKind.Previous], handler.Seen);
        Assert.True(run.Duration >= TimeSpan.FromMilliseconds(25));
        Assert.Equal(3, run.Steps.Count);
    }

    [Fact]
    public async Task StopAndContinuePoliciesAreDistinct()
    {
        var handler = new Handler(); var engine = new MacroExecutionEngine(handler);
        var macro = new RadioMacro { Actions = [new() { Kind = ActionKind.Duck }, new() { Kind = ActionKind.Next }] };
        var stopped = await engine.TriggerAsync(macro, MacroTrigger.Press);
        Assert.False(stopped.Completed); Assert.Single(stopped.Steps); Assert.Single(handler.Seen);
        macro.FailurePolicy = MacroFailurePolicy.Continue;
        var continued = await engine.TriggerAsync(macro, MacroTrigger.Press);
        Assert.False(continued.Completed); Assert.Equal(2, continued.Steps.Count); Assert.Equal(ActionKind.Next, handler.Seen.Last());
    }

    [Fact]
    public async Task HoldRestoresExactPriorValueOnRelease()
    {
        var handler = new Handler { Gain = .63 }; var engine = new MacroExecutionEngine(handler);
        var macro = new RadioMacro { Activation = MacroActivation.Hold, Actions = [new() { Kind = ActionKind.SetMasterGain, Value = .18 }], ReleaseActions = [new() { Kind = ActionKind.RestorePreviousMasterGain }] };
        Assert.True((await engine.TriggerAsync(macro, MacroTrigger.Press)).Completed);
        Assert.Equal(.18, handler.Gain); Assert.True(engine.IsActive(macro.Id));
        Assert.True((await engine.TriggerAsync(macro, MacroTrigger.Release)).Completed);
        Assert.Equal(.63, handler.Gain); Assert.False(engine.IsActive(macro.Id));
    }

    [Fact]
    public async Task ToggleAlternatesOnAndOffInSession()
    {
        var handler = new Handler(); var engine = new MacroExecutionEngine(handler);
        var macro = new RadioMacro { Activation = MacroActivation.Toggle, Actions = [new() { Kind = ActionKind.Next }], OffActions = [new() { Kind = ActionKind.Previous }] };
        await engine.TriggerAsync(macro, MacroTrigger.Press);
        Assert.True(engine.IsActive(macro.Id));
        await engine.TriggerAsync(macro, MacroTrigger.Press);
        Assert.False(engine.IsActive(macro.Id));
        Assert.Equal([ActionKind.Next, ActionKind.Previous], handler.Seen);
    }

    [Fact]
    public async Task CancellationStopsRemainingActions()
    {
        var handler = new Handler(); var engine = new MacroExecutionEngine(handler); using var cts = new CancellationTokenSource(20);
        var macro = new RadioMacro { Actions = [new() { Kind = ActionKind.Delay, DelayMilliseconds = 400 }, new() { Kind = ActionKind.Next }] };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TriggerAsync(macro, MacroTrigger.Press, cts.Token));
        Assert.Empty(handler.Seen);
    }

    [Fact]
    public async Task FailedTemporaryMacroRestoresStateBeforeStopping()
    {
        var handler = new Handler { Gain = .71 }; var engine = new MacroExecutionEngine(handler);
        var macro = new RadioMacro { Activation = MacroActivation.Momentary, Actions = [new() { Kind = ActionKind.SetMasterGain, Value = .2 }, new() { Kind = ActionKind.Duck }], ReleaseActions = [new() { Kind = ActionKind.RestorePreviousMasterGain }] };
        var result = await engine.TriggerAsync(macro, MacroTrigger.Press);
        Assert.False(result.Completed);
        Assert.Equal(.71, handler.Gain);
        Assert.False(engine.IsActive(macro.Id));
        Assert.Contains(result.Steps, x => x.Kind == ActionKind.RestorePreviousMasterGain && x.Success);
    }

    [Fact]
    public async Task FailedToggleOnRunsOffActionsToRestorePriorState()
    {
        var handler = new Handler { Gain = .57 }; var engine = new MacroExecutionEngine(handler);
        var macro = new RadioMacro { Activation = MacroActivation.Toggle,
            Actions = [new() { Kind = ActionKind.SetMasterGain, Value = .2 }, new() { Kind = ActionKind.Duck }],
            OffActions = [new() { Kind = ActionKind.RestorePreviousMasterGain }] };
        var result = await engine.TriggerAsync(macro, MacroTrigger.Press);
        Assert.False(result.Completed);
        Assert.False(engine.IsActive(macro.Id));
        Assert.Equal(.57, handler.Gain);
        Assert.Contains(result.Steps, x => x.Kind == ActionKind.RestorePreviousMasterGain && x.Success);
    }

    [Fact]
    public async Task IgnoredReleaseDoesNotEmitExecutionEvents()
    {
        var engine = new MacroExecutionEngine(new Handler());
        var macro = new RadioMacro { Actions = [new() { Kind = ActionKind.Next }] };
        var starts = 0; var finishes = 0;
        engine.ExecutionStarted += (_, _) => starts++;
        engine.ExecutionFinished += (_, _) => finishes++;
        await engine.TriggerAsync(macro, MacroTrigger.Release);
        Assert.Equal(0, starts);
        Assert.Equal(0, finishes);
    }
}
