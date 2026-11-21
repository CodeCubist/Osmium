using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Osmium.App.Controls;

public sealed partial class TogglePasswordBox : UserControl
{
    private bool _allowReveal = true;
    private bool _isPasswordRevealed;

    public event RoutedEventHandler? PasswordChanged;

    public TogglePasswordBox()
    {
        InitializeComponent();
        RegisterPropertyChangedCallback(CornerRadiusProperty, (_, _) =>
        {
            if (InnerPasswordBox != null)
            {
                InnerPasswordBox.CornerRadius = CornerRadius;
            }
        });
        RegisterPropertyChangedCallback(FontFamilyProperty, (_, _) =>
        {
            if (InnerPasswordBox != null && FontFamily != null)
            {
                InnerPasswordBox.FontFamily = FontFamily;
            }
        });
        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) =>
        {
            if (InnerPasswordBox != null && FontSize > 0)
            {
                InnerPasswordBox.FontSize = FontSize;
            }
        });
        ApplyRevealVisuals();
    }

    public string Password
    {
        get => InnerPasswordBox?.Password ?? string.Empty;
        set
        {
            if (InnerPasswordBox != null)
            {
                InnerPasswordBox.Password = value ?? string.Empty;
            }
        }
    }

    public string PlaceholderText
    {
        get => InnerPasswordBox?.PlaceholderText ?? string.Empty;
        set
        {
            if (InnerPasswordBox != null)
            {
                InnerPasswordBox.PlaceholderText = value ?? string.Empty;
            }
        }
    }

    public string Header
    {
        get => HeaderText?.Text ?? string.Empty;
        set
        {
            if (HeaderText != null)
            {
                HeaderText.Text = value ?? string.Empty;
                HeaderText.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    public int MaxLength
    {
        get => InnerPasswordBox?.MaxLength ?? 0;
        set
        {
            if (InnerPasswordBox != null)
            {
                InnerPasswordBox.MaxLength = value;
            }
        }
    }

    public bool AllowReveal
    {
        get => _allowReveal;
        set
        {
            _allowReveal = value;
            if (!_allowReveal)
            {
                _isPasswordRevealed = false;
            }

            ApplyRevealVisuals();
        }
    }

    public bool IsPasswordRevealed
    {
        get => _isPasswordRevealed;
        set
        {
            _isPasswordRevealed = _allowReveal && value;
            ApplyRevealVisuals();
        }
    }

    public new bool Focus(FocusState value)
        => InnerPasswordBox != null && InnerPasswordBox.Focus(value);

    private void ApplyRevealVisuals()
    {
        if (InnerPasswordBox == null || RevealToggleBtn == null || RevealIcon == null) return;

        bool revealed = _allowReveal && _isPasswordRevealed;
        InnerPasswordBox.PasswordRevealMode = revealed ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
        RevealToggleBtn.Visibility = _allowReveal ? Visibility.Visible : Visibility.Collapsed;
        InnerPasswordBox.Padding = _allowReveal
            ? new Thickness(11, 5, 36, 6)
            : new Thickness(11, 5, 11, 6);
        RevealIcon.Glyph = revealed ? "\uE5F3" : "\uE5F6";
    }

    private void RevealToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_allowReveal || !IsEnabled) return;
        _isPasswordRevealed = !_isPasswordRevealed;
        ApplyRevealVisuals();
        InnerPasswordBox.Focus(FocusState.Programmatic);
    }

    private void InnerPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        PasswordChanged?.Invoke(this, e);
    }
}