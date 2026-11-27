using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Osmium.Core.Models;
using Osmium.Core.Network;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Osmium.Core.Services;

public sealed class QuickLaunchHostStartedEventArgs : EventArgs
{
    public required string AppName
    {
        get;
        init;
    }

    public required string ExePath
    {
        get;
        init;
    }

    public required string RequesterDeviceId
    {
        get;
        init;
    }

    public required bool IsLocalLaunch
    {
        get;
        init;
    }
}

public sealed class QuickLaunchHostService : IDisposable
{
    private const int IconSize = 64;
    private const int MaxIconDataChars = 90000;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint DiNormal = 0x0003;
    private const uint DiMask = 0x0001;
    private const uint Th32csSnapProcess = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int SwRestore = 9;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;

    private readonly SettingsService _settings;
    private readonly AccountService _account;
    private readonly SignalingClient _signaling;
    private readonly P2PCoordinator _coordinator;
    private readonly LoggingService _log;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<QuickLaunchBrowseResult>> _pendingBrowse =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<QuickLaunchValidateResult>> _pendingValidate =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, PendingStartTracker> _pendingStarts = new(StringComparer.Ordinal);

    private readonly object _hostGuardLock = new();
    private CancellationTokenSource? _inflightLaunchCts;
    private string? _inflightLaunchRequestId;
    private ActiveProcessGuard? _activeGuard;
    private int _disposed;

    private sealed class PendingStartTracker
    {
        public required TaskCompletionSource<QuickLaunchStartResponse> Tcs
        {
            get;
            init;
        }

        public Action<string>? OnStatus
        {
            get;
            init;
        }
    }

    private sealed class ActiveProcessGuard
    {
        public required string RequestId
        {
            get;
            init;
        }

        public required string RequesterDeviceId
        {
            get;
            init;
        }

        public required string TargetDir
        {
            get;
            init;
        }

        public required string TargetExeName
        {
            get;
            init;
        }

        public required HashSet<int> TrackedPids
        {
            get;
            init;
        }

        public required DateTime ArmedAtUtc
        {
            get;
            init;
        }

        public required CancellationTokenSource MonitorCts
        {
            get;
            init;
        }

        public bool SessionObserved
        {
            get;
            set;
        }
    }

    public bool HasActiveSessionGuard
    {
        get
        {
            lock (_hostGuardLock)
            {
                return _activeGuard != null;
            }
        }
    }

    public event Action<QuickLaunchHostStartedEventArgs>? AppLaunchedOnHost;

    public QuickLaunchHostService(
        SettingsService settings,
        AccountService account,
        SignalingClient signaling,
        P2PCoordinator coordinator,
        LoggingService log)
    {
        _settings = settings;
        _account = account;
        _signaling = signaling;
        _coordinator = coordinator;
        _log = log;

        _signaling.QuickLaunchBrowseRequestReceived += OnBrowseRequest;
        _signaling.QuickLaunchValidateRequestReceived += OnValidateRequest;
        _signaling.QuickLaunchStartRequestReceived += OnStartRequest;
        _signaling.QuickLaunchCancelReceived += OnCancelReceived;
        _signaling.QuickLaunchBrowseResponseReceived += OnBrowseResponse;
        _signaling.QuickLaunchValidateResponseReceived += OnValidateResponse;
        _signaling.QuickLaunchStartResponseReceived += OnStartResponse;
        _coordinator.HostSessionStarted += OnHostSessionStarted;
        _coordinator.HostSessionEnded += OnHostSessionEnded;
    }

    public async Task<QuickLaunchBrowseResult> BrowseAsync(string targetDeviceId, string path,
        CancellationToken ct = default)
    {
        string normTarget = DeviceIdentity.NormalizeDeviceId(targetDeviceId);
        string localId = DeviceIdentity.NormalizeDeviceId(_settings.Identity.DeviceId);
        if (string.Equals(normTarget, localId, StringComparison.Ordinal))
        {
            if (!_settings.Settings.HostAllowQuickLaunch)
            {
                return new QuickLaunchBrowseResult
                {
                    Ok = false,
                    Code = OsmiumErrorCode.QuickLaunchDisabled,
                    Reason = "QUICK_LAUNCH_DISABLED"
                };
            }

            return await Task.Run(() => BrowseLocalDirectory(path), ct);
        }

        string reqId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<QuickLaunchBrowseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingBrowse[reqId] = tcs;
        try
        {
            bool sent = await _signaling.SendQuickLaunchBrowseRequestAsync(reqId, normTarget, path);
            if (!sent)
            {
                return new QuickLaunchBrowseResult
                {
                    RequestId = reqId,
                    Ok = false,
                    Code = OsmiumErrorCode.NetworkError,
                    Reason = "SIGNALING_DISCONNECTED"
                };
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new QuickLaunchBrowseResult
            {
                RequestId = reqId,
                Ok = false,
                Code = OsmiumErrorCode.NetworkError,
                Reason = "TIMEOUT"
            };
        }
        finally
        {
            _pendingBrowse.TryRemove(reqId, out _);
        }
    }

    public async Task<QuickLaunchValidateResult> ValidateAsync(string targetDeviceId, string path,
        CancellationToken ct = default)
    {
        string normTarget = DeviceIdentity.NormalizeDeviceId(targetDeviceId);
        string localId = DeviceIdentity.NormalizeDeviceId(_settings.Identity.DeviceId);
        if (string.Equals(normTarget, localId, StringComparison.Ordinal))
        {
            if (!_settings.Settings.HostAllowQuickLaunch)
            {
                return new QuickLaunchValidateResult
                {
                    Ok = false,
                    Code = OsmiumErrorCode.QuickLaunchDisabled,
                    Reason = "QUICK_LAUNCH_DISABLED"
                };
            }

            return await Task.Run(() => ValidateLocalPathAsync(path), ct);
        }

        string reqId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<QuickLaunchValidateResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingValidate[reqId] = tcs;
        try
        {
            bool sent = await _signaling.SendQuickLaunchValidateRequestAsync(reqId, normTarget, path);
            if (!sent)
            {
                return new QuickLaunchValidateResult
                {
                    RequestId = reqId,
                    Ok = false,
                    Code = OsmiumErrorCode.NetworkError,
                    Reason = "SIGNALING_DISCONNECTED"
                };
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new QuickLaunchValidateResult
            {
                RequestId = reqId,
                Ok = false,
                Code = OsmiumErrorCode.NetworkError,
                Reason = "TIMEOUT"
            };
        }
        finally
        {
            _pendingValidate.TryRemove(reqId, out _);
        }
    }

    public async Task<QuickLaunchStartResponse> StartRemoteAppAsync(
        string targetDeviceId,
        string appId,
        string exePath,
        string appName,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        string normTarget = DeviceIdentity.NormalizeDeviceId(targetDeviceId);
        string reqId = Guid.NewGuid().ToString("N");
        var tracker = new PendingStartTracker
        {
            Tcs =
                new TaskCompletionSource<QuickLaunchStartResponse>(TaskCreationOptions.RunContinuationsAsynchronously),
            OnStatus = onStatus
        };
        _pendingStarts[reqId] = tracker;
        try
        {
            bool sent = await _signaling.SendQuickLaunchStartRequestAsync(reqId, normTarget, appId, exePath, appName);
            if (!sent)
            {
                return new QuickLaunchStartResponse
                {
                    RequestId = reqId,
                    Status = "failed",
                    Ok = false,
                    Code = OsmiumErrorCode.NetworkError,
                    Reason = "SIGNALING_DISCONNECTED"
                };
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(42));
            return await tracker.Tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            _ = _signaling.SendQuickLaunchCancelAsync(reqId, normTarget);
            if (ct.IsCancellationRequested) throw;
            return new QuickLaunchStartResponse
            {
                RequestId = reqId,
                Status = "failed",
                Ok = false,
                Code = OsmiumErrorCode.NetworkError,
                Reason = "LAUNCH_TIMEOUT"
            };
        }
        finally
        {
            _pendingStarts.TryRemove(reqId, out _);
        }
    }

    public bool TryLaunchLocalExecutable(string exePath, out string error, string? appName = null)
    {
        error = string.Empty;
        if (!TryNormalizeLocalPath(exePath, out string normPath) || !IsValidWindowsExecutable(normPath))
        {
            error = "INVALID_EXECUTABLE";
            return false;
        }

        try
        {
            string? dir = Path.GetDirectoryName(normPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = normPath,
                WorkingDirectory = string.IsNullOrWhiteSpace(dir) ? Environment.CurrentDirectory : dir,
                UseShellExecute = true
            });
            string displayName = !string.IsNullOrWhiteSpace(appName)
                ? appName.Trim()
                : ExtractFriendlyExeName(normPath);
            AppLaunchedOnHost?.Invoke(new QuickLaunchHostStartedEventArgs
            {
                AppName = displayName,
                ExePath = normPath,
                RequesterDeviceId = DeviceIdentity.NormalizeDeviceId(_settings.Identity.DeviceId),
                IsLocalLaunch = true
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void OnBrowseResponse(QuickLaunchBrowseResult res)
    {
        if (!string.IsNullOrEmpty(res.RequestId) && _pendingBrowse.TryRemove(res.RequestId, out var tcs))
        {
            tcs.TrySetResult(res);
        }
    }

    private void OnValidateResponse(QuickLaunchValidateResult res)
    {
        if (!string.IsNullOrEmpty(res.RequestId) && _pendingValidate.TryRemove(res.RequestId, out var tcs))
        {
            tcs.TrySetResult(res);
        }
    }

    private void OnStartResponse(QuickLaunchStartResponse res)
    {
        if (string.IsNullOrEmpty(res.RequestId)) return;
        if (!_pendingStarts.TryGetValue(res.RequestId, out var tracker)) return;
        if (string.Equals(res.Status, "launching", StringComparison.OrdinalIgnoreCase))
        {
            tracker.OnStatus?.Invoke("launching");
            return;
        }

        if (_pendingStarts.TryRemove(res.RequestId, out var finalTracker))
        {
            finalTracker.Tcs.TrySetResult(res);
        }
    }

    private void OnBrowseRequest(QuickLaunchBrowseRequestEventArgs req)
    {
        _ = Task.Run(async () =>
        {
            QuickLaunchBrowseResult res;
            if (!IsRequesterAuthorizedSameAccount(req.RequesterUserId))
            {
                res = new QuickLaunchBrowseResult
                {
                    RequestId = req.RequestId,
                    Ok = false,
                    Code = OsmiumErrorCode.Forbidden,
                    Reason = "FORBIDDEN"
                };
            }
            else if (!_settings.Settings.HostAllowQuickLaunch)
            {
                res = new QuickLaunchBrowseResult
                {
                    RequestId = req.RequestId,
                    Ok = false,
                    Code = OsmiumErrorCode.QuickLaunchDisabled,
                    Reason = "QUICK_LAUNCH_DISABLED"
                };
            }
            else
            {
                res = BrowseLocalDirectory(req.Path);
                res.RequestId = req.RequestId;
            }

            await _signaling.SendQuickLaunchBrowseResponseAsync(req.RequesterDeviceId, res);
        });
    }

    private void OnValidateRequest(QuickLaunchValidateRequestEventArgs req)
    {
        _ = Task.Run(async () =>
        {
            QuickLaunchValidateResult res;
            if (!IsRequesterAuthorizedSameAccount(req.RequesterUserId))
            {
                res = new QuickLaunchValidateResult
                {
                    RequestId = req.RequestId,
                    Ok = false,
                    Code = OsmiumErrorCode.Forbidden,
                    Reason = "FORBIDDEN"
                };
            }
            else if (!_settings.Settings.HostAllowQuickLaunch)
            {
                res = new QuickLaunchValidateResult
                {
                    RequestId = req.RequestId,
                    Ok = false,
                    Code = OsmiumErrorCode.QuickLaunchDisabled,
                    Reason = "QUICK_LAUNCH_DISABLED"
                };
            }
            else
            {
                res = await ValidateLocalPathAsync(req.Path);
                res.RequestId = req.RequestId;
            }

            await _signaling.SendQuickLaunchValidateResponseAsync(req.RequesterDeviceId, res);
        });
    }

    private void OnCancelReceived(string requestId, string requesterDeviceId)
    {
        lock (_hostGuardLock)
        {
            if (_inflightLaunchCts != null &&
                (string.IsNullOrEmpty(requestId) ||
                 string.Equals(_inflightLaunchRequestId, requestId, StringComparison.Ordinal)))
            {
                try
                {
                    _inflightLaunchCts.Cancel();
                }
                catch
                {
                }
            }

            if (_activeGuard != null &&
                string.Equals(_activeGuard.RequesterDeviceId, DeviceIdentity.NormalizeDeviceId(requesterDeviceId),
                    StringComparison.Ordinal) &&
                !_activeGuard.SessionObserved)
            {
                ClearActiveGuardLocked();
            }
        }
    }

    private void OnHostSessionStarted(HostSessionActiveInfo info)
    {
        lock (_hostGuardLock)
        {
            if (_activeGuard != null &&
                string.Equals(_activeGuard.RequesterDeviceId, DeviceIdentity.NormalizeDeviceId(info.PeerDeviceId),
                    StringComparison.Ordinal))
            {
                _activeGuard.SessionObserved = true;
            }
        }
    }

    private void OnHostSessionEnded(string _)
    {
        lock (_hostGuardLock)
        {
            if (_activeGuard != null && _activeGuard.SessionObserved)
            {
                ClearActiveGuardLocked();
            }
        }
    }

    private void OnStartRequest(QuickLaunchStartRequestEventArgs req)
    {
        _ = Task.Run(() => HandleStartRequestAsync(req));
    }

    private async Task HandleStartRequestAsync(QuickLaunchStartRequestEventArgs req)
    {
        async Task FailAsync(int code, string reason)
        {
            await _signaling.SendQuickLaunchStartResponseAsync(req.RequesterDeviceId, new QuickLaunchStartResponse
            {
                RequestId = req.RequestId,
                Status = "failed",
                Ok = false,
                Code = code,
                Reason = reason
            });
        }

        if (!IsRequesterAuthorizedSameAccount(req.RequesterUserId))
        {
            await FailAsync(OsmiumErrorCode.Forbidden, "FORBIDDEN");
            return;
        }

        if (!_settings.Settings.HostAllowQuickLaunch)
        {
            await FailAsync(OsmiumErrorCode.QuickLaunchDisabled, "QUICK_LAUNCH_DISABLED");
            return;
        }

        if (!_coordinator.EffectiveAllowRemoteControl)
        {
            await FailAsync(OsmiumErrorCode.RemoteDisabled, "REMOTE_DISABLED");
            return;
        }

        if (_coordinator.HasActiveHostPipeline)
        {
            await FailAsync(OsmiumErrorCode.ConcurrentOperation, "HOST_BUSY");
            return;
        }

        if (!TryNormalizeLocalPath(req.ExePath, out string exePath))
        {
            await FailAsync(OsmiumErrorCode.InvalidInput, "INVALID_PATH");
            return;
        }

        string? dirPath = Path.GetDirectoryName(exePath);
        if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath))
        {
            await FailAsync(OsmiumErrorCode.QuickLaunchNotFound, "DIRECTORY_NOT_FOUND");
            return;
        }

        if (!File.Exists(exePath) || !IsValidWindowsExecutable(exePath))
        {
            await FailAsync(OsmiumErrorCode.QuickLaunchNotFound, "BINARY_NOT_FOUND");
            return;
        }

        CancellationTokenSource launchCts;
        lock (_hostGuardLock)
        {
            try
            {
                _inflightLaunchCts?.Cancel();
            }
            catch
            {
            }

            _inflightLaunchCts?.Dispose();
            ClearActiveGuardLocked();
            launchCts = new CancellationTokenSource();
            _inflightLaunchCts = launchCts;
            _inflightLaunchRequestId = req.RequestId;
        }

        try
        {
            await _signaling.SendQuickLaunchStartResponseAsync(req.RequesterDeviceId, new QuickLaunchStartResponse
            {
                RequestId = req.RequestId,
                Status = "launching",
                Ok = true,
                Code = OsmiumErrorCode.Ok
            });

            string exeNameNoExt = Path.GetFileNameWithoutExtension(exePath);
            string normalizedDir = EnsureTrailingSeparator(dirPath);
            var preExistingSameExePids = SnapshotPidsMatchingExePath(exePath);
            DateTime launchStartedUtc = DateTime.UtcNow;

            Process? startedProc;
            try
            {
                startedProc = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = dirPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _log.Warn($"QuickLaunch failed to start '{exePath}': {ex.Message}");
                await FailAsync(OsmiumErrorCode.InternalError, "PROCESS_START_FAILED");
                return;
            }

            var trackedPids = new HashSet<int>();
            if (startedProc != null)
            {
                trackedPids.Add(startedProc.Id);
            }

            _log.Info(
                $"QuickLaunch started '{exePath}' (rootPid={startedProc?.Id ?? 0}) for peer {req.RequesterDeviceId}");

            string displayName = !string.IsNullOrWhiteSpace(req.AppName)
                ? req.AppName.Trim()
                : ExtractFriendlyExeName(exePath);
            AppLaunchedOnHost?.Invoke(new QuickLaunchHostStartedEventArgs
            {
                AppName = displayName,
                ExePath = exePath,
                RequesterDeviceId = DeviceIdentity.NormalizeDeviceId(req.RequesterDeviceId),
                IsLocalLaunch = false
            });

            int foregroundPid = 0;
            var sw = Stopwatch.StartNew();
            const int maxWaitMs = 35000;
            const int earlyExitGraceMs = 2500;
            int promoteAttempts = 0;

            while (sw.ElapsedMilliseconds < maxWaitMs && !launchCts.Token.IsCancellationRequested)
            {
                ExpandTrackedPids(trackedPids, normalizedDir, exeNameNoExt, launchStartedUtc);

                IntPtr fgHwnd = GetForegroundWindow();
                if (fgHwnd != IntPtr.Zero && IsWindowVisible(fgHwnd) && !IsIconic(fgHwnd))
                {
                    GetWindowThreadProcessId(fgHwnd, out uint fgPidRaw);
                    int fgPid = (int)fgPidRaw;
                    if (fgPid > 0)
                    {
                        if (trackedPids.Contains(fgPid))
                        {
                            foregroundPid = fgPid;
                            break;
                        }

                        if (preExistingSameExePids.Contains(fgPid))
                        {
                            trackedPids.Add(fgPid);
                            foregroundPid = fgPid;
                            break;
                        }

                        string? fgPath = TryGetProcessImagePath(fgPid);
                        if (!string.IsNullOrEmpty(fgPath) &&
                            (string.Equals(fgPath, exePath, StringComparison.OrdinalIgnoreCase) ||
                             fgPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase)))
                        {
                            trackedPids.Add(fgPid);
                            foregroundPid = fgPid;
                            break;
                        }
                    }
                }

                if (promoteAttempts < 12)
                {
                    IntPtr candidateHwnd = FindVisibleTopLevelWindowForPids(trackedPids);
                    if (candidateHwnd != IntPtr.Zero)
                    {
                        promoteAttempts++;
                        if (IsIconic(candidateHwnd))
                        {
                            ShowWindow(candidateHwnd, SwRestore);
                        }

                        SetForegroundWindow(candidateHwnd);
                    }
                }

                if (sw.ElapsedMilliseconds >= earlyExitGraceMs && !HasAnyAlivePid(trackedPids))
                {
                    await FailAsync(OsmiumErrorCode.QuickLaunchNotFound, "PROCESS_EXITED_EARLY");
                    return;
                }

                await Task.Delay(120, launchCts.Token);
            }

            if (launchCts.Token.IsCancellationRequested)
            {
                return;
            }

            if (foregroundPid <= 0)
            {
                await FailAsync(OsmiumErrorCode.InternalError, "FOREGROUND_TIMEOUT");
                return;
            }

            var monitorCts = new CancellationTokenSource();
            var guard = new ActiveProcessGuard
            {
                RequestId = req.RequestId,
                RequesterDeviceId = DeviceIdentity.NormalizeDeviceId(req.RequesterDeviceId),
                TargetDir = normalizedDir,
                TargetExeName = exeNameNoExt,
                TrackedPids = new HashSet<int>(trackedPids),
                ArmedAtUtc = DateTime.UtcNow,
                MonitorCts = monitorCts
            };

            lock (_hostGuardLock)
            {
                ClearActiveGuardLocked();
                _activeGuard = guard;
            }

            _log.Info($"QuickLaunch foreground ready (pid={foregroundPid}) for '{exePath}', arming process exit guard");

            await _signaling.SendQuickLaunchStartResponseAsync(req.RequesterDeviceId, new QuickLaunchStartResponse
            {
                RequestId = req.RequestId,
                Status = "ready",
                Ok = true,
                Code = OsmiumErrorCode.Ok,
                Pid = foregroundPid
            });

            _ = Task.Run(() => MonitorProcessExitLoopAsync(guard));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error($"QuickLaunch HandleStartRequestAsync error: {ex.Message}");
            await FailAsync(OsmiumErrorCode.InternalError, "LAUNCH_ERROR");
        }
        finally
        {
            lock (_hostGuardLock)
            {
                if (ReferenceEquals(_inflightLaunchCts, launchCts))
                {
                    _inflightLaunchCts = null;
                    _inflightLaunchRequestId = null;
                }
            }

            launchCts.Dispose();
        }
    }

    private async Task MonitorProcessExitLoopAsync(ActiveProcessGuard guard)
    {
        CancellationToken ct = guard.MonitorCts.Token;
        int deadTicks = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);

                ExpandTrackedPids(guard.TrackedPids, guard.TargetDir, guard.TargetExeName,
                    guard.ArmedAtUtc.AddSeconds(-30));
                bool anyAlive = HasAnyAlivePid(guard.TrackedPids);

                if (!anyAlive)
                {
                    deadTicks++;
                    if (deadTicks >= 2)
                    {
                        _log.Info(
                            $"QuickLaunch tracked process exited for '{guard.TargetExeName}', disconnecting remote session");
                        bool shouldDisconnect = false;
                        lock (_hostGuardLock)
                        {
                            if (ReferenceEquals(_activeGuard, guard))
                            {
                                shouldDisconnect = true;
                                _activeGuard = null;
                            }
                        }

                        if (shouldDisconnect && _coordinator.HasActiveHostPipeline)
                        {
                            _coordinator.StopHostSession("Quick launch app exited");
                        }

                        return;
                    }
                }
                else
                {
                    deadTicks = 0;
                }

                if (!guard.SessionObserved && !_coordinator.HasActiveHostPipeline &&
                    (DateTime.UtcNow - guard.ArmedAtUtc).TotalSeconds > 60)
                {
                    lock (_hostGuardLock)
                    {
                        if (ReferenceEquals(_activeGuard, guard))
                        {
                            ClearActiveGuardLocked();
                        }
                    }

                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warn($"QuickLaunch MonitorProcessExitLoopAsync error: {ex.Message}");
        }
    }

    private void ClearActiveGuardLocked()
    {
        if (_activeGuard != null)
        {
            try
            {
                _activeGuard.MonitorCts.Cancel();
            }
            catch
            {
            }

            _activeGuard.MonitorCts.Dispose();
            _activeGuard = null;
        }
    }

    private bool IsRequesterAuthorizedSameAccount(string requesterUserId)
    {
        return _account.IsLoggedIn &&
               !string.IsNullOrWhiteSpace(requesterUserId) &&
               string.Equals(requesterUserId.Trim(), _account.CurrentUserId, StringComparison.OrdinalIgnoreCase);
    }

    public static QuickLaunchBrowseResult BrowseLocalDirectory(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            var drives = new List<QuickLaunchBrowseEntry>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                    string label = string.IsNullOrWhiteSpace(d.VolumeLabel)
                        ? d.Name.TrimEnd('\\')
                        : $"{d.Name.TrimEnd('\\')} ({d.VolumeLabel})";
                    drives.Add(new QuickLaunchBrowseEntry { Name = label, Path = d.Name });
                }
                catch
                {
                }
            }

            return new QuickLaunchBrowseResult
            {
                Ok = true,
                Code = OsmiumErrorCode.Ok,
                CurrentPath = string.Empty,
                ParentPath = string.Empty,
                Directories = drives,
                Executables = []
            };
        }

        if (!TryNormalizeLocalPath(rawPath, out string normPath))
        {
            return new QuickLaunchBrowseResult
            {
                Ok = false,
                Code = OsmiumErrorCode.InvalidInput,
                Reason = "INVALID_PATH"
            };
        }

        if (File.Exists(normPath))
        {
            normPath = Path.GetDirectoryName(normPath) ?? normPath;
        }

        if (!Directory.Exists(normPath))
        {
            return new QuickLaunchBrowseResult
            {
                Ok = false,
                Code = OsmiumErrorCode.QuickLaunchNotFound,
                Reason = "DIRECTORY_NOT_FOUND"
            };
        }

        var dirs = new List<QuickLaunchBrowseEntry>();
        var exes = new List<QuickLaunchBrowseEntry>();

        try
        {
            foreach (string sub in Directory.EnumerateDirectories(normPath))
            {
                try
                {
                    var di = new DirectoryInfo(sub);
                    if ((di.Attributes & (FileAttributes.Hidden | FileAttributes.System)) ==
                        (FileAttributes.Hidden | FileAttributes.System))
                    {
                        continue;
                    }

                    dirs.Add(new QuickLaunchBrowseEntry { Name = di.Name, Path = di.FullName });
                    if (dirs.Count >= 300) break;
                }
                catch
                {
                }
            }

            foreach (string file in Directory.EnumerateFiles(normPath, "*.exe", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var fi = new FileInfo(file);
                    exes.Add(new QuickLaunchBrowseEntry { Name = fi.Name, Path = fi.FullName });
                    if (exes.Count >= 200) break;
                }
                catch
                {
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return new QuickLaunchBrowseResult
            {
                Ok = false,
                Code = OsmiumErrorCode.Forbidden,
                Reason = "ACCESS_DENIED"
            };
        }
        catch (Exception ex)
        {
            return new QuickLaunchBrowseResult
            {
                Ok = false,
                Code = OsmiumErrorCode.InternalError,
                Reason = ex.Message
            };
        }

        dirs.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        exes.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        string parentPath = string.Empty;
        try
        {
            var parent = Directory.GetParent(normPath.TrimEnd('\\'));
            if (parent != null)
            {
                parentPath = parent.FullName;
            }
        }
        catch
        {
        }

        return new QuickLaunchBrowseResult
        {
            Ok = true,
            Code = OsmiumErrorCode.Ok,
            CurrentPath = normPath,
            ParentPath = parentPath,
            Directories = dirs,
            Executables = exes
        };
    }

    public static async Task<QuickLaunchValidateResult> ValidateLocalPathAsync(string? rawPath)
    {
        if (!TryNormalizeLocalPath(rawPath, out string normPath))
        {
            return new QuickLaunchValidateResult
            {
                Ok = false,
                Code = OsmiumErrorCode.InvalidInput,
                Reason = "INVALID_PATH"
            };
        }

        if (File.Exists(normPath))
        {
            if (!IsValidWindowsExecutable(normPath))
            {
                return new QuickLaunchValidateResult
                {
                    Ok = false,
                    Code = OsmiumErrorCode.InvalidInput,
                    Reason = "NOT_VALID_EXE"
                };
            }

            string? iconData = await TryExtractExeIconDataUriAsync(normPath);
            return new QuickLaunchValidateResult
            {
                Ok = true,
                Code = OsmiumErrorCode.Ok,
                NeedsExeSelection = false,
                DirectoryPath = Path.GetDirectoryName(normPath) ?? string.Empty,
                ResolvedExePath = normPath,
                SuggestedName = ExtractFriendlyExeName(normPath),
                IconData = iconData,
                Candidates = []
            };
        }

        if (!Directory.Exists(normPath))
        {
            return new QuickLaunchValidateResult
            {
                Ok = false,
                Code = OsmiumErrorCode.QuickLaunchNotFound,
                Reason = "PATH_NOT_FOUND"
            };
        }

        var validExes = new List<QuickLaunchBrowseEntry>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(normPath, "*.exe", SearchOption.TopDirectoryOnly))
            {
                if (IsValidWindowsExecutable(file))
                {
                    validExes.Add(new QuickLaunchBrowseEntry
                    {
                        Name = Path.GetFileName(file),
                        Path = file
                    });
                    if (validExes.Count >= 100) break;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return new QuickLaunchValidateResult
            {
                Ok = false,
                Code = OsmiumErrorCode.Forbidden,
                Reason = "ACCESS_DENIED"
            };
        }

        if (validExes.Count == 0)
        {
            return new QuickLaunchValidateResult
            {
                Ok = false,
                Code = OsmiumErrorCode.QuickLaunchNotFound,
                Reason = "NO_EXECUTABLE_IN_DIR",
                DirectoryPath = normPath
            };
        }

        string folderName = new DirectoryInfo(normPath).Name;
        validExes.Sort((a, b) =>
        {
            int sa = ScoreExeCandidate(a.Name, folderName);
            int sb = ScoreExeCandidate(b.Name, folderName);
            if (sa != sb) return sb.CompareTo(sa);
            return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });

        if (validExes.Count == 1)
        {
            string onlyExe = validExes[0].Path;
            string? iconData = await TryExtractExeIconDataUriAsync(onlyExe);
            return new QuickLaunchValidateResult
            {
                Ok = true,
                Code = OsmiumErrorCode.Ok,
                NeedsExeSelection = false,
                DirectoryPath = normPath,
                ResolvedExePath = onlyExe,
                SuggestedName = ExtractFriendlyExeName(onlyExe),
                IconData = iconData,
                Candidates = validExes
            };
        }

        return new QuickLaunchValidateResult
        {
            Ok = true,
            Code = OsmiumErrorCode.Ok,
            NeedsExeSelection = true,
            DirectoryPath = normPath,
            Candidates = validExes
        };
    }

    private static int ScoreExeCandidate(string fileName, string folderName)
    {
        string lower = fileName.ToLowerInvariant();
        if (lower.Contains("unins") || lower.Contains("crash") || lower.Contains("redist") ||
            lower.Contains("vcredist") || lower.Contains("dxsetup") || lower.Contains("updater"))
        {
            return -10;
        }

        string noExt = Path.GetFileNameWithoutExtension(fileName);
        if (!string.IsNullOrWhiteSpace(folderName) &&
            string.Equals(noExt, folderName, StringComparison.OrdinalIgnoreCase))
        {
            return 20;
        }

        return 0;
    }

    public static bool TryNormalizeLocalPath(string? rawPath, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath)) return false;

        string trimmed = rawPath.Trim().Trim('"');
        if (trimmed.Length < 2 || trimmed.Length > 520) return false;
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (!char.IsAsciiLetter(trimmed[0]) || trimmed[1] != ':') return false;
        if (trimmed.Length == 2) trimmed += @"\";
        else if (trimmed[2] != '\\') return false;

        try
        {
            string full = Path.GetFullPath(trimmed);
            if (!Path.IsPathFullyQualified(full) || full.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            normalized = full;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsValidWindowsExecutable(string filePath)
    {
        try
        {
            if (!filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            var fi = new FileInfo(filePath);
            if (!fi.Exists || fi.Length < 64) return false;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[64];
            if (fs.Read(header) < 64) return false;
            if (header[0] != 0x4D || header[1] != 0x5A) return false;

            int peOffset = BitConverter.ToInt32(header.Slice(60, 4));
            if (peOffset < 64 || peOffset > fi.Length - 4) return false;

            fs.Seek(peOffset, SeekOrigin.Begin);
            Span<byte> peSig = stackalloc byte[4];
            if (fs.Read(peSig) < 4) return false;
            return peSig[0] == 0x50 && peSig[1] == 0x45 && peSig[2] == 0x00 && peSig[3] == 0x00;
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractFriendlyExeName(string exePath)
    {
        string fallback = Path.GetFileNameWithoutExtension(exePath);
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            string? candidate = !string.IsNullOrWhiteSpace(info.ProductName)
                ? info.ProductName.Trim()
                : (!string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription.Trim() : null);

            if (!string.IsNullOrWhiteSpace(candidate) &&
                candidate.Length <= 64 &&
                !candidate.Contains("Operating System", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
        catch
        {
        }

        return string.IsNullOrWhiteSpace(fallback) ? "App" : fallback;
    }

    public static async Task<string?> TryExtractExeIconDataUriAsync(string exePath)
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            IntPtr[] icons = new IntPtr[1];
            uint[] ids = new uint[1];
            uint extracted = PrivateExtractIconsW(exePath, 0, IconSize, IconSize, icons, ids, 1, 0);
            if (extracted > 0 && icons[0] != IntPtr.Zero)
            {
                hIcon = icons[0];
            }

            if (hIcon == IntPtr.Zero)
            {
                var shfi = new ShFileInfo();
                if (SHGetFileInfoW(exePath, 0, ref shfi, (uint)Marshal.SizeOf<ShFileInfo>(),
                        ShgfiIcon | ShgfiLargeIcon) != IntPtr.Zero)
                {
                    hIcon = shfi.hIcon;
                }
            }

            if (hIcon == IntPtr.Zero) return null;

            byte[]? bgra = RenderIconToBgra(hIcon, IconSize);
            if (bgra == null) return null;

            using var outStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                IconSize,
                IconSize,
                96,
                96,
                bgra);
            await encoder.FlushAsync();

            if (outStream.Size == 0 || outStream.Size > 65536) return null;
            byte[] pngBytes = new byte[outStream.Size];
            outStream.Seek(0);
            await outStream.ReadAsync(pngBytes.AsBuffer(), (uint)pngBytes.Length, InputStreamOptions.None);
            string uri = "data:image/png;base64," + Convert.ToBase64String(pngBytes);
            return uri.Length <= MaxIconDataChars ? uri : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero)
            {
                DestroyIcon(hIcon);
            }
        }
    }

    private static byte[]? RenderIconToBgra(IntPtr hIcon, int size)
    {
        IntPtr hdc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;
        try
        {
            hdc = CreateCompatibleDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return null;

            var bmi = new BitmapInfo
            {
                bmiHeader = new BitmapInfoHeader
                {
                    biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    biWidth = size,
                    biHeight = -size,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };

            hBitmap = CreateDIBSection(hdc, in bmi, 0, out IntPtr bitsPtr, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || bitsPtr == IntPtr.Zero) return null;

            oldObj = SelectObject(hdc, hBitmap);
            int byteCount = size * size * 4;
            byte[] pixels = new byte[byteCount];
            Marshal.Copy(pixels, 0, bitsPtr, byteCount);

            if (!DrawIconEx(hdc, 0, 0, hIcon, size, size, 0, IntPtr.Zero, DiNormal))
            {
                return null;
            }

            Marshal.Copy(bitsPtr, pixels, 0, byteCount);

            bool hasAlpha = false;
            for (int i = 3; i < byteCount; i += 4)
            {
                if (pixels[i] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }

            if (!hasAlpha)
            {
                byte[] maskBuf = new byte[byteCount];
                Array.Fill(maskBuf, (byte)0xFF);
                Marshal.Copy(maskBuf, 0, bitsPtr, byteCount);
                if (DrawIconEx(hdc, 0, 0, hIcon, size, size, 0, IntPtr.Zero, DiMask))
                {
                    Marshal.Copy(bitsPtr, maskBuf, 0, byteCount);
                    for (int i = 0; i < byteCount; i += 4)
                    {
                        bool opaque = maskBuf[i] < 128;
                        if (opaque)
                        {
                            pixels[i + 3] = 255;
                        }
                        else
                        {
                            pixels[i] = 0;
                            pixels[i + 1] = 0;
                            pixels[i + 2] = 0;
                            pixels[i + 3] = 0;
                        }
                    }
                }
            }

            return pixels;
        }
        finally
        {
            if (oldObj != IntPtr.Zero && hdc != IntPtr.Zero) SelectObject(hdc, oldObj);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (hdc != IntPtr.Zero) DeleteDC(hdc);
        }
    }

    private static string EnsureTrailingSeparator(string dir)
        => dir.EndsWith('\\') ? dir : dir + "\\";

    private static HashSet<int> SnapshotPidsMatchingExePath(string exePath)
    {
        var result = new HashSet<int>();
        foreach (var (pid, _) in EnumerateProcessParents())
        {
            string? path = TryGetProcessImagePath(pid);
            if (!string.IsNullOrEmpty(path) && string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(pid);
            }
        }

        return result;
    }

    private static void ExpandTrackedPids(HashSet<int> trackedPids, string normalizedDir, string exeNameNoExt,
        DateTime startedSinceUtc)
    {
        var snapshot = EnumerateProcessParents();
        bool added;
        do
        {
            added = false;
            foreach (var (pid, parentPid) in snapshot)
            {
                if (pid <= 4 || trackedPids.Contains(pid)) continue;
                if (trackedPids.Contains(parentPid))
                {
                    trackedPids.Add(pid);
                    added = true;
                }
            }
        } while (added);

        foreach (var (pid, _) in snapshot)
        {
            if (pid <= 4 || trackedPids.Contains(pid)) continue;
            string? imgPath = TryGetProcessImagePath(pid);
            if (string.IsNullOrEmpty(imgPath)) continue;

            bool pathMatches = imgPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(Path.GetFileNameWithoutExtension(imgPath), exeNameNoExt,
                                   StringComparison.OrdinalIgnoreCase);
            if (!pathMatches) continue;

            try
            {
                using var proc = Process.GetProcessById(pid);
                if (proc.StartTime.ToUniversalTime() >= startedSinceUtc.AddSeconds(-2))
                {
                    trackedPids.Add(pid);
                }
            }
            catch
            {
            }
        }
    }

    private static bool HasAnyAlivePid(HashSet<int> pids)
    {
        foreach (int pid in pids)
        {
            if (pid <= 0) continue;
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited) return true;
            }
            catch
            {
            }
        }

        return false;
    }

    private static List<(int Pid, int ParentPid)> EnumerateProcessParents()
    {
        var list = new List<(int, int)>();
        IntPtr snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;

        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (Process32FirstW(snap, ref entry))
            {
                do
                {
                    list.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID));
                } while (Process32NextW(snap, ref entry));
            }
        }
        finally
        {
            CloseHandle(snap);
        }

        return list;
    }

    private static string? TryGetProcessImagePath(int pid)
    {
        IntPtr hProc = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (hProc == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint len = (uint)sb.Capacity;
            if (QueryFullProcessImageNameW(hProc, 0, sb, ref len) && len > 0)
            {
                return sb.ToString(0, (int)len);
            }

            return null;
        }
        finally
        {
            CloseHandle(hProc);
        }
    }

    private static IntPtr FindVisibleTopLevelWindowForPids(HashSet<int> pids)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint pidRaw);
            if (!pids.Contains((int)pidRaw)) return true;

            int exStyle = GetWindowLongW(hwnd, GwlExStyle);
            if ((exStyle & WsExToolWindow) != 0) return true;

            if (GetWindowRect(hwnd, out var rc))
            {
                int w = rc.Right - rc.Left;
                int h = rc.Bottom - rc.Top;
                if (w >= 80 && h >= 60)
                {
                    found = hwnd;
                    return false;
                }
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _signaling.QuickLaunchBrowseRequestReceived -= OnBrowseRequest;
        _signaling.QuickLaunchValidateRequestReceived -= OnValidateRequest;
        _signaling.QuickLaunchStartRequestReceived -= OnStartRequest;
        _signaling.QuickLaunchCancelReceived -= OnCancelReceived;
        _signaling.QuickLaunchBrowseResponseReceived -= OnBrowseResponse;
        _signaling.QuickLaunchValidateResponseReceived -= OnValidateResponse;
        _signaling.QuickLaunchStartResponseReceived -= OnStartResponse;
        _coordinator.HostSessionStarted -= OnHostSessionStarted;
        _coordinator.HostSessionEnded -= OnHostSessionEnded;

        lock (_hostGuardLock)
        {
            try
            {
                _inflightLaunchCts?.Cancel();
            }
            catch
            {
            }

            _inflightLaunchCts?.Dispose();
            _inflightLaunchCts = null;
            ClearActiveGuardLocked();
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(
        string szFileName,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        [Out] IntPtr[] phicon,
        [Out] uint[] piconid,
        uint nIcons,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(
        string pszPath,
        uint dwFileAttributes,
        ref ShFileInfo psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(
        IntPtr hdc,
        int xLeft,
        int yTop,
        IntPtr hIcon,
        int cxWidth,
        int cyWidth,
        uint istepIfAniCur,
        IntPtr hbrFlickerFreeDraw,
        uint diFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        in BitmapInfo pbmi,
        uint usage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName,
        ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}