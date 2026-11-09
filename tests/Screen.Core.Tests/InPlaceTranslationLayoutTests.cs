namespace Screen.Core.Tests;

[TestClass]
public sealed class InPlaceTranslationLayoutTests
{
    [TestMethod]
    public void WrappedParagraphKeepsItsOriginalFootprint()
    {
        var result = Create(400, 200,
            Line("This is the first wrapped line", 20, 30, 250, 20),
            Line("and this completes the paragraph.", 22, 55, 230, 20));

        Assert.HasCount(1, result);
        Assert.AreEqual("This is the first wrapped line and this completes the paragraph.", result[0].Text);
        Assert.AreEqual(new PixelRect(20, 30, 250, 45), result[0].Bounds);
        Assert.AreEqual(20.0, result[0].LineHeight);
    }

    [TestMethod]
    public void InterleavedColumnsNeverTranslateAsOneRegion()
    {
        var result = Create(700, 200,
            Line("The first column starts here", 20, 20, 250, 20),
            Line("The second column starts here", 380, 20, 260, 20),
            Line("and continues in the left column.", 20, 45, 260, 20),
            Line("and continues in the right column.", 380, 45, 270, 20));

        Assert.HasCount(2, result);
        Assert.IsTrue(result[0].Bounds.Right < result[1].Bounds.X);
        StringAssert.Contains(result[0].Text, "left column");
        StringAssert.Contains(result[1].Text, "right column");
    }

    [TestMethod]
    public void LabelsAndListItemsStaySeparate()
    {
        var result = Create(500, 300,
            Line("Settings", 20, 20, 90, 20),
            Line("Privacy", 20, 45, 80, 20),
            Line("• First item with several words", 20, 80, 270, 20),
            Line("• Second item with several words", 20, 105, 280, 20));

        Assert.HasCount(4, result);
    }

    [TestMethod]
    public void BoundsAreClippedAndOffImageDetectionsAreIgnored()
    {
        var result = Create(100, 80,
            Line("Visible", -5, -4, 40, 20),
            Line("Edge", 90, 70, 40, 30),
            Line("Outside", 140, 120, 30, 20),
            Line("Overflow", int.MaxValue - 5, 0, 20, 10));

        Assert.HasCount(2, result);
        Assert.AreEqual(new PixelRect(0, 0, 35, 16), result[0].Bounds);
        Assert.AreEqual(new PixelRect(90, 70, 10, 10), result[1].Bounds);
    }

    [TestMethod]
    public void MissingGeometryUsesWholeSelectionWithoutLosingText()
    {
        var result = InPlaceTranslationLayout.CreateRegions(new OcrDocument("en", "Text without geometry", []), 200, 100);
        Assert.HasCount(1, result);
        Assert.AreEqual("Text without geometry", result[0].Text);
        Assert.AreEqual(new PixelRect(0, 0, 200, 100), result[0].Bounds);
        Assert.HasCount(0, InPlaceTranslationLayout.CreateRegions(new OcrDocument("en", "", []), 200, 100));
    }

    private static OcrLine Line(string text, int x, int y, int width, int height) => new(text, new PixelRect(x, y, width, height), []);

    private static IReadOnlyList<TranslationRegion> Create(int width, int height, params OcrLine[] lines) =>
        InPlaceTranslationLayout.CreateRegions(new OcrDocument("en", string.Join('\n', lines.Select(line => line.Text)), lines), width, height);
}
