using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace SacramentalRecords.App;

internal static class UiSpacing
{
    private static readonly ConditionalWeakTable<FrameworkElement, OriginalLayout> OriginalLayouts = new();
    private static readonly string[] Labels = ["Compact", "Standard", "Spacious"];
    private static readonly double[] Scales = [0.7, 1.0, 1.3];

    private static int _level = 1;

    public static int Level => _level;
    public static string Label => Labels[_level];
    public static bool CanDecrease => _level > 0;
    public static bool CanIncrease => _level < Labels.Length - 1;

    public static void SetLevel(int level)
    {
        _level = Math.Clamp(level, 0, Labels.Length - 1);
        ApplyToAllWindows();
    }

    public static void ApplyToWindow(Window window)
    {
        ApplyElement(window);
        ApplyChildren(window);
    }

    private static void ApplyToAllWindows()
    {
        foreach (Window window in System.Windows.Application.Current.Windows)
        {
            ApplyToWindow(window);
        }
    }

    private static void ApplyChildren(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is FrameworkElement element) ApplyElement(element);
            ApplyChildren(child);
        }
    }

    private static void ApplyElement(FrameworkElement element)
    {
        var layout = OriginalLayouts.GetValue(element, static item => new OriginalLayout(
            item.Margin,
            item is Control control ? control.Padding : null,
            item is Border border ? border.Padding : null));
        var scale = Scales[_level];

        element.Margin = Scale(layout.Margin, scale);
        if (element is Control control && layout.ControlPadding is Thickness controlPadding)
            control.Padding = Scale(controlPadding, scale);
        if (element is Border border && layout.BorderPadding is Thickness borderPadding)
            border.Padding = Scale(borderPadding, scale);
    }

    private static Thickness Scale(Thickness thickness, double scale) => new(
        thickness.Left * scale,
        thickness.Top * scale,
        thickness.Right * scale,
        thickness.Bottom * scale);

    private sealed record OriginalLayout(Thickness Margin, Thickness? ControlPadding, Thickness? BorderPadding);
}
