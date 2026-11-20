using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class RecordingFormatTests
{
    [TestMethod]
    public void GifMutesOnlyTheCurrentSessionAndPreservesIndependentMouseDefaults()
    {
        var defaults = new RecordingProfile { RecordMicrophone = true, RecordSystemAudio = true, ShowCursor = true, ShowClickEffects = false };
        var choice = new RecordingSelection(new PixelRect(0, 0, 128, 128), true, false, true, true, RecordingFormat.Gif);
        var session = RecordingController.ResolveSessionProfile(defaults, choice);
        Assert.AreEqual(RecordingFormat.Gif, session.Format);
        Assert.IsFalse(session.RecordSystemAudio || session.RecordMicrophone || session.ShowClickEffects);
        Assert.IsTrue(session.ShowCursor);
        Assert.IsTrue(defaults.RecordSystemAudio && defaults.RecordMicrophone);
        Assert.AreEqual(RecordingFormat.Mp4, defaults.Format);
        Assert.AreEqual(defaults, RecordingController.ResolveSessionProfile(defaults, choice with { Format = RecordingFormat.Mp4 }));
        var gifDefault = defaults with { Format = RecordingFormat.Gif };
        var unprompted = RecordingController.ResolveSessionProfile(gifDefault, null);
        Assert.IsFalse(unprompted.RecordMicrophone || unprompted.RecordSystemAudio);
        Assert.IsTrue(gifDefault.RecordMicrophone && gifDefault.RecordSystemAudio);
    }
}
