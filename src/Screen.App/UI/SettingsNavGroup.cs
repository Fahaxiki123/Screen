using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Screen.App;

/// <summary>
/// 设置侧边栏单个分组的导航轨道控件：
/// 同分组内切换时，纯白选中卡片沿当前分组轨道无变形平滑平移；
/// 跨分组切换时，旧组白卡原地淡出、新组白卡在目标位置自然淡入，不跨越分组标题；
/// 鼠标在同分组未选中项间划过时，淡灰悬停层丝滑磁吸跟随。
/// </summary>
internal sealed class SettingsNavGroup : Grid, IDisposable
{
    private static readonly SolidColorBrush HoverFillBrush = CreateFrozenBrush(0x0D, 0x0F, 0x17, 0x2A);
    private static readonly SolidColorBrush SelectionOutlineBrush = CreateFrozenBrush(0x12, 0x0F, 0x17, 0x2A);

    private readonly StackPanel _items = new();
    private readonly SquircleGlassBorder _hoverCard;
    private readonly SquircleGlassBorder _selectionCard;
    private readonly TranslateTransform _hoverTranslate = new();
    private readonly ScaleTransform _selectionScale = new();
    private readonly TranslateTransform _selectionTranslate = new();
    private readonly SettingsNavTrackMotion _motion = new();

    private FrameworkElement? _selectedItem;
    private FrameworkElement? _hoveredItem;
    private bool _pendingSnap = true;
    private bool _pendingFadeIn;
    private bool _animating;
    private TimeSpan? _lastFrame;
    private long _lastUpdate;

    public SettingsNavGroup()
    {
        _hoverCard = new SquircleGlassBorder
        {
            CornerRadiusRadius = 8.5,
            ShowRim = false,
            ShadowLevel = 0,
            GlassBackground = HoverFillBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Opacity = 0,
            Visibility = Visibility.Hidden,
            RenderTransform = _hoverTranslate
        };

        var selectionTransform = new TransformGroup();
        selectionTransform.Children.Add(_selectionScale);
        selectionTransform.Children.Add(_selectionTranslate);
        _selectionCard = new SquircleGlassBorder
        {
            CornerRadiusRadius = 8.5,
            ShowRim = false,
            ShadowLevel = 1,
            CacheShadows = true,
            GlassBackground = Brushes.White,
            OutlineBrush = SelectionOutlineBrush,
            OutlineThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Opacity = 0,
            Visibility = Visibility.Hidden,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = selectionTransform
        };

        var indicatorLayer = new Canvas { IsHitTestVisible = false };
        indicatorLayer.Children.Add(_hoverCard);
        indicatorLayer.Children.Add(_selectionCard);

        Children.Add(indicatorLayer);
        Children.Add(_items);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        MouseLeave += (_, _) => ClearHover();
    }

    /// <summary>当鼠标进入本分组任意项时触发，便于外层清空其他分组的悬停层。</summary>
    public event Action<SettingsNavGroup>? HoverEntered;

    /// <summary>向本分组追加导航按钮，并自动接入悬停磁吸追踪。</summary>
    public void AddItem(Button button)
    {
        button.MouseEnter += (_, _) => HandleItemHover(button);
        _items.Children.Add(button);
    }

    /// <summary>选中本分组中的某一项。</summary>
    /// <param name="target">目标导航按钮。</param>
    /// <param name="crossGroup">true 表示从其他分组切入（原地淡入）；false 表示同分组内切换（平滑平移）。</param>
    /// <param name="instant">是否跳过动画直接落位（用于窗口首次打开）。</param>
    public void Select(FrameworkElement target, bool crossGroup, bool instant = false)
    {
        _selectedItem = target;
        if (_hoveredItem == target)
        {
            ClearHover();
        }

        if (!TryGetBounds(target, out var bounds))
        {
            _pendingSnap |= instant;
            _pendingFadeIn = !instant && crossGroup;
            return;
        }

        _pendingSnap = false;
        _pendingFadeIn = false;
        if (instant)
        {
            _motion.SnapSelection(bounds);
            ApplyVisuals();
            StopIfSettled(settled: true);
            return;
        }

        if (crossGroup)
        {
            _motion.FadeInSelection(bounds);
        }
        else
        {
            _motion.SlideSelectionTo(bounds);
        }

        ApplyVisuals();
        EnsureAnimating();
    }

    /// <summary>当用户切换到其他分组时，令本组白色选中卡片原地轻柔淡出。</summary>
    public void Deselect()
    {
        _selectedItem = null;
        _pendingSnap = false;
        _pendingFadeIn = false;
        _motion.FadeOutSelection();
        EnsureAnimating();
    }

    /// <summary>平滑淡出本分组的鼠标悬停淡灰层。</summary>
    public void ClearHover()
    {
        _hoveredItem = null;
        _motion.HideHover();
        if (_motion.HoverOpacity > 0.001)
        {
            EnsureAnimating();
        }
    }

    private void HandleItemHover(FrameworkElement item)
    {
        HoverEntered?.Invoke(this);
        if (item == _selectedItem)
        {
            ClearHover();
            return;
        }

        _hoveredItem = item;
        if (!TryGetBounds(item, out var bounds))
        {
            return;
        }

        _motion.HoverAt(bounds);
        ApplyVisuals();
        EnsureAnimating();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdated;
        LayoutUpdated += OnLayoutUpdated;
        SyncLayout();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdated;
        StopAnimation();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => SyncLayout();

    private void SyncLayout()
    {
        if (!IsVisible)
        {
            return;
        }

        if (_selectedItem is not null && TryGetBounds(_selectedItem, out var selectedBounds))
        {
            if (_pendingSnap)
            {
                _pendingSnap = false;
                _pendingFadeIn = false;
                _motion.SnapSelection(selectedBounds);
                ApplyVisuals();
            }
            else if (_pendingFadeIn)
            {
                _pendingFadeIn = false;
                _motion.FadeInSelection(selectedBounds);
                ApplyVisuals();
                EnsureAnimating();
            }
            else if (_motion.SyncSelectionBounds(selectedBounds))
            {
                ApplyVisuals();
            }
        }

        if (_hoveredItem is not null && _hoveredItem != _selectedItem && TryGetBounds(_hoveredItem, out var hoverBounds))
        {
            if (_motion.SyncHoverBounds(hoverBounds))
            {
                ApplyVisuals();
            }
        }
    }

    private bool TryGetBounds(FrameworkElement target, out Rect bounds)
    {
        if (!IsVisible || !target.IsVisible || target.ActualWidth <= 0 || target.ActualHeight <= 0 ||
            !IsArrangeValid || !target.IsArrangeValid || !IsAncestorOf(target))
        {
            bounds = Rect.Empty;
            return false;
        }

        var origin = target.TransformToAncestor(this).Transform(new Point(0, 0));
        bounds = new Rect(origin.X, origin.Y, target.ActualWidth, target.ActualHeight);
        return true;
    }

    private void EnsureAnimating()
    {
        long now = Stopwatch.GetTimestamp();
        if (!_animating)
        {
            _animating = true;
            _lastUpdate = now;
            _lastFrame = null;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var frameTime = ((RenderingEventArgs)e).RenderingTime;
        if (frameTime == _lastFrame)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        double elapsed = Stopwatch.GetElapsedTime(_lastUpdate, now).TotalSeconds;
        _lastUpdate = now;
        _lastFrame = frameTime;

        bool settled = _motion.Advance(elapsed);
        ApplyVisuals();
        StopIfSettled(settled);
    }

    private void StopIfSettled(bool settled)
    {
        if (settled)
        {
            StopAnimation();
        }
    }

    private void StopAnimation()
    {
        if (_animating)
        {
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
        }
        _lastFrame = null;
    }

    private void ApplyVisuals()
    {
        if (_motion.SelectionOpacity > 0.002 && _motion.SelectionBounds.Width > 0 && _motion.SelectionBounds.Height > 0)
        {
            _selectionCard.Visibility = Visibility.Visible;
            _selectionCard.Opacity = _motion.SelectionOpacity;
            _selectionCard.Width = _motion.SelectionBounds.Width;
            _selectionCard.Height = _motion.SelectionBounds.Height;
            _selectionTranslate.X = _motion.SelectionBounds.X;
            _selectionTranslate.Y = _motion.SelectionBounds.Y;
            _selectionScale.ScaleX = _motion.SelectionScale;
            _selectionScale.ScaleY = _motion.SelectionScale;
        }
        else
        {
            _selectionCard.Visibility = Visibility.Hidden;
            _selectionCard.Opacity = 0;
        }

        if (_motion.HoverOpacity > 0.002 && _motion.HoverBounds.Width > 0 && _motion.HoverBounds.Height > 0)
        {
            _hoverCard.Visibility = Visibility.Visible;
            _hoverCard.Opacity = _motion.HoverOpacity;
            _hoverCard.Width = _motion.HoverBounds.Width;
            _hoverCard.Height = _motion.HoverBounds.Height;
            _hoverTranslate.X = _motion.HoverBounds.X;
            _hoverTranslate.Y = _motion.HoverBounds.Y;
        }
        else
        {
            _hoverCard.Visibility = Visibility.Hidden;
            _hoverCard.Opacity = 0;
        }
    }

    public void Dispose()
    {
        LayoutUpdated -= OnLayoutUpdated;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        StopAnimation();
    }

    private static SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// 帧率无关的分组导航物理运动内核（支持 60Hz–300Hz 显示器精确一致响应）。
/// </summary>
internal sealed class SettingsNavTrackMotion
{
    private const double SelectionStiffness = 620.0;
    private const double SelectionDamping = 48.0;
    private const double SelectionFadeRate = 22.0;
    private const double SelectionScaleRate = 20.0;
    private const double HiddenSelectionScale = 0.976;

    private const double HoverStiffness = 680.0;
    private const double HoverDamping = 50.0;
    private const double HoverFadeRate = 26.0;
    private const double MaxStepSeconds = 1.0 / 120.0;

    private double _selectionY;
    private double _selectionTargetY;
    private double _selectionVelocityY;
    private double _selectionTargetOpacity;
    private double _selectionTargetScale = HiddenSelectionScale;

    private double _hoverY;
    private double _hoverTargetY;
    private double _hoverVelocityY;
    private double _hoverTargetOpacity;

    public Rect SelectionBounds { get; private set; }
    public double SelectionOpacity { get; private set; }
    public double SelectionScale { get; private set; } = HiddenSelectionScale;

    public Rect HoverBounds { get; private set; }
    public double HoverOpacity { get; private set; }

    /// <summary>直接无动画落位选中白卡（首次打开窗口使用）。</summary>
    public void SnapSelection(Rect bounds)
    {
        _selectionY = _selectionTargetY = bounds.Y;
        _selectionVelocityY = 0;
        SelectionBounds = bounds;
        SelectionOpacity = _selectionTargetOpacity = 1;
        SelectionScale = _selectionTargetScale = 1;
    }

    /// <summary>同分组内平滑平移白卡至新项位置。</summary>
    public void SlideSelectionTo(Rect bounds)
    {
        if (SelectionOpacity <= 0.01)
        {
            FadeInSelection(bounds);
            return;
        }

        _selectionTargetY = bounds.Y;
        SelectionBounds = new Rect(bounds.X, _selectionY, bounds.Width, bounds.Height);
        _selectionTargetOpacity = 1;
        _selectionTargetScale = 1;
    }

    /// <summary>跨分组切入本组：直接定位到目标位置并原地柔和淡入。</summary>
    public void FadeInSelection(Rect bounds)
    {
        _selectionY = _selectionTargetY = bounds.Y;
        _selectionVelocityY = 0;
        SelectionBounds = bounds;
        SelectionOpacity = 0;
        SelectionScale = HiddenSelectionScale;
        _selectionTargetOpacity = 1;
        _selectionTargetScale = 1;
    }

    /// <summary>跨分组离开本组：原地轻柔淡出，不发生位移。</summary>
    public void FadeOutSelection()
    {
        _selectionVelocityY = 0;
        _selectionTargetY = _selectionY;
        _selectionTargetOpacity = 0;
        _selectionTargetScale = HiddenSelectionScale;
    }

    /// <summary>悬停至本组某未选中项：若当前不可见则直接落位淡入，否则丝滑磁吸平移。</summary>
    public void HoverAt(Rect bounds)
    {
        if (HoverOpacity < 0.08)
        {
            _hoverY = _hoverTargetY = bounds.Y;
            _hoverVelocityY = 0;
            HoverBounds = bounds;
        }
        else
        {
            _hoverTargetY = bounds.Y;
            HoverBounds = new Rect(bounds.X, _hoverY, bounds.Width, bounds.Height);
        }

        _hoverTargetOpacity = 1;
    }

    /// <summary>淡出本组悬停淡灰层。</summary>
    public void HideHover()
    {
        _hoverTargetOpacity = 0;
    }

    /// <summary>布局变化时（如窗口缩放）同步选中项几何尺寸。</summary>
    public bool SyncSelectionBounds(Rect bounds)
    {
        if (Math.Abs(SelectionBounds.X - bounds.X) < 0.05 &&
            Math.Abs(_selectionTargetY - bounds.Y) < 0.05 &&
            Math.Abs(SelectionBounds.Width - bounds.Width) < 0.05 &&
            Math.Abs(SelectionBounds.Height - bounds.Height) < 0.05)
        {
            return false;
        }

        if (Math.Abs(_selectionY - _selectionTargetY) < 0.15 && Math.Abs(_selectionVelocityY) < 0.6)
        {
            _selectionY = _selectionTargetY = bounds.Y;
            _selectionVelocityY = 0;
        }
        else
        {
            _selectionTargetY = bounds.Y;
        }

        SelectionBounds = new Rect(bounds.X, _selectionY, bounds.Width, bounds.Height);
        return true;
    }

    /// <summary>布局变化时同步悬停层几何尺寸。</summary>
    public bool SyncHoverBounds(Rect bounds)
    {
        if (Math.Abs(HoverBounds.X - bounds.X) < 0.05 &&
            Math.Abs(_hoverTargetY - bounds.Y) < 0.05 &&
            Math.Abs(HoverBounds.Width - bounds.Width) < 0.05 &&
            Math.Abs(HoverBounds.Height - bounds.Height) < 0.05)
        {
            return false;
        }

        _hoverTargetY = bounds.Y;
        HoverBounds = new Rect(bounds.X, _hoverY, bounds.Width, bounds.Height);
        return true;
    }

    /// <summary>推进物理步进，所有通道收敛时返回 true。</summary>
    public bool Advance(double elapsed)
    {
        if (!(elapsed > 0))
        {
            return false;
        }

        if (elapsed >= 2.0)
        {
            _selectionY = _selectionTargetY;
            _selectionVelocityY = 0;
            SelectionOpacity = _selectionTargetOpacity;
            SelectionScale = _selectionTargetScale;
            SelectionBounds = new Rect(SelectionBounds.X, _selectionY, SelectionBounds.Width, SelectionBounds.Height);

            _hoverY = _hoverTargetY;
            _hoverVelocityY = 0;
            HoverOpacity = _hoverTargetOpacity;
            HoverBounds = new Rect(HoverBounds.X, _hoverY, HoverBounds.Width, HoverBounds.Height);
            return true;
        }

        int steps = Math.Max(1, (int)Math.Ceiling(elapsed / MaxStepSeconds));
        double dt = elapsed / steps;
        for (int i = 0; i < steps; i++)
        {
            StepSpring(ref _selectionY, ref _selectionVelocityY, _selectionTargetY, SelectionStiffness, SelectionDamping, dt);
            SelectionOpacity = StepExp(SelectionOpacity, _selectionTargetOpacity, SelectionFadeRate, dt);
            SelectionScale = StepExp(SelectionScale, _selectionTargetScale, SelectionScaleRate, dt);

            StepSpring(ref _hoverY, ref _hoverVelocityY, _hoverTargetY, HoverStiffness, HoverDamping, dt);
            HoverOpacity = StepExp(HoverOpacity, _hoverTargetOpacity, HoverFadeRate, dt);
        }

        bool selectionSettled =
            Math.Abs(_selectionY - _selectionTargetY) < 0.12 &&
            Math.Abs(_selectionVelocityY) < 0.6 &&
            Math.Abs(SelectionOpacity - _selectionTargetOpacity) < 0.004 &&
            Math.Abs(SelectionScale - _selectionTargetScale) < 0.001;
        if (selectionSettled)
        {
            _selectionY = _selectionTargetY;
            _selectionVelocityY = 0;
            SelectionOpacity = _selectionTargetOpacity;
            SelectionScale = _selectionTargetScale;
        }

        bool hoverSettled =
            Math.Abs(_hoverY - _hoverTargetY) < 0.12 &&
            Math.Abs(_hoverVelocityY) < 0.6 &&
            Math.Abs(HoverOpacity - _hoverTargetOpacity) < 0.004;
        if (hoverSettled)
        {
            _hoverY = _hoverTargetY;
            _hoverVelocityY = 0;
            HoverOpacity = _hoverTargetOpacity;
        }

        SelectionBounds = new Rect(SelectionBounds.X, _selectionY, SelectionBounds.Width, SelectionBounds.Height);
        HoverBounds = new Rect(HoverBounds.X, _hoverY, HoverBounds.Width, HoverBounds.Height);
        return selectionSettled && hoverSettled;
    }

    private static void StepSpring(ref double position, ref double velocity, double target, double stiffness, double damping, double dt)
    {
        double acceleration = -stiffness * (position - target) - damping * velocity;
        velocity += acceleration * dt;
        position += velocity * dt;
    }

    private static double StepExp(double current, double target, double rate, double dt)
    {
        double alpha = 1.0 - Math.Exp(-rate * dt);
        return current + (target - current) * alpha;
    }
}
