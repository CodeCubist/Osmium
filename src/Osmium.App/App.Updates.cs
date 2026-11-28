using System.Text.Json;
using Microsoft.UI.Xaml;
using Osmium.App.Services;
using Osmium.App.Views;
using Osmium.Core.Models;

namespace Osmium.App;

public partial class App
{
    private readonly CancellationTokenSource _updateLifetime = new();
    private bool _checkingForUpdate;
    private UpdateRelease? _pendingUpdate;
    private UpdateAnnouncementWindow? _updateWindow;

    private async Task CheckForUpdateAsync()
    {
        if (_checkingForUpdate || _updateLifetime.IsCancellationRequested) return;
        _checkingForUpdate = true;
        try
        {
            string language = Uri.EscapeDataString(LocalizationService.ActiveLanguage);
            var endpoint = new Uri(Engine.AccountService.GetServiceBaseUri(), $"/api/update?platform=windows&lang={language}");
            _pendingUpdate = await UpdateService.CheckAsync(endpoint, AppVersionInfo.Current, _updateLifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // A closed window or a timed-out request does not need an alert.
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            Engine.Logging.Info($"Update check failed: {ex.GetType().Name}");
        }
        finally
        {
            _checkingForUpdate = false;
        }

        TryShowUpdateAnnouncement();
    }

    private void OnUpdateSessionStateChanged()
    {
        MainWindowInstance?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_updateLifetime.IsCancellationRequested) return;
            if (IsRemoteControlInProgress)
            {
                CloseUpdateAnnouncement();
                return;
            }

            _ = CheckForUpdateAsync();
        });
    }

    private void TryShowUpdateAnnouncement()
    {
        var mainWindow = MainWindowInstance;
        var release = _pendingUpdate;
        if (release == null || _updateWindow != null || _updateLifetime.IsCancellationRequested ||
            IsRemoteControlInProgress || RestartRequested || Volatile.Read(ref _executingRestart) != 0 ||
            mainWindow?.AppWindow.IsVisible != true ||
            Engine.SettingsService.Settings.LastAnnouncedUpdateVersion == release.Version)
        {
            return;
        }

        try
        {
            var window = new UpdateAnnouncementWindow(release, (mainWindow.Content as FrameworkElement)?.ActualTheme);
            _updateWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_updateWindow, window)) _updateWindow = null;
            };
            if (IsRemoteControlInProgress)
            {
                CloseUpdateAnnouncement();
                return;
            }

            window.Activate();
            Engine.SettingsService.Settings.LastAnnouncedUpdateVersion = release.Version;
            Engine.SettingsService.SaveSettings();
        }
        catch (Exception ex)
        {
            CloseUpdateAnnouncement();
            Engine.Logging.Warn($"Update announcement failed: {ex.GetType().Name}");
        }
    }

    private void CloseUpdateAnnouncement()
    {
        var window = _updateWindow;
        if (window == null) return;
        _updateWindow = null;
        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            Engine.Logging.Warn($"Closing the update announcement failed: {ex.GetType().Name}");
        }
    }

    private void StopUpdateAnnouncements()
    {
        _updateLifetime.Cancel();
        CloseUpdateAnnouncement();
        _updateLifetime.Dispose();
    }
}
