using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Screen.App;

internal abstract class GlassResultCard : Grid
{
    private readonly DispatcherTimer _copyFeedback = new() { Interval = TimeSpan.FromSeconds(1) };
    private Button? _copyButton;
    private object? _normalContent;
    private Point _dragOffset;
    public bool IsUserPositioned { get; private set; }

    protected GlassResultCard()
    {
        _copyFeedback.Tick += (_, _) => RestoreCopyLabel();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                GlassMotion.Enter(this, 10, 0.96, 280);
            }
            else
            {
                RestoreCopyLabel();
                ResetPosition();
            }
        };
    }

    protected void AttachCard(SquircleGlassBorder card, FrameworkElement dragHeader)
    {
        Children.Add(card);
        dragHeader.Cursor = Cursors.SizeAll;
        var lift = new ScaleTransform();
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        card.RenderTransform = lift;
        dragHeader.MouseLeftButtonDown += (_, e) =>
        {
            if (Parent is not Canvas canvas || !dragHeader.CaptureMouse())
            {
                return;
            }
            var mouse = e.GetPosition(canvas);
            _dragOffset = new Point(mouse.X - Canvas.GetLeft(this), mouse.Y - Canvas.GetTop(this));
            IsUserPositioned = true;
            card.ShadowLevel = 3;
            AnimateLift(lift, 1.015);
            e.Handled = true;
        };
        dragHeader.MouseMove += (_, e) =>
        {
            if (!dragHeader.IsMouseCaptured || Parent is not Canvas canvas)
            {
                return;
            }
            var position = e.GetPosition(canvas);
            Canvas.SetLeft(this, Math.Clamp(position.X - _dragOffset.X, 8, Math.Max(8, canvas.ActualWidth - ActualWidth - 8)));
            Canvas.SetTop(this, Math.Clamp(position.Y - _dragOffset.Y, 8, Math.Max(8, canvas.ActualHeight - ActualHeight - 8)));
        };
        dragHeader.MouseLeftButtonUp += (_, e) =>
        {
            if (dragHeader.IsMouseCaptured)
            {
                dragHeader.ReleaseMouseCapture();
                e.Handled = true;
            }
        };
        dragHeader.LostMouseCapture += (_, _) =>
        {
            card.ShadowLevel = 2;
            AnimateLift(lift, 1);
        };
    }

    public void ResetPosition() => IsUserPositioned = false;

    protected void ShowCopied(Button button, object normalContent)
    {
        if (_copyButton != button)
        {
            RestoreCopyLabel();
            _copyButton = button;
            _normalContent = normalContent;
        }
        button.Content = "✓ 已复制";
        _copyFeedback.Stop();
        _copyFeedback.Start();
    }

    protected void RestoreCopyLabel()
    {
        _copyFeedback.Stop();
        if (_copyButton != null)
        {
            _copyButton.Content = _normalContent;
            _copyButton = null;
            _normalContent = null;
        }
    }

    private static void AnimateLift(ScaleTransform scale, double target)
    {
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
}
