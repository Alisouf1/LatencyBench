using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using LatencyBench.App.Converters;
using LatencyBench.App.ViewModels;

namespace LatencyBench.Tests;

/// <summary>
/// Drives the nav tabs the way a click does, rather than by calling Navigate() directly.
///
/// Reported symptom: clicking a tab highlights it but the panel below never changes. That pairing
/// is specific — a RadioButton in a GroupName updates its own checked visual locally on click no
/// matter what, so the highlight proves nothing about whether the value ever reached the view
/// model. Only the ConvertBack half of the IsChecked binding does that, and if it returns
/// Binding.DoNothing the section silently never changes while the tab still lights up.
///
/// These tests therefore assert on CurrentViewModel — what the ContentControl actually displays —
/// after setting IsChecked, which is exactly what a click does.
/// </summary>
public class NavigationTests
{
    private static void RunOnStaThread(Action action)
        => WpfTestHost.RunOnStaThread(action, "Navigation test failed");

    [Fact]
    public void ConvertBackTurnsACheckedTabIntoItsSection()
    {
        var converter = new EnumToBooleanConverter();

        object result = converter.ConvertBack(true, typeof(NavSection), "PortTest", System.Globalization.CultureInfo.InvariantCulture);

        // Binding.DoNothing here is the exact failure that leaves the tab highlighted and the
        // content stuck on the previous view.
        Assert.NotEqual(Binding.DoNothing, result);
        Assert.Equal(NavSection.PortTest, result);
    }

    [Fact]
    public void ConvertBackIgnoresTheTabBeingUnchecked()
    {
        var converter = new EnumToBooleanConverter();

        // Unchecking the outgoing tab must not write anything back, or the two RadioButtons in the
        // group would fight over the section and the last writer would win.
        Assert.Equal(
            Binding.DoNothing,
            converter.ConvertBack(false, typeof(NavSection), "PortTest", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(NavSection.Optimize)]
    [InlineData(NavSection.PortTest)]
    [InlineData(NavSection.DpcIsr)]
    [InlineData(NavSection.MsiMode)]
    [InlineData(NavSection.Affinity)]
    [InlineData(NavSection.Tweaks)]
    [InlineData(NavSection.Processes)]
    [InlineData(NavSection.Monitor)]
    [InlineData(NavSection.MouseTest)]
    [InlineData(NavSection.Dashboard)]
    public void SettingTheSectionSwapsTheDisplayedViewModel(NavSection section)
    {
        RunOnStaThread(() =>
        {
            var vm = new MainViewModel();

            vm.CurrentSection = section;

            Assert.NotNull(vm.CurrentViewModel);

            // Every section must map to a distinct view model instance; falling through to the
            // Dashboard default for a real section is how a tab would appear to "do nothing".
            if (section != NavSection.Dashboard)
            {
                Assert.NotSame(vm.Dashboard, vm.CurrentViewModel);
            }
        });
    }

    [Fact]
    public void ClickingEveryNavTabInTheRealWindowChangesTheDisplayedView()
    {
        RunOnStaThread(() =>
        {
            var window = new LatencyBench.App.MainWindow();

            // Measure/Arrange alone does not build the visual tree of an unshown Window, so the nav
            // buttons would not exist to find. Showing it off-screen builds the real tree - the same
            // one a user clicks - without a window flashing up during the test run.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000;
            window.Top = -10000;
            window.ShowInTaskbar = false;
            window.Show();
            window.UpdateLayout();

            var vm = Assert.IsType<MainViewModel>(window.DataContext);

            var navButtons = FindVisualChildren<RadioButton>(window)
                .Where(r => r.GroupName == "Nav")
                .ToList();

            Assert.True(navButtons.Count >= 10, $"Expected at least 10 nav tabs, found {navButtons.Count}.");

            var seen = new List<object>();

            foreach (var button in navButtons)
            {
                // This is precisely what a mouse click does to a RadioButton: sets IsChecked. If the
                // binding back to CurrentSection is broken, the button still reports IsChecked = true
                // while CurrentViewModel stays put — the reported symptom exactly.
                button.IsChecked = true;
                window.UpdateLayout();

                Assert.NotNull(vm.CurrentViewModel);
                seen.Add(vm.CurrentViewModel!);
            }

            // If navigation were broken, every entry would be the same (stuck) view model.
            int distinct = seen.Distinct().Count();
            window.Close();

            Assert.True(
                distinct >= 10,
                $"Clicking {navButtons.Count} tabs only ever produced {distinct} distinct views - navigation is not switching.");
        });
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
