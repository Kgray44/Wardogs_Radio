using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using WardogsRadio.App;
using Xunit;

namespace WardogsRadio.App.Tests;

public sealed class YouTubeSearchWindowTests
{
    [Fact]
    public void SearchWindowLoadsWithoutCredentialControlsAndExplainsUnavailableSearch()
    {
        RunSta(() =>
        {
            var window = new YouTubeSearchWindow();
            try
            {
                Assert.NotNull(window.FindName("QueryBox"));
                Assert.NotNull(window.FindName("TypeBox"));
                Assert.NotNull(window.FindName("ResultsList"));
                Assert.NotNull(window.FindName("PreviewButton"));
                Assert.NotNull(window.FindName("SelectButton"));
                Assert.Null(window.FindName("KeyBox"));
                Assert.Null(window.FindName("KeyPanel"));
                Assert.False(Assert.IsType<Button>(window.FindName("SearchButton")).IsEnabled);
                Assert.Contains("paste a link", Assert.IsType<TextBlock>(window.FindName("StatusText")).Text,
                    StringComparison.OrdinalIgnoreCase);
                Assert.IsType<ComboBox>(window.FindName("TypeBox")).SelectedIndex = 1;
                Assert.False(Assert.IsType<Button>(window.FindName("SearchButton")).IsEnabled);
                Assert.Contains("paste a link", Assert.IsType<TextBlock>(window.FindName("StatusText")).Text,
                    StringComparison.OrdinalIgnoreCase);
            }
            finally { window.Close(); }
        });
    }

    static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The WPF search window did not finish loading.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
