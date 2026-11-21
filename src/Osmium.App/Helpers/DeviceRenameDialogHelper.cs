using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Osmium.App.Services;
using Osmium.Core.Models;

namespace Osmium.App.Helpers;

public static class DeviceRenameDialogHelper
{
    public const int MaxCustomNameLength = 24;

    public static async Task<(bool Updated, bool Cleared)> ShowAsync(
        XamlRoot xamlRoot,
        ElementTheme theme,
        AccountDeviceDto device)
    {
        var acc = App.Engine.AccountService;
        bool updated = false;
        bool cleared = false;

        var nameBox = new TextBox
        {
            Header = LocalizationService.GetString("Set_DeviceRenameHeader"),
            PlaceholderText = device.OriginalDeviceName,
            Text = device.CustomName ?? string.Empty,
            MaxLength = MaxCustomNameLength,
            CornerRadius = new CornerRadius(6)
        };

        var hintText = new TextBlock
        {
            Text = LocalizationService.Format("Set_DeviceRenameHintFmt", device.OriginalDeviceName),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };

        var errText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            Visibility = Visibility.Collapsed
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 340, MaxWidth = 400 };
        panel.Children.Add(nameBox);
        panel.Children.Add(hintText);
        panel.Children.Add(errText);

        var dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = theme,
            Title = LocalizationService.GetString("Set_DeviceRenameTitle"),
            Content = panel,
            PrimaryButtonText = LocalizationService.GetString("Acc_SaveBtn"),
            SecondaryButtonText = device.HasCustomName
                ? LocalizationService.GetString("Set_DeviceClearRenameBtn")
                : string.Empty,
            CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        dlg.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                string nextName = nameBox.Text.Trim();
                var res = await acc.RenameDeviceAsync(device.DeviceId, nextName);
                if (!res.Ok)
                {
                    args.Cancel = true;
                    errText.Text = acc.FormatFriendlyError(res.Code, res.HttpStatusCode);
                    errText.Visibility = Visibility.Visible;
                }
                else
                {
                    updated = true;
                    cleared = string.IsNullOrEmpty(nextName);
                }
            }
            finally
            {
                deferral.Complete();
            }
        };

        dlg.SecondaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var res = await acc.RenameDeviceAsync(device.DeviceId, string.Empty);
                if (!res.Ok)
                {
                    args.Cancel = true;
                    errText.Text = acc.FormatFriendlyError(res.Code, res.HttpStatusCode);
                    errText.Visibility = Visibility.Visible;
                }
                else
                {
                    updated = true;
                    cleared = true;
                }
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dlg.ShowAsync();
        return (updated, cleared);
    }
}