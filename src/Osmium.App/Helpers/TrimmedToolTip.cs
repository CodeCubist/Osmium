using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Osmium.App.Helpers;

public static class TrimmedToolTip
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(TrimmedToolTip),
        new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly ConditionalWeakTable<TextBlock, TrackState> States = new();

    private sealed class TrackState
    {
        public long TextCallbackToken;
    }

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;

        if (e.NewValue is true)
        {
            var st = States.GetOrCreateValue(tb);
            if (st.TextCallbackToken == 0)
            {
                st.TextCallbackToken = tb.RegisterPropertyChangedCallback(TextBlock.TextProperty, OnTextChanged);
            }

            tb.Loaded += OnLoadedOrSized;
            tb.SizeChanged += OnLoadedOrSized;
            UpdateToolTip(tb);
        }
        else
        {
            tb.Loaded -= OnLoadedOrSized;
            tb.SizeChanged -= OnLoadedOrSized;
            if (States.TryGetValue(tb, out var st) && st.TextCallbackToken != 0)
            {
                tb.UnregisterPropertyChangedCallback(TextBlock.TextProperty, st.TextCallbackToken);
                st.TextCallbackToken = 0;
            }

            ToolTipService.SetToolTip(tb, null);
        }
    }

    private static void OnTextChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (sender is TextBlock tb) UpdateToolTip(tb);
    }

    private static void OnLoadedOrSized(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock tb) UpdateToolTip(tb);
    }

    private static void UpdateToolTip(TextBlock tb)
    {
        if (!tb.IsLoaded || string.IsNullOrWhiteSpace(tb.Text))
        {
            ToolTipService.SetToolTip(tb, null);
            return;
        }

        if (tb.IsTextTrimmed)
        {
            ToolTipService.SetToolTip(tb, tb.Text);
        }
        else
        {
            ToolTipService.SetToolTip(tb, null);
        }
    }
}