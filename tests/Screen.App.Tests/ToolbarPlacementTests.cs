using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.App;

namespace Screen.App.Tests;

[TestClass]
public sealed class ToolbarPlacementTests
{
    [TestMethod]
    [DataRow(110, 150, 250, 140, true)]
    [DataRow(20, 8, 200, 675, false)] // The main toolbar is on the right of the tall selection.
    public void InPlaceStatusKeepsTheTranslationAndToolbarVisible(int x, int y, int width, int height, bool fitsAbove)
    {
        var viewport = new Size(1000, 700);
        var selection = new Rect(x, y, width, height);
        var toolbarSize = new Size(480, 42);
        var toolbar = new Rect(CaptureOverlayWindow.PlaceToolbarGroup(selection, viewport, toolbarSize), toolbarSize);
        var statusSize = new Size(310, 40);
        var status = new Rect(CaptureOverlayWindow.PlaceCallout(selection, toolbar, viewport, statusSize, preferAbove: true), statusSize);
        Assert.IsTrue(new Rect(viewport).Contains(status));
        Assert.IsFalse(selection.IntersectsWith(status));
        Assert.IsFalse(toolbar.IntersectsWith(status));
        if (fitsAbove)
            Assert.AreEqual(10d, selection.Top - status.Bottom);
    }

    [TestMethod]
    [DataRow(160, 240, 440, 180)] // Both rows fit below the selection.
    [DataRow(180, 610, 400, 80)]  // Both rows must move above the selection.
    [DataRow(80, 5, 200, 685)]    // The toolbar is beside a selection that reaches the top edge.
    public void StylePanelStaysEightDipBelowTheMainToolbarWhenTheGroupMoves(int x, int y, int width, int height)
    {
        var viewport = new Size(1000, 700);
        var mainSize = new Size(480, 42);
        var styleSize = new Size(220, 36);
        var selection = new Rect(x, y, width, height);
        var (main, styleTop) = CaptureOverlayWindow.PlaceToolbarRows(selection, viewport, mainSize, styleSize);
        var toolbar = new Rect(main, mainSize);
        var style = new Rect(new Point(main.X, styleTop), styleSize);
        Assert.AreEqual(8d, style.Top - toolbar.Bottom);
        var stack = Rect.Union(toolbar, style);
        Assert.IsTrue(new Rect(viewport).Contains(stack));
        Assert.IsFalse(selection.IntersectsWith(stack));
    }

    [TestMethod]
    [DataRow(90, 180, 500, 457, "above")] // 63 DIP below holds the main strip, but not both strips.
    [DataRow(100, 230, 500, 180, "below")] // Both vertical placements fit.
    [DataRow(20, 3, 550, 340, "below")]
    [DataRow(280, 550, 610, 147, "above")]
    [DataRow(180, 15, 200, 670, "right")]
    [DataRow(430, 15, 120, 670, "right")] // Both sides fit.
    [DataRow(690, 15, 260, 670, "left")]
    public void EntireToolbarGroupFitsOnPreferredFreeSide(int x, int y, int width, int height, string side)
    {
        var viewport = new Size(1000, 700);
        var group = new Size(280, 100);
        var selection = new Rect(x, y, width, height);
        var placed = new Rect(CaptureOverlayWindow.PlaceToolbarGroup(selection, viewport, group), group);

        Assert.IsTrue(new Rect(viewport).Contains(placed), $"Toolbar group escaped viewport: {placed}");
        bool onExpectedSide = side switch
        {
            "below" => placed.Top >= selection.Bottom,
            "above" => placed.Bottom <= selection.Top,
            "right" => placed.Left >= selection.Right,
            "left" => placed.Right <= selection.Left,
            _ => false
        };
        Assert.IsTrue(onExpectedSide, $"Expected group {side} selection {selection}, got {placed}.");
    }

    [TestMethod]
    [DataRow(15, 300, true)]
    [DataRow(50, 295, false)]
    public void CrowdedViewportUsesEdgeWithLeastObstruction(int top, int height, bool preferBottom)
    {
        var viewport = new Size(640, 360);
        var group = new Size(270, 105);
        // The selection spans the whole viewport width. Horizontal displacement
        // cannot help and neither gap is tall enough for both toolbars.
        var selection = new Rect(0, top, viewport.Width, height);
        var placed = new Rect(CaptureOverlayWindow.PlaceToolbarGroup(selection, viewport, group), group);
        var expectedEdge = new Rect(8, preferBottom ? viewport.Height - 8 - group.Height : 8, group.Width, group.Height);

        Assert.IsTrue(new Rect(viewport).Contains(placed));
        Assert.IsLessThanOrEqualTo(Rect.Intersect(expectedEdge, selection).Height, Rect.Intersect(placed, selection).Height);
    }
}
