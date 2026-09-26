using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class NowPlayingSurfaceStateTests
{
    [Fact]
    public void Compact_to_expanded_and_back_is_explicit()
    {
        var state = new NowPlayingSurfaceStateMachine();
        Assert.Equal(NowPlayingSurfaceState.Expanding, state.Request(true));
        Assert.Equal(NowPlayingSurfaceState.Expanded, state.CompleteTransition());
        Assert.Equal(NowPlayingSurfaceState.Collapsing, state.Request(false));
        Assert.Equal(NowPlayingSurfaceState.Compact, state.CompleteTransition());
    }

    [Fact]
    public void Rapid_requests_finish_at_the_last_requested_state_without_fighting_transitions()
    {
        var state = new NowPlayingSurfaceStateMachine();
        state.Request(true);
        state.Request(false);
        state.Request(true);
        Assert.Equal(NowPlayingSurfaceState.Expanded, state.CompleteTransition());

        state.Request(false);
        Assert.Equal(NowPlayingSurfaceState.Compact, state.CompleteTransition());
    }

    [Fact]
    public void Reduced_motion_can_set_the_final_state_immediately()
    {
        var state = new NowPlayingSurfaceStateMachine();
        state.SetImmediately(true);
        Assert.Equal(NowPlayingSurfaceState.Expanded, state.State);
        state.SetImmediately(false);
        Assert.Equal(NowPlayingSurfaceState.Compact, state.State);
    }
}
