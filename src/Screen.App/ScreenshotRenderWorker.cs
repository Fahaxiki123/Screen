using Screen.Core;

namespace Screen.App;

internal static class ScreenshotRenderWorker
{
    // WPF bitmap rendering needs an STA, but never needs a visible Window.
    // Only immutable pixels and annotation snapshots cross this boundary.
    internal static Task<PixelBuffer> RenderAsync(
        PixelBuffer source, PixelRect selection, AnnotationItem[] annotations, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<PixelBuffer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(CaptureSurface.RenderAnnotatedRegion(source, selection, annotations, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        { IsBackground = true, Name = "Screen image export" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
