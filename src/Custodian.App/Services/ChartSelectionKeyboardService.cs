using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Custodian.App.Services;

internal static class ChartSelectionKeyboardService
{
    internal static bool IsTextInputFocus(IInputElement? focusedElement)
    {
        return focusedElement is DependencyObject focusedObject &&
            (IsDescendantOfType<System.Windows.Controls.Primitives.TextBoxBase>(focusedObject) ||
             IsDescendantOfType<System.Windows.Controls.ComboBox>(focusedObject));
    }

    internal static bool ShouldRouteDeleteShortcut(
        IInputElement? focusedElement,
        DependencyObject chartSurfaceHost,
        bool hasChartSelection)
    {
        if (!hasChartSelection || focusedElement is not DependencyObject focusedObject)
        {
            return false;
        }

        return IsDescendantOf(focusedObject, chartSurfaceHost);
    }

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null; current = GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDescendantOfType<T>(DependencyObject element)
        where T : DependencyObject
    {
        for (DependencyObject? current = element; current is not null; current = GetParent(current))
        {
            if (current is T)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element)
    {
        if (element is FrameworkContentElement contentElement)
        {
            return contentElement.Parent;
        }

        DependencyObject? visualParent = null;
        try
        {
            visualParent = VisualTreeHelper.GetParent(element);
        }
        catch (InvalidOperationException)
        {
        }

        return visualParent ?? LogicalTreeHelper.GetParent(element);
    }
}
