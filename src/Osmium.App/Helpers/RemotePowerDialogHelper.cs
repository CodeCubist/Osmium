using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Osmium.App.Services;
using Osmium.Core.Models;

namespace Osmium.App.Helpers;

public static class RemotePowerDialogHelper
{
    public static async Task ExecuteAsync(
        XamlRoot xamlRoot,
        ElementTheme theme,
        AccountDeviceDto dev,
        string action,
        bool requireConfirm)
    {
        if (!dev.IsComputerDevice) return;

        string devName = string.IsNullOrWhiteSpace(dev.DeviceName) ? dev.FormattedDeviceId : dev.DeviceName;
        if (requireConfirm)
        {
            string confirmTitleKey = action == "restart" ? "Power_ConfirmRestartTitle" : "Power_ConfirmShutdownTitle";
            string confirmBodyKey = action == "restart" ? "Power_ConfirmRestartBodyFmt" : "Power_ConfirmShutdownBodyFmt";
            string confirmBtnKey = action == "restart" ? "Power_ActionRestart" : "Power_ActionShutdown";

            var confirmDlg = new ContentDialog
            {
                XamlRoot = xamlRoot,
                RequestedTheme = theme,
                Title = LocalizationService.GetString(confirmTitleKey),
                Content = new TextBlock
                {
                    Text = LocalizationService.Format(confirmBodyKey, devName),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 400
                },
                PrimaryButtonText = LocalizationService.GetString(confirmBtnKey),
                CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirmDlg.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        try
        {
            var outcome = await App.Engine.RequestRemotePowerAsync(dev, action);
            if (!outcome.Ok)
            {
                await ShowDialogAsync(
                    xamlRoot,
                    theme,
                    LocalizationService.GetString("Conn_ErrorTitle"),
                    outcome.ErrorMessage);
                return;
            }

            string msg;
            if (action == "wake")
            {
                if (outcome.AlreadyOnline)
                {
                    msg = LocalizationService.Format("Power_WakeAlreadyOnlineFmt", devName);
                }
                else if (outcome.RelayedCount > 0)
                {
                    msg = LocalizationService.Format("Power_WakeSentWithRelayFmt", devName, outcome.RelayedCount);
                }
                else
                {
                    msg = LocalizationService.Format("Power_WakeSentLanOnlyFmt", devName);
                }
            }
            else if (action == "restart")
            {
                msg = LocalizationService.Format("Power_RestartSentFmt", devName, Math.Max(1, outcome.DelaySeconds));
            }
            else
            {
                msg = LocalizationService.Format("Power_ShutdownSentFmt", devName, Math.Max(1, outcome.DelaySeconds));
            }

            await ShowDialogAsync(
                xamlRoot,
                theme,
                LocalizationService.GetString("Power_DockLabel"),
                msg);
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(
                xamlRoot,
                theme,
                LocalizationService.GetString("Conn_ErrorTitle"),
                ex.Message);
        }
    }

    private static async Task ShowDialogAsync(
        XamlRoot xamlRoot,
        ElementTheme theme,
        string title,
        string message)
    {
        var dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = theme,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                MinWidth = 300,
                MaxWidth = 420
            },
            CloseButtonText = LocalizationService.GetString("Assist_OkBtn"),
            DefaultButton = ContentDialogButton.Close
        };
        await dlg.ShowAsync();
    }
}
