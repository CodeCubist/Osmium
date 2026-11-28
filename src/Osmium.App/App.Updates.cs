using System.Text.Json;
using Microsoft.UI.Xaml;
using Osmium.App.Services;
using Osmium.App.Views;
using Osmium.Core.Models;

namespace Osmium.App;

internal enum ManualUpdateResult
{
    Available,
    UpToDate,
    Failed
}

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

        ShowUpdateWindow(release);
    }

    internal async Task<ManualUpdateResult> CheckForUpdateManuallyAsync()
    {
        if (_updateLifetime.IsCancellationRequested) return ManualUpdateResult.Failed;
        try
        {
            string language = Uri.EscapeDataString(LocalizationService.ActiveLanguage);
            var endpoint = new Uri(Engine.AccountService.GetServiceBaseUri(), $"/api/update?platform=windows&lang={language}");
            var release = await UpdateService.CheckAsync(endpoint, AppVersionInfo.Current, _updateLifetime.Token);
            _pendingUpdate = release;
            if (release == null) return ManualUpdateResult.UpToDate;
            return ShowUpdateWindow(release) ? ManualUpdateResult.Available : ManualUpdateResult.Failed;
        }
        catch (OperationCanceledException)
        {
            return ManualUpdateResult.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            Engine.Logging.Info($"Manual update check failed: {ex.GetType().Name}");
            return ManualUpdateResult.Failed;
        }
    }

    private bool ShowUpdateWindow(UpdateRelease release)
    {
        if (_updateWindow != null)
        {
            try
            {
                _updateWindow.Activate();
                return true;
            }
            catch
            {
                return false;
            }
        }

        try
        {
            var window = new UpdateAnnouncementWindow(release, (MainWindowInstance?.Content as FrameworkElement)?.ActualTheme);
            _updateWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_updateWindow, window)) _updateWindow = null;
            };
            if (IsRemoteControlInProgress)
            {
                CloseUpdateAnnouncement();
                return false;
            }

            window.Activate();
            Engine.SettingsService.Settings.LastAnnouncedUpdateVersion = release.Version;
            Engine.SettingsService.SaveSettings();
            return true;
        }
        catch (Exception ex)
        {
            CloseUpdateAnnouncement();
            Engine.Logging.Warn($"Update announcement failed: {ex.GetType().Name}");
            return false;
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
