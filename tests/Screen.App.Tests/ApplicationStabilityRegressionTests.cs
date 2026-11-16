using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Screen.App.Tests;

[TestClass]
public sealed class ApplicationStabilityRegressionTests
{
    [TestMethod]
    public void ClipboardLastFailureIsReportedWithItsNativeCause()
    {
        var attempts = 0;
        var nativeError = new ExternalException("Clipboard is held");
        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            BitmapUtilities.WriteClipboardWithRetry(() =>
            {
                attempts++;
                throw nativeError;
            }, "请重试复制。"));

        Assert.AreEqual(5, attempts);
        Assert.AreEqual("请重试复制。", error.Message);
        Assert.AreSame(nativeError, error.InnerException);
    }

    [TestMethod]
    public void InstanceShutdownReleasesMutexWhenListenerIsStillRunning()
    {
        var name = $@"Local\Screen.Instance.Test.{Guid.NewGuid():N}";
        var eventName = name + ".Capture";
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        using var primary = new SingleInstanceCoordinator(name, eventName);
        Assert.IsTrue(primary.TryBecomePrimary());
        primary.StartListening(() =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            finished.Set();
        });

        using var trigger = EventWaitHandle.OpenExisting(eventName);
        trigger.Set();
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            primary.Dispose();
            Assert.IsFalse(finished.IsSet);
            using var successor = new SingleInstanceCoordinator(name, eventName);
            Assert.IsTrue(successor.TryBecomePrimary());
        }
        finally
        {
            release.Set();
            if (entered.IsSet)
            {
                Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(5)));
            }
        }
    }
}
