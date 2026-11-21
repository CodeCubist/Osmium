using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Osmium.App.Helpers;

public static class PopupCornerHelper
{
    private const double PopupCornerRadiusValue = 8.0;

    private static readonly object Marker = new();
    private static readonly ConditionalWeakTable<ComboBox, object> AttachedCombos = new();
    private static readonly ConditionalWeakTable<MenuFlyout, object> AttachedFlyouts = new();
    private static readonly ConditionalWeakTable<UIElement, object> InstalledRoots = new();

    public static void Install(Window window, UIElement root)
    {
        if (window == null || root == null) return;

        if (!InstalledRoots.TryGetValue(root, out _))
        {
            InstalledRoots.Add(root, Marker);
            root.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler((_, e) => HandleInteraction(e.OriginalSource)), true);
            root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => HandleInteraction(e.OriginalSource)),
                true);
        }

        AttachToTree(root);
    }

    public static void AttachToTree(DependencyObject? root)
    {
        if (root == null) return;

        var stack = new Stack<DependencyObject>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is ComboBox combo)
            {
                AttachComboBox(combo);
            }

            int count = VisualTreeHelper.GetChildrenCount(current);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(current, i);
                if (child != null) stack.Push(child);
            }
        }
    }

    public static void AttachMenuFlyout(MenuFlyout? flyout, FrameworkElement? anchor)
    {
        if (flyout == null || AttachedFlyouts.TryGetValue(flyout, out _)) return;
        AttachedFlyouts.Add(flyout, Marker);

        flyout.Opened += (_, _) =>
        {
            ScheduleRoundOpenPopups(anchor?.XamlRoot, anchor?.DispatcherQueue);
        };
    }

    private static void HandleInteraction(object? originalSource)
    {
        if (originalSource is DependencyObject source)
        {
            var combo = FindAncestor<ComboBox>(source);
            if (combo != null)
            {
                AttachComboBox(combo);
            }
        }
    }

    private static void AttachComboBox(ComboBox combo)
    {
        if (AttachedCombos.TryGetValue(combo, out _)) return;
        AttachedCombos.Add(combo, Marker);

        combo.DropDownOpened += (_, _) =>
        {
            ScheduleRoundOpenPopups(combo.XamlRoot, combo.DispatcherQueue);
        };
    }

    private static void ScheduleRoundOpenPopups(XamlRoot? xamlRoot, DispatcherQueue? queue)
    {
        RoundOpenPopups(xamlRoot);
        queue?.TryEnqueue(DispatcherQueuePriority.High, () => RoundOpenPopups(xamlRoot));
        queue?.TryEnqueue(DispatcherQueuePriority.Normal, () => RoundOpenPopups(xamlRoot));
    }

    public static void RoundOpenPopups(XamlRoot? xamlRoot)
    {
        if (xamlRoot == null) return;

        try
        {
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
            {
                ApplyRoundedBorderToPopupChild(popup.Child);
            }
        }
        catch
        {
        }
    }

    private static void ApplyRoundedBorderToPopupChild(UIElement? child)
    {
        if (child == null) return;

        var corner = new CornerRadius(PopupCornerRadiusValue);

        if (child is Border rootBorder)
        {
            rootBorder.CornerRadius = corner;
        }
        else if (child is MenuFlyoutPresenter menuPresenter)
        {
            menuPresenter.CornerRadius = corner;
        }
        else if (child is FlyoutPresenter flyoutPresenter)
        {
            flyoutPresenter.CornerRadius = corner;
        }

        var stack = new Stack<DependencyObject>();
        stack.Push(child);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is Border b && string.Equals(b.Name, "PopupBorder", StringComparison.Ordinal))
            {
                b.CornerRadius = corner;
            }

            int count = VisualTreeHelper.GetChildrenCount(current);
            for (int i = 0; i < count; i++)
            {
                var next = VisualTreeHelper.GetChild(current, i);
                if (next != null) stack.Push(next);
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}