using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Osmium.App.Controls;

[Microsoft.UI.Xaml.Markup.ContentProperty(Name = nameof(ActionContent))]
public sealed partial class SettingsRowCard : UserControl
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingsRowCard),
            new PropertyMetadata(string.Empty, OnPropertyChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingsRowCard),
            new PropertyMetadata(string.Empty, OnPropertyChanged));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsRowCard),
            new PropertyMetadata(string.Empty, OnPropertyChanged));

    public static readonly DependencyProperty ActionContentProperty =
        DependencyProperty.Register(nameof(ActionContent), typeof(object), typeof(SettingsRowCard),
            new PropertyMetadata(null, OnPropertyChanged));

    public static readonly DependencyProperty FooterContentProperty =
        DependencyProperty.Register(nameof(FooterContent), typeof(object), typeof(SettingsRowCard),
            new PropertyMetadata(null, OnPropertyChanged));

    public static readonly DependencyProperty IsGroupedProperty =
        DependencyProperty.Register(nameof(IsGrouped), typeof(bool), typeof(SettingsRowCard),
            new PropertyMetadata(false, OnPropertyChanged));

    public static readonly DependencyProperty ShowIconProperty =
        DependencyProperty.Register(nameof(ShowIcon), typeof(bool), typeof(SettingsRowCard),
            new PropertyMetadata(true, OnPropertyChanged));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    public object? FooterContent
    {
        get => GetValue(FooterContentProperty);
        set => SetValue(FooterContentProperty, value);
    }

    public bool IsGrouped
    {
        get => (bool)GetValue(IsGroupedProperty);
        set => SetValue(IsGroupedProperty, value);
    }

    public bool ShowIcon
    {
        get => (bool)GetValue(ShowIconProperty);
        set => SetValue(ShowIconProperty, value);
    }

    public SettingsRowCard()
    {
        InitializeComponent();
        SyncAppearance();
    }

    private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SettingsRowCard card)
        {
            card.SyncAppearance();
        }
    }

    private void SyncAppearance()
    {
        if (RowTitleText == null) return;

        RowTitleText.Text = Title ?? string.Empty;

        bool hasDesc = !string.IsNullOrWhiteSpace(Description);
        RowDescriptionText.Text = Description ?? string.Empty;
        RowDescriptionText.Visibility = hasDesc ? Visibility.Visible : Visibility.Collapsed;

        bool hasGlyph = ShowIcon && !string.IsNullOrWhiteSpace(Glyph);
        RowGlyphIcon.Glyph = Glyph ?? string.Empty;
        RowGlyphIcon.Visibility = hasGlyph ? Visibility.Visible : Visibility.Collapsed;

        RowActionPresenter.Content = ActionContent;

        RowFooterPresenter.Content = FooterContent;
        RowFooterPresenter.Visibility = FooterContent != null ? Visibility.Visible : Visibility.Collapsed;
        RowFooterPresenter.Margin = ShowIcon ? new Thickness(42, 0, 0, 0) : new Thickness(42, 0, 0, 0);

        if (!ShowIcon)
        {
            RowTitleText.Margin = new Thickness(42, 0, 0, 0);
            RowDescriptionText.Margin = new Thickness(42, 0, 0, 0);
        }
        else
        {
            RowTitleText.Margin = new Thickness(0);
            RowDescriptionText.Margin = new Thickness(0);
        }

        StandaloneChromeBorder.Visibility = IsGrouped ? Visibility.Collapsed : Visibility.Visible;
    }
}