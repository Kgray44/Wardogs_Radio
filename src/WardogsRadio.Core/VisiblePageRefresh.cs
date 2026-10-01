namespace WardogsRadio.Core;

/// <summary>UI-thread coordinator: skip timer overlap, coalesce one explicit range/visibility refresh.</summary>
public sealed class VisiblePageRefresh
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    bool _busy;
    bool _pending;
    public async Task RunAsync(Func<bool> visible, Func<Task> refresh, bool explicitRequest = false)
    {
        if (!visible()) return;
        if (_busy) { _pending |= explicitRequest; return; }
        _busy = true;
        try
        {
            do
            {
                _pending = false;
                await refresh();
            } while (_pending && visible());
        }
        finally { _busy = false; _pending = false; }
    }
}
