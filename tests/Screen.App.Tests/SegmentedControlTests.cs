using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Screen.App.Tests;

[TestClass]
public sealed class SegmentedControlTests
{
    [STATestMethod]
    public void SelectionBindsInBothDirectionsWithoutSelectingAnItemDuringConstruction()
    {
        var source = new ListBox { ItemsSource = new[] { "视频", "GIF" }, SelectedIndex = 1 };
        var segments = new SegmentedControl { ItemsSource = source.ItemsSource };
        Assert.AreEqual(-1, segments.SelectedIndex);
        segments.SetBinding(SegmentedControl.SelectedIndexProperty, new Binding(nameof(ListBox.SelectedIndex)) { Source = source });
        Assert.AreEqual(1, segments.SelectedIndex);

        var changes = 0;
        segments.SelectionChanged += (_, _) => changes++;
        segments.SetCurrentValue(SegmentedControl.SelectedIndexProperty, 0);
        Assert.AreEqual(0, source.SelectedIndex);
        source.SelectedIndex = 1;
        Assert.AreEqual("GIF", segments.SelectedItem);
        Assert.AreEqual(2, changes);
    }

    [STATestMethod]
    public void IndicatorFollowsActualSegmentsAndIsHiddenWhenSelectionClears()
    {
        var segments = new SegmentedControl { ItemsSource = new[] { "原文", "译文" }, SelectedIndex = 1 };
        var resources = new ResourceDictionary { Source = new Uri("/Screen;component/Themes/SettingsControls.xaml", UriKind.Relative) };
        segments.Style = (Style)resources[typeof(SegmentedControl)];
        var size = new Size(200, 32);
        segments.Measure(size);
        segments.Arrange(new Rect(size));

        var indicator = (FrameworkElement)segments.Template.FindName("PART_Indicator", segments);
        var track = (FrameworkElement)segments.Template.FindName("PART_Track", segments);
        var item = (FrameworkElement)segments.ItemContainerGenerator.ContainerFromIndex(1);
        Assert.AreEqual(Visibility.Visible, indicator.Visibility);
        Assert.IsGreaterThan(0d, item.ActualWidth);
        Assert.AreEqual(item.ActualWidth, indicator.Width, 0.01);
        Assert.AreEqual(item.TranslatePoint(new Point(), track).X, ((TranslateTransform)indicator.RenderTransform).X, 0.01);

        segments.SelectedIndex = -1;
        Assert.AreEqual(Visibility.Hidden, indicator.Visibility);
    }
}
