using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class VisiblePageRefreshTests
{
    [Fact]
    public async Task HiddenPageDoesNoWorkAndTimerOverlapIsSkipped()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), VisiblePageRefresh.Interval);
        var gate = new VisiblePageRefresh();
        var calls = 0;
        var held = new TaskCompletionSource();
        Task Refresh() { calls++; return held.Task; }
        await gate.RunAsync(() => false, Refresh);
        Assert.Equal(0, calls);
        var first = gate.RunAsync(() => true, Refresh);
        await gate.RunAsync(() => true, Refresh);
        Assert.Equal(1, calls);
        held.SetResult();
        await first;
        Assert.Equal(1, calls);
        await gate.RunAsync(() => true, Refresh);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task RangeChangesCoalesceAndLeavingPageCancelsPendingWork(bool remainsVisible, int expectedCalls)
    {
        var gate = new VisiblePageRefresh();
        var held = new TaskCompletionSource();
        var visible = true;
        var calls = 0;
        Task Refresh() => ++calls == 1 ? held.Task : Task.CompletedTask;
        var first = gate.RunAsync(() => visible, Refresh);
        await gate.RunAsync(() => visible, Refresh, explicitRequest: true);
        await gate.RunAsync(() => visible, Refresh, explicitRequest: true);
        visible = remainsVisible;
        held.SetResult();
        await first;
        Assert.Equal(expectedCalls, calls);
    }
}
