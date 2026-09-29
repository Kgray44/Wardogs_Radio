using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WardogsRadio.App;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WardogsRadio.App.Tests;

public sealed class ScrollingTests
{
    [Fact]
    public void WheelMovesLibraryStyleListAndPageAndMenusHaveScrollHosts()
    {
        RunSta(() =>
        {
            var app = new App();
            app.InitializeComponent();
            try
            {
                var list = new ListBox { Height = 120, Width = 220 };
                for (var index = 0; index < 30; index++) list.Items.Add($"Library song {index}");
                var combo = new ComboBox { Width = 220, Style = Assert.IsType<Style>(app.Resources[typeof(ComboBox)]) };
                for (var index = 0; index < 30; index++) combo.Items.Add($"Dropdown choice {index}");
                var spacer = new Border { Height = 600 };
                var pageContent = new StackPanel();
                pageContent.Children.Add(list);
                pageContent.Children.Add(combo);
                pageContent.Children.Add(spacer);
                var page = new ScrollViewer { Width = 260, Height = 220, Content = pageContent };
                var window = new Window
                {
                    Content = page, Width = 300, Height = 260,
                    Left = -10000, Top = -10000, Opacity = 0,
                    ShowActivated = false, ShowInTaskbar = false
                };
                window.Show();
                window.UpdateLayout();

                var listScroll = FindChild<ScrollViewer>(list);
                Assert.NotNull(listScroll);
                Assert.True(listScroll.ScrollableHeight > 0);
                Assert.True(page.ScrollableHeight > 0);
                var firstItem = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
                var listWheel = WheelDown();
                firstItem.RaiseEvent(listWheel);
                window.UpdateLayout();
                Assert.True(listScroll.VerticalOffset > 0,
                    $"Wheel should move a Library-style list (handled={listWheel.Handled}, extent={listScroll.ExtentHeight}, viewport={listScroll.ViewportHeight}).");
                listScroll.ScrollToTop();
                window.UpdateLayout();
                for (var index = 0; index < 4; index++) firstItem.RaiseEvent(WheelDown(30));
                window.UpdateLayout();
                Assert.True(listScroll.VerticalOffset > 0, "Small trackpad wheel deltas should move a Library-style list.");

                page.ScrollToTop();
                window.UpdateLayout();
                var pageWheel = WheelDown();
                spacer.RaiseEvent(pageWheel);
                window.UpdateLayout();
                Assert.True(page.VerticalOffset > 0,
                    $"Wheel should move a page (handled={pageWheel.Handled}, extent={page.ExtentHeight}, viewport={page.ViewportHeight}).");

                combo.IsDropDownOpen = true;
                combo.UpdateLayout();
                var popup = Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo));
                var popupScroll = FindChild<ScrollViewer>(popup.Child);
                Assert.NotNull(popupScroll);
                Assert.True(popupScroll.ScrollableHeight > 0, "The dropdown must be scrollable when it overflows.");
                var firstChoice = Assert.IsType<ComboBoxItem>(combo.ItemContainerGenerator.ContainerFromIndex(0));
                firstChoice.RaiseEvent(WheelDown());
                popup.Child.UpdateLayout();
                Assert.True(popupScroll.VerticalOffset > 0, "Wheel should move an open dropdown.");
                popupScroll.ScrollToTop();
                popup.Child.UpdateLayout();
                for (var index = 0; index < 4; index++) firstChoice.RaiseEvent(WheelDown(30));
                popup.Child.UpdateLayout();
                Assert.True(popupScroll.VerticalOffset > 0, "Small trackpad deltas should move an open dropdown.");
                combo.IsDropDownOpen = false;

                var menu = new ContextMenu { Style = Assert.IsType<Style>(app.Resources[typeof(ContextMenu)]) };
                for (var index = 0; index < 30; index++) menu.Items.Add(new MenuItem { Header = $"Menu action {index}" });
                menu.PlacementTarget = list;
                menu.IsOpen = true;
                menu.UpdateLayout();
                var menuScroll = FindChild<ScrollViewer>(menu);
                Assert.NotNull(menuScroll);
                Assert.True(menuScroll.ScrollableHeight > 0, "The context menu must be scrollable when it overflows.");
                Assert.IsType<MenuItem>(menu.Items[0]).RaiseEvent(WheelDown());
                menu.UpdateLayout();
                Assert.True(menuScroll.VerticalOffset > 0, "Wheel should move an open context menu.");
                menuScroll.ScrollToTop();
                menu.UpdateLayout();
                for (var index = 0; index < 4; index++) Assert.IsType<MenuItem>(menu.Items[0]).RaiseEvent(WheelDown(30));
                menu.UpdateLayout();
                Assert.True(menuScroll.VerticalOffset > 0, "Small trackpad deltas should move an open context menu.");
                menu.IsOpen = false;
                window.Close();
            }
            finally { app.Shutdown(); }
        });
    }

    static MouseWheelEventArgs WheelDown(int delta = 120) => new(Mouse.PrimaryDevice, Environment.TickCount, -delta)
    {
        RoutedEvent = UIElement.MouseWheelEvent
    };

    static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } descendant) return descendant;
        }
        return null;
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Scrolling smoke test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
