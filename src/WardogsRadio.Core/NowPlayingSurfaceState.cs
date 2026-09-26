namespace WardogsRadio.Core;

/// <summary>
/// Owns the presentation transition, independently of playback state. A request made
/// during a transition is remembered and runs after the in-flight morph completes.
/// </summary>
public enum NowPlayingSurfaceState { Compact, Expanding, Expanded, Collapsing }

public sealed class NowPlayingSurfaceStateMachine
{
    public NowPlayingSurfaceState State { get; private set; } = NowPlayingSurfaceState.Compact;
    public bool RequestedExpanded { get; private set; }

    public NowPlayingSurfaceState Request(bool expanded)
    {
        RequestedExpanded = expanded;
        if (State is NowPlayingSurfaceState.Expanding or NowPlayingSurfaceState.Collapsing) return State;
        if (expanded && State == NowPlayingSurfaceState.Compact) State = NowPlayingSurfaceState.Expanding;
        else if (!expanded && State == NowPlayingSurfaceState.Expanded) State = NowPlayingSurfaceState.Collapsing;
        return State;
    }

    public NowPlayingSurfaceState CompleteTransition()
    {
        var expanded = State == NowPlayingSurfaceState.Expanding;
        State = expanded ? NowPlayingSurfaceState.Expanded : NowPlayingSurfaceState.Compact;
        if (RequestedExpanded != expanded)
            State = RequestedExpanded ? NowPlayingSurfaceState.Expanding : NowPlayingSurfaceState.Collapsing;
        return State;
    }

    public void SetImmediately(bool expanded)
    {
        RequestedExpanded = expanded;
        State = expanded ? NowPlayingSurfaceState.Expanded : NowPlayingSurfaceState.Compact;
    }
}
