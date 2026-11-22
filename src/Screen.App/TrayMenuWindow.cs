using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Forms = System.Windows.Forms;

namespace Screen.App;

internal sealed class TrayMenuWindow : Window
{
    private readonly StackPanel _items = new();
    private readonly List<Button> _buttons = [];
    private readonly SpringTrack _hover;
    private readonly SquircleGlassBorder _glass;
    private bool _committed;

    public TrayMenuWindow()
    {
        Title = "Flick";
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        KeyboardNavigation.SetTabNavigation(_items, KeyboardNavigationMode.Cycle);
        _hover = new SpringTrack(_items, 6.5, Axis.Y, fill: (Brush)FindResource("Brush.SurfaceHover"));
        _glass = new SquircleGlassBorder
        {
            CornerRadiusRadius = 10.5,
            Margin = SquircleGlassBorder.FloatingShadowMargin,
            Padding = new Thickness(4),
            Child = _hover
        };
        Content = _glass;
        _items.MouseLeave += (_, _) =>
        {
            if (!_committed)
            {
                Keyboard.Focus(this);
                _hover.Select(null);
            }
        };
        Deactivated += (_, _) => { if (IsVisible && !_committed) Close(); };
        PreviewKeyDown += OnMenuKey;
    }

    public void AddItem(string label, string shortcut, string glyph, Func<Task> action, bool capturesDesktop = false)
    {
        var row = new DockPanel();
        var icon = AppleVectorIcons.Create(glyph, 16);
        icon.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var gesture = new TextBlock { Text = shortcut, Margin = new Thickness(16, 0, 0, 0), FontSize = 12 };
        gesture.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        DockPanel.SetDock(gesture, Dock.Right);
        row.Children.Add(gesture);
        row.Children.Add(new TextBlock { Text = label, FontSize = 13 });
        var button = new Button { Height = 34, MinWidth = 236, Content = row };
        button.SetResourceReference(StyleProperty, "TrayMenuItemStyle");
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetAcceleratorKey(button, shortcut);
        button.MouseEnter += (_, _) =>
        {
            if (!_committed)
                button.Focus();
        };
        button.GotKeyboardFocus += (_, _) => _hover.Select(button);
        void Press() => button.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.68, TimeSpan.FromMilliseconds(100))
        {
            AutoReverse = true,
            FillBehavior = FillBehavior.Stop
        });
        button.PreviewMouseLeftButtonDown += (_, _) => Press();
        button.PreviewKeyDown += (_, e) =>
        {
            if (!e.IsRepeat && e.Key is Key.Space or Key.Enter)
                Press();
        };
        button.Click += async (_, _) =>
        {
            if (_committed)
                return;
            _committed = true;
            Close();
            try
            {
                // A capture launched from a menu item should see the desktop after dismissal.
                // A global hotkey can capture the menu while it is still on screen.
                if (capturesDesktop)
                    NativeMethods.DwmFlush();
                await action();
            }
            catch (Exception exception)
            {
                SimpleLogger.LogError($"Tray action '{label}' failed", exception);
                MessageBox.Show($"“{label}”执行失败：{exception.Message}", "Flick", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        _buttons.Add(button);
        _items.Children.Add(button);
    }

    public void AddSeparator()
    {
        var divider = new Border { Height = 1, Margin = new Thickness(10, 4, 10, 4) };
        divider.SetResourceReference(Border.BackgroundProperty, "Brush.BorderSubtle");
        _items.Children.Add(divider);
    }

    public void OpenAt(NativeMethods.NativePoint anchor)
    {
        var area = Forms.Screen.FromPoint(new System.Drawing.Point(anchor.X, anchor.Y)).WorkingArea;
        var dpiScale = NativeMethods.GetDpiAtPoint(anchor) / 96d;
        _glass.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = (int)Math.Ceiling(_glass.DesiredSize.Width * dpiScale);
        var height = (int)Math.Ceiling(_glass.DesiredSize.Height * dpiScale);
        var margin = _glass.Margin;
        int x = anchor.X - (int)Math.Round(margin.Left * dpiScale);
        int y = anchor.Y - (int)Math.Round(margin.Top * dpiScale);
        if (x + width > area.Right)
            x = anchor.X - width + (int)Math.Round(margin.Right * dpiScale);
        if (y + height > area.Bottom)
            y = anchor.Y - height + (int)Math.Round(margin.Bottom * dpiScale);
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height));
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowPos(handle, new nint(-1), x, y, width, height, NativeMethods.SwpNoActivate);
        };
        Show();
        Activate();
        Focus();
    }

    private void OnMenuKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        if (_buttons.Count == 0 || e.Key is not (Key.Down or Key.Up or Key.Home or Key.End))
            return;
        int selected = Keyboard.FocusedElement is Button current ? _buttons.IndexOf(current) : -1;
        int next = e.Key switch
        {
            Key.Home => 0,
            Key.End => _buttons.Count - 1,
            Key.Up => selected <= 0 ? _buttons.Count - 1 : selected - 1,
            _ => (selected + 1) % _buttons.Count
        };
        _buttons[next].Focus();
        e.Handled = true;
    }
}
