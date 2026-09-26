using System.Collections.Concurrent;
using System.Diagnostics;

namespace WardogsRadio.Core;

public enum MacroTrigger { Press, Release }

public sealed record MacroStepResult(int Number, ActionKind Kind, bool Success, string Message);
public sealed record MacroRunResult(Guid MacroId, MacroTrigger Trigger, bool Completed, TimeSpan Duration, IReadOnlyList<MacroStepResult> Steps)
{
    public string Summary => Completed ? $"Completed in {Duration.TotalSeconds:0.00} s" : "Macro stopped.";
}

public sealed class MacroExecutionContext
{
    readonly Dictionary<string, Stack<object>> _previous = [];

    public void Remember<T>(string key, T value) where T : notnull
    {
        if (!_previous.TryGetValue(key, out var stack)) _previous[key] = stack = new();
        stack.Push(value);
    }

    public bool TryRestore<T>(string key, out T? value)
    {
        if (_previous.TryGetValue(key, out var stack) && stack.Count > 0 && stack.Peek() is T typed)
        {
            value = typed;
            stack.Pop();
            return true;
        }
        value = default;
        return false;
    }

    public bool TryPeek<T>(string key, out T? value)
    {
        if (_previous.TryGetValue(key, out var stack) && stack.Count > 0 && stack.Peek() is T typed)
        {
            value = typed;
            return true;
        }
        value = default;
        return false;
    }
}

public interface IMacroActionHandler
{
    Task<string> ExecuteAsync(RadioAction action, MacroExecutionContext context, CancellationToken cancellationToken);
}

public sealed class MacroExecutionEngine(IMacroActionHandler handler)
{
    sealed class Runtime
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Active;
        public MacroExecutionContext? Context;
    }

    readonly ConcurrentDictionary<Guid, Runtime> _states = new();
    public event EventHandler<MacroStepResult>? StepCompleted;
    public event EventHandler<Guid>? ExecutionStarted;
    public event EventHandler<Guid>? ExecutionFinished;

    public bool IsActive(Guid id) => _states.TryGetValue(id, out var state) && state.Active;

    public async Task<MacroRunResult> TriggerAsync(RadioMacro macro, MacroTrigger trigger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(macro);
        if (!macro.Enabled) throw new InvalidOperationException($"{macro.Name} is disabled.");
        var runtime = _states.GetOrAdd(macro.Id, _ => new Runtime());
        await runtime.Gate.WaitAsync(cancellationToken);
        var started = Stopwatch.GetTimestamp();
        var steps = new List<MacroStepResult>();
        MacroExecutionContext? recoveryContext = null;
        var executionStarted = false;
        var activatingToggle = false;
        try
        {
            if (trigger == MacroTrigger.Release && macro.Activation == MacroActivation.Press)
                return new(macro.Id, trigger, true, Stopwatch.GetElapsedTime(started), steps);
            if (macro.Activation is MacroActivation.Hold or MacroActivation.Momentary)
            {
                if (trigger == MacroTrigger.Press && runtime.Active || trigger == MacroTrigger.Release && !runtime.Active)
                    return new(macro.Id, trigger, true, Stopwatch.GetElapsedTime(started), steps);
            }
            else if (trigger == MacroTrigger.Release)
                return new(macro.Id, trigger, true, Stopwatch.GetElapsedTime(started), steps);

            var releasing = trigger == MacroTrigger.Release;
            var togglingOff = macro.Activation == MacroActivation.Toggle && runtime.Active;
            activatingToggle = macro.Activation == MacroActivation.Toggle && !togglingOff;
            var actions = releasing ? macro.ReleaseActions : togglingOff ? macro.OffActions : macro.Actions;
            var context = releasing || togglingOff ? runtime.Context ?? new() : new MacroExecutionContext();
            recoveryContext = context;
            ExecutionStarted?.Invoke(this, macro.Id);
            executionStarted = true;
            var completed = true;
            for (var index = 0; index < actions.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var action = actions[index];
                MacroStepResult result;
                try
                {
                    string message;
                    if (action.Kind == ActionKind.Delay)
                    {
                        if (action.DelayMilliseconds < 0 || action.DelayMilliseconds > 600_000)
                            throw new InvalidOperationException("Delay must be between 0 and 600,000 ms.");
                        await Task.Delay(action.DelayMilliseconds, cancellationToken);
                        message = $"Waited {action.DelayMilliseconds} ms";
                    }
                    else message = await handler.ExecuteAsync(action, context, cancellationToken);
                    result = new(index + 1, action.Kind, true, message);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    result = new(index + 1, action.Kind, false, ex.Message);
                    completed = false;
                }
                steps.Add(result);
                StepCompleted?.Invoke(this, result);
                if (!result.Success && macro.FailurePolicy == MacroFailurePolicy.StopOnFailure) break;
            }
            if (completed)
            {
                if (macro.Activation is MacroActivation.Toggle or MacroActivation.Hold or MacroActivation.Momentary)
                {
                    runtime.Active = !(releasing || togglingOff);
                    runtime.Context = runtime.Active ? context : null;
                }
            }
            else if (trigger == MacroTrigger.Press && (macro.Activation is MacroActivation.Hold or MacroActivation.Momentary || activatingToggle))
            {
                await RecoverTemporaryStateAsync(activatingToggle ? macro.OffActions : macro.ReleaseActions, context, steps);
                runtime.Active = false;
                runtime.Context = null;
            }
            return new(macro.Id, trigger, completed, Stopwatch.GetElapsedTime(started), steps);
        }
        catch (OperationCanceledException) when (trigger == MacroTrigger.Press && recoveryContext is not null && (macro.Activation is MacroActivation.Hold or MacroActivation.Momentary || activatingToggle))
        {
            await RecoverTemporaryStateAsync(activatingToggle ? macro.OffActions : macro.ReleaseActions, recoveryContext, steps);
            runtime.Active = false;
            runtime.Context = null;
            throw;
        }
        finally
        {
            if (executionStarted) ExecutionFinished?.Invoke(this, macro.Id);
            runtime.Gate.Release();
        }
    }

    async Task RecoverTemporaryStateAsync(IEnumerable<RadioAction> recoveryActions, MacroExecutionContext context, List<MacroStepResult> steps)
    {
        foreach (var action in recoveryActions)
        {
            MacroStepResult result;
            try
            {
                var message = action.Kind == ActionKind.Delay ? "Skipped delay during recovery" :
                    await handler.ExecuteAsync(action, context, CancellationToken.None);
                result = new(steps.Count + 1, action.Kind, true, $"Recovery: {message}");
            }
            catch (Exception ex) { result = new(steps.Count + 1, action.Kind, false, $"Recovery failed: {ex.Message}"); }
            steps.Add(result);
            StepCompleted?.Invoke(this, result);
        }
    }

    public void Forget(Guid id) => _states.TryRemove(id, out _);
}
