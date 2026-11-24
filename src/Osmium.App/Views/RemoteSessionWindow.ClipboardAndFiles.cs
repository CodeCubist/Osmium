using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Osmium.App.Services;
using Osmium.Core.Interop;
using Osmium.Core.Models;
using Osmium.Core.Network;
using Osmium.Core.Services;

namespace Osmium.App.Views;

public sealed partial class RemoteSessionWindow : Window
{
    private const uint WmClipboardUpdate = 0x031D;
    private const long BackgroundAutoSendThresholdBytes = 10L * 1024 * 1024;

    private OsmiumAuxDataDelegate? _auxDataDel;
    private AuxTransferChannel? _auxChannel;
    private bool _clipboardListenerRegistered;

    private bool _sharedClipboardEnabled;
    private bool _fileTransferEnabled;
    private volatile bool _hostAllowsFileTransfer = true;
    private volatile bool _hasHostFilePolicy;

    private long _lastClientWrittenClipboardSeq = -1;
    private long _lastProcessedClipboardSeq = -1;
    private byte[]? _lastSyncedClipboardHash;
    private string? _lastSentFileDropSig;

    private readonly object _pendingFileDropLock = new();
    private List<string>? _pendingOutgoingFileDropPaths;
    private string? _pendingOutgoingFileDropSig;
    private string _latestTransferStatusText = string.Empty;
    private AuxFeatureConfig? _lastSentFeatureConfig;

    private void InitializeClipboardAndTransferDefaults()
    {
        var s = App.Engine.SettingsService.Settings;
        _sharedClipboardEnabled = s.EnableSharedClipboard;
        _fileTransferEnabled = s.EnableFileTransfer;

        SharedClipboardMenuItem.IsChecked = _sharedClipboardEnabled;
        FileTransferMenuItem.IsChecked = _fileTransferEnabled;
        UpdateTransferStatusHintUi();
    }

    private void AttachClipboardListener()
    {
        if (_hwnd == IntPtr.Zero || _clipboardListenerRegistered) return;
        try
        {
            _clipboardListenerRegistered = AddClipboardFormatListener(_hwnd);
        }
        catch
        {
            _clipboardListenerRegistered = false;
        }
    }

    private void DetachClipboardListener()
    {
        if (_hwnd == IntPtr.Zero || !_clipboardListenerRegistered) return;
        try
        {
            RemoveClipboardFormatListener(_hwnd);
        }
        catch
        {
        }

        _clipboardListenerRegistered = false;
    }

    private void SetupAuxTransferChannel()
    {
        DisposeAuxTransferChannel();
        _ = Task.Run(() => WindowsClipboardHelper.CleanupOldStagingDirs(TimeSpan.FromHours(6)));

        var channel = new AuxTransferChannel(
            isController: true,
            sendRaw: (data, len) =>
            {
                IntPtr h = Volatile.Read(ref _clientHandle);
                if (h == IntPtr.Zero || _isClosed || _remoteSessionEnded) return false;
                return NativeMethods.Osmium_ClientSendAuxData(h, data, len) != 0;
            },
            log: App.Engine.Logging,
            reliableUnderlyingTransport: _launchArgs.TransportRoute == 4 || _launchArgs.KcpEnabled);

        channel.ValidateIncomingTransfer = (type, totalBytes) =>
        {
            if (type == AuxTransferType.DesktopWallpaper)
            {
                return totalBytes is > 0 and <= DeviceWallpaperStore.MaxWallpaperBytes
                    ? AuxTransferRejectReason.None
                    : AuxTransferRejectReason.TooLarge;
            }

            if (type != AuxTransferType.FileBatch) return AuxTransferRejectReason.Disabled;
            if (!_fileTransferEnabled) return AuxTransferRejectReason.Disabled;

            var s = App.Engine.SettingsService.Settings;
            if (s.FileTransferDirection != FileTransferDirection.Bidirectional &&
                s.FileTransferDirection != FileTransferDirection.HostToControllerOnly)
            {
                return AuxTransferRejectReason.DirectionNotAllowed;
            }

            long maxBytes = (long)Math.Clamp(s.FileTransferMaxSizeMb, 1, 4096) * 1024 * 1024 + 4L * 1024 * 1024;
            return totalBytes <= maxBytes ? AuxTransferRejectReason.None : AuxTransferRejectReason.TooLarge;
        };

        channel.DesktopWallpaperReceived += wallpaperBytes =>
        {
            if (_isClosed || _remoteSessionEnded) return;
            string targetId = _launchArgs.TargetDeviceId;
            if (string.IsNullOrWhiteSpace(targetId) || wallpaperBytes == null || wallpaperBytes.Length == 0) return;

            _ = Task.Run(() =>
            {
                string? savedPath = App.Engine.DeviceWallpapers.SaveWallpaper(targetId, wallpaperBytes);
                if (!string.IsNullOrEmpty(savedPath))
                {
                    App.Engine.Logging.Info(
                        $"Client cached remote desktop wallpaper for {targetId} ({wallpaperBytes.Length} bytes)");
                }
            });
        };

        channel.HostFileTransferPolicyReceived += hostAllows =>
        {
            bool prev = _hostAllowsFileTransfer;
            bool hadBefore = _hasHostFilePolicy;
            _hostAllowsFileTransfer = hostAllows;
            _hasHostFilePolicy = true;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosed) return;
                UpdateTransferStatusHintUi();
                if (!hostAllows && _fileTransferEnabled && (!hadBefore || prev))
                {
                    ShowTopToast(LocalizationService.GetString("Session_ToastHostFileTransferDisabled"));
                }
            });
        };

        channel.FilesReceived += (roots, itemCount, totalBytes) =>
        {
            if (_isClosed || _remoteSessionEnded) return;
            if (WindowsClipboardHelper.TrySetDropFiles(roots,
                    seq => Volatile.Write(ref _lastClientWrittenClipboardSeq, seq)))
            {
                App.Engine.Logging.Info(
                    $"Client received {itemCount} file item(s) ({totalBytes} bytes) from host; ready to paste");
            }
        };

        channel.TransferProgressChanged += OnAuxTransferProgressChanged;

        _auxDataDel = (dataPtr, length) =>
        {
            if (dataPtr == IntPtr.Zero || length <= 0 || _isClosed || _remoteSessionEnded) return;
            var ch = Volatile.Read(ref _auxChannel);
            if (ch == null) return;
            byte[] copy = new byte[length];
            Marshal.Copy(dataPtr, copy, 0, length);
            ch.HandleIncomingPacket(copy);
        };

        Volatile.Write(ref _auxChannel, channel);
    }

    private void StartAuxTransferAfterSessionReady()
    {
        SendAuxFeatureConfig();

        if (_sharedClipboardEnabled || _fileTransferEnabled)
        {
            TriggerControllerClipboardCheck(forceClipboardSync: _sharedClipboardEnabled, isActivationTrigger: true);
        }
    }

    private void DisposeAuxTransferChannel()
    {
        var ch = Interlocked.Exchange(ref _auxChannel, null);
        if (ch != null)
        {
            ch.TransferProgressChanged -= OnAuxTransferProgressChanged;
            ch.Dispose();
        }
    }

    private void SendAuxFeatureConfig(bool force = true)
    {
        var ch = Volatile.Read(ref _auxChannel);
        if (ch == null || _isClosed || _remoteSessionEnded) return;

        var s = App.Engine.SettingsService.Settings;
        var cfg = new AuxFeatureConfig(
            SharedClipboardEnabled: _sharedClipboardEnabled,
            FileTransferEnabled: _fileTransferEnabled,
            FileTransferDirection: s.FileTransferDirection,
            MaxFileTransferMb: Math.Clamp(s.FileTransferMaxSizeMb, 1, 4096),
            MaxClipboardMb: Math.Clamp(s.SharedClipboardMaxSizeMb, 1, 64));

        if (!force && Equals(_lastSentFeatureConfig, cfg)) return;
        _lastSentFeatureConfig = cfg;
        ch.SendFeatureConfig(cfg);
    }

    private void SharedClipboardMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi) return;
        _sharedClipboardEnabled = SharedClipboardMenuItem.IsChecked;

        var s = App.Engine.SettingsService.Settings;
        s.EnableSharedClipboard = _sharedClipboardEnabled;
        App.Engine.SettingsService.SaveSettings();

        SendAuxFeatureConfig();
        UpdateTransferStatusHintUi();

        if (_sharedClipboardEnabled)
        {
            _lastSyncedClipboardHash = null;
            ShowTopToast(LocalizationService.GetString("Session_ToastSharedClipboardOn"));
            TriggerControllerClipboardCheck(forceClipboardSync: true, isActivationTrigger: false);
        }
        else
        {
            ShowTopToast(LocalizationService.GetString("Session_ToastSharedClipboardOff"));
        }
    }

    private void FileTransferMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi) return;
        _fileTransferEnabled = FileTransferMenuItem.IsChecked;

        var s = App.Engine.SettingsService.Settings;
        s.EnableFileTransfer = _fileTransferEnabled;
        App.Engine.SettingsService.SaveSettings();

        SendAuxFeatureConfig();
        UpdateTransferStatusHintUi();

        if (_fileTransferEnabled)
        {
            if (_hasHostFilePolicy && !_hostAllowsFileTransfer)
            {
                ShowTopToast(LocalizationService.GetString("Session_ToastHostFileTransferDisabled"));
            }
            else
            {
                ShowTopToast(LocalizationService.GetString("Session_ToastFileTransferOn"));
                TriggerControllerClipboardCheck(forceClipboardSync: false, isActivationTrigger: true);
            }
        }
        else
        {
            lock (_pendingFileDropLock)
            {
                _pendingOutgoingFileDropPaths = null;
                _pendingOutgoingFileDropSig = null;
            }

            Volatile.Read(ref _auxChannel)?.CancelAllFileTransfers();
            _latestTransferStatusText = string.Empty;
            UpdateTransferStatusHintUi();
            ShowTopToast(LocalizationService.GetString("Session_ToastFileTransferOff"));
        }
    }

    private void OnLocalClipboardUpdated()
    {
        if (_isClosed || _remoteSessionEnded) return;
        if (!_sharedClipboardEnabled && !_fileTransferEnabled) return;
        TriggerControllerClipboardCheck(forceClipboardSync: false, isActivationTrigger: false);
    }

    private void OnSessionWindowActivatedForClipboard()
    {
        if (_isClosed || _remoteSessionEnded) return;

        SendAuxFeatureConfig(force: false);

        
        List<string>? pendingPaths = null;
        string? pendingSig = null;
        lock (_pendingFileDropLock)
        {
            if (_pendingOutgoingFileDropPaths != null && _pendingOutgoingFileDropSig != null)
            {
                pendingPaths = _pendingOutgoingFileDropPaths;
                pendingSig = _pendingOutgoingFileDropSig;
                _pendingOutgoingFileDropPaths = null;
                _pendingOutgoingFileDropSig = null;
            }
        }

        if (pendingPaths != null && pendingSig != null && CanSendFilesToHostNow())
        {
            _lastSentFileDropSig = pendingSig;
            var ch = Volatile.Read(ref _auxChannel);
            if (ch != null)
            {
                long maxBytes = (long)Math.Clamp(App.Engine.SettingsService.Settings.FileTransferMaxSizeMb, 1, 4096) *
                                1024 * 1024;
                _ = ch.StartSendFilesAsync(pendingPaths, maxBytes);
            }

            return;
        }

        if (_sharedClipboardEnabled || _fileTransferEnabled)
        {
            TriggerControllerClipboardCheck(forceClipboardSync: false, isActivationTrigger: true);
        }
    }

    private bool CanSendFilesToHostNow()
    {
        if (!_fileTransferEnabled || !_hostAllowsFileTransfer) return false;
        var dir = App.Engine.SettingsService.Settings.FileTransferDirection;
        return dir == FileTransferDirection.Bidirectional || dir == FileTransferDirection.ControllerToHostOnly;
    }

    private void TriggerControllerClipboardCheck(bool forceClipboardSync, bool isActivationTrigger)
    {
        var ch = Volatile.Read(ref _auxChannel);
        if (ch == null || _isClosed || _remoteSessionEnded) return;

        _ = Task.Run(() =>
        {
            if (_isClosed || _remoteSessionEnded) return;

            uint seq = WindowsClipboardHelper.GetSequenceNumber();
            long writtenSeq = Volatile.Read(ref _lastClientWrittenClipboardSeq);
            if (writtenSeq >= 0 && seq == (uint)writtenSeq) return;

            long prevProcessed = Volatile.Read(ref _lastProcessedClipboardSeq);
            if (!forceClipboardSync && !isActivationTrigger && prevProcessed >= 0 && seq == (uint)prevProcessed)
                return;

            
            if (WindowsClipboardHelper.HasDropFiles())
            {
                Volatile.Write(ref _lastProcessedClipboardSeq, seq);
                if (!CanSendFilesToHostNow()) return;

                if (!WindowsClipboardHelper.TryReadDropFiles(out var paths) || paths.Count == 0) return;
                if (paths.Any(WindowsClipboardHelper.IsInsideStagingDir)) return;

                string sig = $"{seq}|{string.Join('|', paths)}";
                if (string.Equals(sig, _lastSentFileDropSig, StringComparison.Ordinal)) return;

                bool shouldSendNow = _isWindowFocused || isActivationTrigger ||
                                     IsSmallFileSelection(paths, BackgroundAutoSendThresholdBytes);
                if (!shouldSendNow)
                {
                    lock (_pendingFileDropLock)
                    {
                        _pendingOutgoingFileDropPaths = paths;
                        _pendingOutgoingFileDropSig = sig;
                    }

                    return;
                }

                _lastSentFileDropSig = sig;
                long maxBytes = (long)Math.Clamp(App.Engine.SettingsService.Settings.FileTransferMaxSizeMb, 1, 4096) *
                                1024 * 1024;
                _ = ch.StartSendFilesAsync(paths, maxBytes);
                return;
            }

            
            if (!_sharedClipboardEnabled) return;
            Volatile.Write(ref _lastProcessedClipboardSeq, seq);

            var settings = App.Engine.SettingsService.Settings;
            long maxClipBytes = (long)Math.Clamp(settings.SharedClipboardMaxSizeMb, 1, 64) * 1024 * 1024;

            if (WindowsClipboardHelper.HasUnicodeText() && WindowsClipboardHelper.TryReadText(out string text))
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(text);
                byte[] hash = SHA256.HashData(utf8);
                if (!forceClipboardSync && _lastSyncedClipboardHash != null &&
                    hash.AsSpan().SequenceEqual(_lastSyncedClipboardHash))
                {
                    return;
                }

                _lastSyncedClipboardHash = hash;
                ch.SendClipboardText(text, maxClipBytes);
                return;
            }

            if (settings.SharedClipboardSyncImages && WindowsClipboardHelper.HasDibImage())
            {
                
                if (WindowsClipboardHelper.TryReadDib(64 * 1024 * 1024, out byte[] dibBytes))
                {
                    byte[] hash = SHA256.HashData(dibBytes);
                    if (!forceClipboardSync && _lastSyncedClipboardHash != null &&
                        hash.AsSpan().SequenceEqual(_lastSyncedClipboardHash))
                    {
                        return;
                    }

                    _lastSyncedClipboardHash = hash;
                    ch.SendClipboardImageDib(dibBytes, maxClipBytes);
                }
            }
        });
    }

    private static bool IsSmallFileSelection(IReadOnlyList<string> paths, long thresholdBytes)
    {
        long total = 0;
        foreach (string p in paths)
        {
            if (Directory.Exists(p)) return false;
            try
            {
                total += new FileInfo(p).Length;
                if (total > thresholdBytes) return false;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    private void OnAuxTransferProgressChanged(AuxTransferProgress p)
    {
        if (_isClosed || _remoteSessionEnded) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosed || _remoteSessionEnded) return;

            if (p.TransferType is AuxTransferType.ClipboardText or AuxTransferType.ClipboardImage)
            {
                if (p.State == AuxTransferState.Rejected && p.RejectReason == AuxTransferRejectReason.TooLarge)
                {
                    int maxMb = Math.Clamp(App.Engine.SettingsService.Settings.SharedClipboardMaxSizeMb, 1, 64);
                    ShowTopToast(LocalizationService.Format("Session_ToastClipboardTooLargeFmt", maxMb));
                }

                return;
            }

            if (p.TransferType != AuxTransferType.FileBatch) return;

            int pct = p.TotalBytes > 0
                ? Math.Clamp((int)Math.Round(p.TransferredBytes * 100.0 / p.TotalBytes), 0, 100)
                : 100;
            string name = string.IsNullOrWhiteSpace(p.SummaryName) ? $"{p.ItemCount}" : p.SummaryName;

            switch (p.State)
            {
                case AuxTransferState.Started:
                    _latestTransferStatusText = p.IsOutgoing
                        ? LocalizationService.Format("Session_FileSendingFmt", name, 0)
                        : LocalizationService.Format("Session_FileReceivingFmt", name, 0);
                    ShowTopToast(_latestTransferStatusText);
                    break;

                case AuxTransferState.Progress:
                    _latestTransferStatusText = p.IsOutgoing
                        ? LocalizationService.Format("Session_FileSendingFmt", name, pct)
                        : LocalizationService.Format("Session_FileReceivingFmt", name, pct);
                    ShowTopToast(_latestTransferStatusText);
                    break;

                case AuxTransferState.Completed:
                    _latestTransferStatusText = string.Empty;
                    ShowTopToast(p.IsOutgoing
                        ? LocalizationService.Format("Session_FileSentReadyFmt", p.ItemCount)
                        : LocalizationService.Format("Session_FileReceivedReadyFmt", p.ItemCount));
                    break;

                case AuxTransferState.Rejected:
                    _latestTransferStatusText = string.Empty;
                    if (p.RejectReason == AuxTransferRejectReason.TooLarge)
                    {
                        int maxMb = Math.Clamp(App.Engine.SettingsService.Settings.FileTransferMaxSizeMb, 1, 4096);
                        ShowTopToast(LocalizationService.Format("Session_ToastFileTooLargeFmt", maxMb));
                    }
                    else if (p.RejectReason == AuxTransferRejectReason.Disabled)
                    {
                        ShowTopToast(LocalizationService.GetString("Session_ToastHostFileTransferDisabled"));
                    }
                    else if (p.RejectReason == AuxTransferRejectReason.DirectionNotAllowed)
                    {
                        ShowTopToast(LocalizationService.GetString("Session_ToastFileDirectionBlocked"));
                    }
                    break;

                case AuxTransferState.Failed:
                    _latestTransferStatusText = string.Empty;
                    ShowTopToast(LocalizationService.GetString("Session_ToastFileTransferFailed"));
                    break;

                case AuxTransferState.Cancelled:
                    _latestTransferStatusText = string.Empty;
                    break;
            }

            UpdateTransferStatusHintUi();
        });
    }

    private void UpdateTransferStatusHintUi()
    {
        if (TransferStatusMenuItem == null) return;

        if (!string.IsNullOrEmpty(_latestTransferStatusText))
        {
            TransferStatusMenuItem.Text = _latestTransferStatusText;
            TransferStatusMenuItem.Visibility = Visibility.Visible;
        }
        else if (_fileTransferEnabled && _hasHostFilePolicy && !_hostAllowsFileTransfer)
        {
            TransferStatusMenuItem.Text = LocalizationService.GetString("Session_ToastHostFileTransferDisabled");
            TransferStatusMenuItem.Visibility = Visibility.Visible;
        }
        else
        {
            TransferStatusMenuItem.Text = string.Empty;
            TransferStatusMenuItem.Visibility = Visibility.Collapsed;
        }
    }
}
