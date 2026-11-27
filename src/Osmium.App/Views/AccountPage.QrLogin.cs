using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Osmium.App.Services;
using Osmium.Core.Helpers;
using Osmium.Core.Models;
using Windows.Storage.Streams;

namespace Osmium.App.Views;

public sealed partial class AccountPage : Page
{
    private bool _inQrLoginFlow;
    private CancellationTokenSource? _qrCts;
    private QrLoginCreateData? _activeQrSession;
    private string _qrVisualState = "idle";
    private DateTimeOffset _qrExpiresAtUtc;
    private DispatcherTimer? _qrCountdownTimer;
    private string? _appliedQrAvatarUri;

    private void EnterQrLoginFlow()
    {
        _inQrLoginFlow = true;
        _wizardStep = -1;
        WizardFlowPanel.Visibility = Visibility.Collapsed;

        int gen = ++_wizardAnimGen;
        AnimateHorizontalSwap(
            ModeSelectCard,
            ModeSelectCardTransform,
            QrLoginFlowPanel,
            QrLoginFlowTransform,
            forward: true,
            isStillCurrent: () => gen == _wizardAnimGen,
            slideDistance: 48);

        _ = StartNewQrSessionAsync();
    }

    private void BtnQrBack_Click(object sender, RoutedEventArgs e)
    {
        if (!_inQrLoginFlow) return;
        _inQrLoginFlow = false;
        StatusInfoBar.IsOpen = false;
        StopQrLoginSession(cancelRemote: true);

        int gen = ++_wizardAnimGen;
        AnimateHorizontalSwap(
            QrLoginFlowPanel,
            QrLoginFlowTransform,
            ModeSelectCard,
            ModeSelectCardTransform,
            forward: false,
            isStillCurrent: () => gen == _wizardAnimGen,
            slideDistance: 48);
    }

    private void BtnQrRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (!_inQrLoginFlow) return;
        StatusInfoBar.IsOpen = false;
        _ = StartNewQrSessionAsync();
    }

    private void StopQrLoginSession(bool cancelRemote)
    {
        _qrCountdownTimer?.Stop();
        var oldCts = _qrCts;
        _qrCts = null;
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch
        {
        }

        var oldSession = _activeQrSession;
        _activeQrSession = null;
        if (cancelRemote && oldSession != null &&
            !string.IsNullOrWhiteSpace(oldSession.QrId) &&
            !string.IsNullOrWhiteSpace(oldSession.PollSecret) &&
            _qrVisualState is "pending" or "scanned" or "awaiting_confirm")
        {
            _ = App.Engine.AccountService.CancelQrLoginAsync(oldSession.QrId, oldSession.PollSecret);
        }

        _qrVisualState = "idle";
    }

    private void EnsureQrCountdownTimer()
    {
        if (_qrCountdownTimer != null) return;
        _qrCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _qrCountdownTimer.Tick += (_, _) => UpdateQrCountdownText();
    }

    private void UpdateQrCountdownText()
    {
        if (!_inQrLoginFlow || _activeQrSession == null ||
            _qrVisualState is not ("pending" or "scanned" or "awaiting_confirm"))
        {
            _qrCountdownTimer?.Stop();
            QrCountdownText.Visibility = Visibility.Collapsed;
            return;
        }

        int rem = (int)Math.Ceiling((_qrExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds);
        if (rem <= 0)
        {
            _qrCountdownTimer?.Stop();
            QrCountdownText.Visibility = Visibility.Collapsed;
            if (_qrVisualState is "pending" or "scanned" or "awaiting_confirm")
            {
                ApplyQrOverlayState("expired");
            }

            return;
        }

        var ts = TimeSpan.FromSeconds(rem);
        QrCountdownText.Text = $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
        QrCountdownText.Visibility = Visibility.Visible;
    }

    private void RefreshQrLocalization()
    {
        if (!_inQrLoginFlow) return;
        switch (_qrVisualState)
        {
            case "pending":
                QrStatusText.Text = LocalizationService.GetString("Acc_QrWaitingScan");
                break;
            case "scanned":
                QrStatusText.Text = LocalizationService.GetString("Acc_QrScanned");
                QrOverlayStatusText.Text = LocalizationService.GetString("Acc_QrScanned");
                break;
            case "awaiting_confirm":
                QrStatusText.Text = LocalizationService.GetString("Acc_QrAwaitingConfirm");
                QrOverlayStatusText.Text = LocalizationService.GetString("Acc_QrAwaitingConfirm");
                break;
            case "expired":
                QrStatusText.Text = LocalizationService.GetString("Acc_QrExpired");
                QrOverlayStatusText.Text = LocalizationService.GetString("Acc_QrExpired");
                break;
            case "cancelled":
                QrStatusText.Text = LocalizationService.GetString("Acc_QrCancelled");
                QrOverlayStatusText.Text = LocalizationService.GetString("Acc_QrCancelled");
                break;
        }
    }

    private async Task StartNewQrSessionAsync()
    {
        StopQrLoginSession(cancelRemote: true);

        var cts = new CancellationTokenSource();
        _qrCts = cts;
        var ct = cts.Token;

        _qrVisualState = "loading";
        _appliedQrAvatarUri = null;
        QrCodeImage.Source = null;
        QrOverlayBorder.Visibility = Visibility.Collapsed;
        QrLoadingRing.IsActive = true;
        QrLoadingRing.Visibility = Visibility.Visible;
        QrCountdownText.Visibility = Visibility.Collapsed;
        QrStatusText.Text = string.Empty;
        BtnQrRefresh.IsEnabled = false;

        try
        {
            var res = await App.Engine.AccountService.CreateQrLoginAsync();
            if (ct.IsCancellationRequested || !_inQrLoginFlow) return;

            if (res.RequiresCaptcha && res.Challenge != null)
            {
                var verifiedRes = await CloudCaptchaWindow.ShowChallengeAsync<QrLoginCreateData>(
                    App.Engine.AccountService,
                    res.Challenge);
                if (ct.IsCancellationRequested || !_inQrLoginFlow) return;

                if (verifiedRes == null)
                {
                    QrLoadingRing.IsActive = false;
                    QrLoadingRing.Visibility = Visibility.Collapsed;
                    BtnQrRefresh.IsEnabled = true;
                    ApplyQrOverlayState("expired");
                    ShowStatus(InfoBarSeverity.Informational, LocalizationService.GetString("Conn_CaptchaCancelled"));
                    return;
                }

                res = verifiedRes;
            }

            if (!res.Ok || res.Data == null || string.IsNullOrWhiteSpace(res.Data.QrUrl))
            {
                QrLoadingRing.IsActive = false;
                QrLoadingRing.Visibility = Visibility.Collapsed;
                BtnQrRefresh.IsEnabled = true;
                ApplyQrOverlayState("expired");
                ShowStatus(InfoBarSeverity.Error,
                    App.Engine.AccountService.FormatFriendlyError(res.Code, res.HttpStatusCode, res.RetryAfter));
                return;
            }

            _activeQrSession = res.Data;
            _qrExpiresAtUtc = res.Data.ExpiresAt > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(res.Data.ExpiresAt)
                : DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, res.Data.ExpiresIn));

            bool[,] matrix = QrCodeEncoder.Encode(res.Data.QrUrl);
            QrCodeImage.Source = RenderQrMatrixToBitmap(matrix, targetPixels: 416);

            QrLoadingRing.IsActive = false;
            QrLoadingRing.Visibility = Visibility.Collapsed;
            BtnQrRefresh.IsEnabled = true;

            _qrVisualState = "pending";
            QrOverlayBorder.Visibility = Visibility.Collapsed;
            QrStatusText.Text = LocalizationService.GetString("Acc_QrWaitingScan");

            EnsureQrCountdownTimer();
            UpdateQrCountdownText();
            _qrCountdownTimer!.Start();

            await PollQrLoopAsync(res.Data, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || !_inQrLoginFlow) return;
            QrLoadingRing.IsActive = false;
            QrLoadingRing.Visibility = Visibility.Collapsed;
            BtnQrRefresh.IsEnabled = true;
            ApplyQrOverlayState("expired");
            ShowStatus(InfoBarSeverity.Error, ex.Message);
        }
    }

    private async Task PollQrLoopAsync(QrLoginCreateData session, CancellationToken ct)
    {
        int baseDelayMs = Math.Clamp(session.PollIntervalMs, 1000, 4000);
        int consecutiveNetworkErrors = 0;

        while (!ct.IsCancellationRequested && _inQrLoginFlow)
        {
            await Task.Delay(baseDelayMs, ct);
            if (ct.IsCancellationRequested || !_inQrLoginFlow) return;

            if (DateTimeOffset.UtcNow >= _qrExpiresAtUtc)
            {
                ApplyQrOverlayState("expired");
                return;
            }

            var pollRes = await App.Engine.AccountService.PollQrLoginStatusAsync(session.PollUrl, session.PollSecret);
            if (ct.IsCancellationRequested || !_inQrLoginFlow) return;

            if (pollRes.Code == OsmiumErrorCode.NetworkError)
            {
                consecutiveNetworkErrors++;
                if (consecutiveNetworkErrors >= 6)
                {
                    ApplyQrOverlayState("expired");
                    ShowStatus(InfoBarSeverity.Warning,
                        App.Engine.AccountService.FormatFriendlyError(pollRes.Code, pollRes.HttpStatusCode));
                    return;
                }

                continue;
            }

            consecutiveNetworkErrors = 0;

            if (!pollRes.Ok || pollRes.Data == null)
            {
                if (pollRes.Code == OsmiumErrorCode.QrCancelled)
                {
                    ApplyQrOverlayState("cancelled");
                }
                else
                {
                    ApplyQrOverlayState("expired");
                }

                return;
            }

            var data = pollRes.Data;
            if (data.ExpiresIn > 0)
            {
                _qrExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(data.ExpiresIn);
            }

            switch (data.State)
            {
                case "pending":
                    if (_qrVisualState != "pending")
                    {
                        _qrVisualState = "pending";
                        QrOverlayBorder.Visibility = Visibility.Collapsed;
                        QrStatusText.Text = LocalizationService.GetString("Acc_QrWaitingScan");
                    }

                    break;

                case "scanned":
                    ApplyQrOverlayState("scanned", data.Username, data.AvatarData);
                    break;

                case "awaiting_confirm":
                    ApplyQrOverlayState("awaiting_confirm", data.Username, data.AvatarData);
                    break;

                case "confirmed":
                    if (data.Session != null)
                    {
                        _qrVisualState = "confirmed";
                        _inQrLoginFlow = false;
                        StopQrLoginSession(cancelRemote: false);
                        App.Engine.AccountService.ApplyLoginSession(data.Session.Email, data.Session);
                        ShowStatus(InfoBarSeverity.Success, LocalizationService.GetString("Acc_MsgLoginSuccess"));
                        RefreshViewState();
                        _ = RefreshAllAccountDataAsync();
                    }
                    else
                    {
                        ApplyQrOverlayState("expired");
                    }

                    return;

                case "cancelled":
                    ApplyQrOverlayState("cancelled");
                    return;

                default:
                    ApplyQrOverlayState("expired");
                    return;
            }
        }
    }

    private void ApplyQrOverlayState(string state, string? username = null, string? avatarData = null)
    {
        _qrVisualState = state;
        QrOverlayBorder.Visibility = Visibility.Visible;

        if (state is "scanned" or "awaiting_confirm")
        {
            string msg = LocalizationService.GetString(state == "awaiting_confirm"
                ? "Acc_QrAwaitingConfirm"
                : "Acc_QrScanned");
            QrStatusText.Text = msg;
            QrOverlayStatusText.Text = msg;
            QrScannedAvatarHost.Visibility = Visibility.Visible;
            BtnQrOverlayRefresh.Visibility = Visibility.Collapsed;

            if (!string.IsNullOrWhiteSpace(username))
            {
                QrScannedUserText.Text = username;
                QrScannedUserText.Visibility = Visibility.Visible;
            }
            else
            {
                QrScannedUserText.Visibility = Visibility.Collapsed;
            }

            _ = ApplyQrScannedAvatarAsync(avatarData);
            return;
        }

        _qrCountdownTimer?.Stop();
        QrCountdownText.Visibility = Visibility.Collapsed;
        QrScannedAvatarHost.Visibility = Visibility.Collapsed;
        QrScannedUserText.Visibility = Visibility.Collapsed;
        BtnQrOverlayRefresh.Visibility = Visibility.Visible;

        string errMsg = LocalizationService.GetString(state == "cancelled" ? "Acc_QrCancelled" : "Acc_QrExpired");
        QrStatusText.Text = errMsg;
        QrOverlayStatusText.Text = errMsg;
    }

    private async Task ApplyQrScannedAvatarAsync(string? dataUri)
    {
        if (string.IsNullOrWhiteSpace(dataUri) ||
            !dataUri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            _appliedQrAvatarUri = null;
            QrScannedAvatarEllipse.Visibility = Visibility.Collapsed;
            QrOverlayIcon.Visibility = Visibility.Visible;
            return;
        }

        if (string.Equals(_appliedQrAvatarUri, dataUri, StringComparison.Ordinal)) return;

        try
        {
            int comma = dataUri.IndexOf(',');
            if (comma < 0) return;
            byte[] bytes = Convert.FromBase64String(dataUri[(comma + 1)..]);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);

            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            _appliedQrAvatarUri = dataUri;
            QrScannedAvatarBrush.ImageSource = bmp;
            QrScannedAvatarEllipse.Visibility = Visibility.Visible;
            QrOverlayIcon.Visibility = Visibility.Collapsed;
        }
        catch
        {
            _appliedQrAvatarUri = null;
            QrScannedAvatarEllipse.Visibility = Visibility.Collapsed;
            QrOverlayIcon.Visibility = Visibility.Visible;
        }
    }

    private static WriteableBitmap RenderQrMatrixToBitmap(bool[,] matrix, int targetPixels)
    {
        int modules = matrix.GetLength(0);
        const int quietZone = 4;
        int totalModules = modules + quietZone * 2;
        int scale = Math.Max(2, targetPixels / totalModules);
        int width = totalModules * scale;
        int height = width;

        byte[] bgra = new byte[width * height * 4];

        Array.Fill(bgra, (byte)255);

        for (int my = 0; my < modules; my++)
        {
            for (int mx = 0; mx < modules; mx++)
            {
                if (!matrix[my, mx]) continue;
                int py0 = (my + quietZone) * scale;
                int px0 = (mx + quietZone) * scale;
                for (int dy = 0; dy < scale; dy++)
                {
                    int rowOffset = (py0 + dy) * width * 4;
                    for (int dx = 0; dx < scale; dx++)
                    {
                        int idx = rowOffset + (px0 + dx) * 4;

                        bgra[idx] = 0x11;
                        bgra[idx + 1] = 0x11;
                        bgra[idx + 2] = 0x11;
                        bgra[idx + 3] = 0xFF;
                    }
                }
            }
        }

        var wb = new WriteableBitmap(width, height);
        using var stream = wb.PixelBuffer.AsStream();
        stream.Write(bgra, 0, bgra.Length);
        wb.Invalidate();
        return wb;
    }
}