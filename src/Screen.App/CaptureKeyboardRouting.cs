using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Screen.App;

internal static class CaptureKeyboardRouting
{
    public static bool PreserveTextInput(Key key, DependencyObject? focusedElement)
    {
        // Escape remains the capture cancellation command. All other keys,
        // including copy, undo and Enter, belong to the focused text control.
        if (key == Key.Escape)
        {
            return false;
        }
        for (var element = focusedElement; element is not null; element = ParentOf(element))
        {
            if (element is TextBoxBase or PasswordBox)
            {
                return true;
            }
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject element) => element switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element),
        ContentElement content => ContentOperations.GetParent(content) ?? LogicalTreeHelper.GetParent(content),
        _ => LogicalTreeHelper.GetParent(element)
    };
}
