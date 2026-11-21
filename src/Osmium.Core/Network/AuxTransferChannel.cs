using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Osmium.Core.Models;
using Osmium.Core.Services;

namespace Osmium.Core.Network;

public enum AuxTransferType : byte
{
    ClipboardText = 1,
    ClipboardImage = 2,
    FileBatch = 3
}

public enum AuxTransferRejectReason : byte
{
    None = 0,
    Disabled = 1,
    TooLarge = 2,
    DirectionNotAllowed = 3,
    IoError = 4,
    Cancelled = 5
}

public enum AuxTransferState
{
    Started,
    Progress,
    Completed,
    Failed,
    Cancelled,
    Rejected
}

public sealed record AuxFeatureConfig(
    bool SharedClipboardEnabled,
    bool FileTransferEnabled,
    FileTransferDirection FileTransferDirection,
    int MaxFileTransferMb,
    int MaxClipboardMb);

public sealed record AuxTransferProgress(
    bool IsOutgoing,
    AuxTransferType TransferType,
    string SummaryName,
    int ItemCount,
    long TransferredBytes,
    long TotalBytes,
    AuxTransferState State,
    AuxTransferRejectReason RejectReason = AuxTransferRejectReason.None,
    string? ErrorMessage = null);

public sealed class AuxTransferChannel : IDisposable
{
    private const byte KindFeatureConfig = 1;
    private const byte KindFeatureConfigAck = 2;
    private const byte KindTransferStart = 10;
    private const byte KindTransferStartAck = 11;
    private const byte KindTransferChunk = 12;
    private const byte KindTransferAck = 13;
    private const byte KindTransferCancel = 14;

    private const int ChunkPayloadSize = 1160;
    private const int WindowSize = 64;
    private const int MaxManifestBytes = 4 * 1024 * 1024;
    private const int MaxDecompressedImageBytes = 64 * 1024 * 1024;
    private const int StartRetransmitMs = 180;
    private const int StartTimeoutMs = 8000;
    private const int ChunkRetransmitMs = 130;
    private const int IdleTimeoutMs = 18000;

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private readonly bool _isController;
    private readonly string _stagingSubDir;
    private readonly Func<byte[], int, bool> _sendRaw;
    private readonly LoggingService? _log;
    private readonly CancellationTokenSource _disposeCts = new();

    private int _nextTransferSeq;
    private volatile bool _disposed;

    // Reliable FeatureConfig sync (Controller -> Host)
    private readonly object _configLock = new();
    private uint _pendingConfigSeq;
    private uint _ackedConfigSeq;
    private AuxFeatureConfig? _pendingConfig;
    private long _lastConfigSendMs;
    private uint _lastRecvConfigSeq;

    // Active transfers (keyed by transferId)
    private readonly ConcurrentDictionary<uint, OutgoingTransfer> _outgoing = new();
    private readonly ConcurrentDictionary<uint, IncomingTransfer> _incoming = new();
    private readonly ConcurrentDictionary<uint, (uint TotalChunks, long CompletedAtMs)> _recentlyCompletedIncoming = new();
    private readonly object _laneLock = new();
    private uint _activeClipboardOutId;
    private uint _activeFileOutId;

    private readonly Thread _pumpThread;
    private readonly AutoResetEvent _pumpWake = new(false);

    public Func<AuxTransferType, long, AuxTransferRejectReason>? ValidateIncomingTransfer { get; set; }
    public Func<bool>? GetHostAllowsFileTransfer { get; set; }

    public event Action<AuxFeatureConfig>? RemoteFeatureConfigReceived;
    public event Action<bool>? HostFileTransferPolicyReceived;
    public event Action<string>? ClipboardTextReceived;
    public event Action<byte[]>? ClipboardImageReceived;
    public event Action<IReadOnlyList<string>, int, long>? FilesReceived;
    public event Action<AuxTransferProgress>? TransferProgressChanged;

    public AuxTransferChannel(bool isController, Func<byte[], int, bool> sendRaw, LoggingService? log = null)
    {
        _isController = isController;
        _stagingSubDir = isController ? "ClientIn" : "HostIn";
        _sendRaw = sendRaw;
        _log = log;
        _nextTransferSeq = isController ? 1 : 2;

        _pumpThread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = isController ? "OsmiumAuxClientPump" : "OsmiumAuxHostPump"
        };
        _pumpThread.Start();
    }

    public bool HasActiveOutgoingFileTransfer
    {
        get
        {
            uint id = Volatile.Read(ref _activeFileOutId);
            return id != 0 && _outgoing.ContainsKey(id);
        }
    }

    public void SendFeatureConfig(AuxFeatureConfig config)
    {
        if (_disposed) return;
        uint seq;
        lock (_configLock)
        {
            _pendingConfigSeq++;
            if (_pendingConfigSeq == 0) _pendingConfigSeq = 1;
            seq = _pendingConfigSeq;
            _pendingConfig = config;
            _lastConfigSendMs = Environment.TickCount64;
        }

        TransmitFeatureConfigPacket(seq, config);
        _pumpWake.Set();
    }

    public bool SendClipboardText(string text, long maxBytes)
    {
        if (_disposed || text == null) return false;
        byte[] payload = Encoding.UTF8.GetBytes(text);
        if (payload.Length > maxBytes)
        {
            RaiseProgress(new AuxTransferProgress(
                IsOutgoing: true,
                TransferType: AuxTransferType.ClipboardText,
                SummaryName: string.Empty,
                ItemCount: 1,
                TransferredBytes: 0,
                TotalBytes: payload.Length,
                State: AuxTransferState.Rejected,
                RejectReason: AuxTransferRejectReason.TooLarge));
            return false;
        }

        return StartOutgoingMemoryTransfer(AuxTransferType.ClipboardText, payload, 1, "Clipboard");
    }

    public bool SendClipboardImageDib(byte[] dibBytes, long maxCompressedBytes)
    {
        if (_disposed || dibBytes == null || dibBytes.Length < 40) return false;
        byte[] compressed = WindowsClipboardHelper.CompressDib(dibBytes);
        if (compressed.Length > maxCompressedBytes)
        {
            RaiseProgress(new AuxTransferProgress(
                IsOutgoing: true,
                TransferType: AuxTransferType.ClipboardImage,
                SummaryName: string.Empty,
                ItemCount: 1,
                TransferredBytes: 0,
                TotalBytes: compressed.Length,
                State: AuxTransferState.Rejected,
                RejectReason: AuxTransferRejectReason.TooLarge));
            return false;
        }

        return StartOutgoingMemoryTransfer(AuxTransferType.ClipboardImage, compressed, 1, "Image");
    }

    public Task<bool> StartSendFilesAsync(IReadOnlyList<string> sourcePaths, long maxTotalBytes)
    {
        if (_disposed || sourcePaths == null || sourcePaths.Count == 0)
            return Task.FromResult(false);

        return Task.Run(() =>
        {
            if (_disposed) return false;
            try
            {
                var buildResult = BuildFileBatch(sourcePaths, maxTotalBytes);
                if (buildResult.RejectReason != AuxTransferRejectReason.None || buildResult.Stream == null)
                {
                    RaiseProgress(new AuxTransferProgress(
                        IsOutgoing: true,
                        TransferType: AuxTransferType.FileBatch,
                        SummaryName: buildResult.SummaryName,
                        ItemCount: buildResult.ItemCount,
                        TransferredBytes: 0,
                        TotalBytes: buildResult.TotalFileBytes,
                        State: AuxTransferState.Rejected,
                        RejectReason: buildResult.RejectReason));
                    return false;
                }

                StartOutgoingStreamTransfer(
                    AuxTransferType.FileBatch,
                    buildResult.Stream,
                    buildResult.Stream.Length,
                    buildResult.ItemCount,
                    buildResult.SummaryName);
                return true;
            }
            catch (Exception ex)
            {
                _log?.Warn($"StartSendFilesAsync failed: {ex.Message}");
                RaiseProgress(new AuxTransferProgress(
                    IsOutgoing: true,
                    TransferType: AuxTransferType.FileBatch,
                    SummaryName: string.Empty,
                    ItemCount: sourcePaths.Count,
                    TransferredBytes: 0,
                    TotalBytes: 0,
                    State: AuxTransferState.Failed,
                    RejectReason: AuxTransferRejectReason.IoError,
                    ErrorMessage: ex.Message));
                return false;
            }
        });
    }

    public void CancelAllFileTransfers()
    {
        foreach (var kv in _outgoing)
        {
            if (kv.Value.TransferType == AuxTransferType.FileBatch)
            {
                CancelOutgoing(kv.Key, AuxTransferRejectReason.Cancelled, notifyPeer: true);
            }
        }

        foreach (var kv in _incoming)
        {
            if (kv.Value.TransferType == AuxTransferType.FileBatch)
            {
                CancelIncoming(kv.Key, AuxTransferRejectReason.Cancelled, notifyPeer: true);
            }
        }
    }

    public void HandleIncomingPacket(ReadOnlySpan<byte> data)
    {
        if (_disposed || data.Length < 2) return;
        byte kind = data[0];
        switch (kind)
        {
            case KindFeatureConfig:
                HandleFeatureConfigPacket(data);
                break;
            case KindFeatureConfigAck:
                HandleFeatureConfigAckPacket(data);
                break;
            case KindTransferStart:
                HandleTransferStartPacket(data);
                break;
            case KindTransferStartAck:
                HandleTransferStartAckPacket(data);
                break;
            case KindTransferChunk:
                HandleTransferChunkPacket(data);
                break;
            case KindTransferAck:
                HandleTransferAckPacket(data);
                break;
            case KindTransferCancel:
                HandleTransferCancelPacket(data);
                break;
        }
    }

    private uint AllocateTransferId()
    {
        // Odd IDs for controller, even IDs for host (never 0)
        int val = Interlocked.Add(ref _nextTransferSeq, 2);
        return unchecked((uint)val);
    }

    private bool StartOutgoingMemoryTransfer(AuxTransferType type, byte[] data, int itemCount, string summaryName)
    {
        var ms = new MemoryStream(data, writable: false);
        StartOutgoingStreamTransfer(type, ms, data.Length, itemCount, summaryName);
        return true;
    }

    private void StartOutgoingStreamTransfer(
        AuxTransferType type,
        Stream stream,
        long totalBytes,
        int itemCount,
        string summaryName)
    {
        uint id = AllocateTransferId();
        uint totalChunks = totalBytes <= 0 ? 0u : (uint)((totalBytes + ChunkPayloadSize - 1) / ChunkPayloadSize);
        string safeSummary = TruncateUtf8(summaryName ?? string.Empty, 200);

        uint prevIdToCancel = 0;
        lock (_laneLock)
        {
            if (type == AuxTransferType.FileBatch)
            {
                prevIdToCancel = _activeFileOutId;
                _activeFileOutId = id;
            }
            else
            {
                prevIdToCancel = _activeClipboardOutId;
                _activeClipboardOutId = id;
            }
        }

        if (prevIdToCancel != 0)
        {
            CancelOutgoing(prevIdToCancel, AuxTransferRejectReason.Cancelled, notifyPeer: true);
        }

        var transfer = new OutgoingTransfer(id, type, stream, totalBytes, totalChunks, itemCount, safeSummary);
        _outgoing[id] = transfer;

        if (type == AuxTransferType.FileBatch)
        {
            RaiseProgress(new AuxTransferProgress(
                IsOutgoing: true,
                TransferType: type,
                SummaryName: safeSummary,
                ItemCount: itemCount,
                TransferredBytes: 0,
                TotalBytes: totalBytes,
                State: AuxTransferState.Started));
        }

        TransmitTransferStartPacket(transfer);
        _pumpWake.Set();
    }

    private void PumpLoop()
    {
        while (!_disposed && !_disposeCts.IsCancellationRequested)
        {
            bool didWork = false;
            long now = Environment.TickCount64;

            // 1. Retransmit unacked FeatureConfig
            AuxFeatureConfig? cfgToSend = null;
            uint cfgSeq = 0;
            lock (_configLock)
            {
                if (_pendingConfig != null && _ackedConfigSeq != _pendingConfigSeq &&
                    now - _lastConfigSendMs >= 250)
                {
                    cfgToSend = _pendingConfig;
                    cfgSeq = _pendingConfigSeq;
                    _lastConfigSendMs = now;
                }
            }

            if (cfgToSend != null)
            {
                TransmitFeatureConfigPacket(cfgSeq, cfgToSend);
                didWork = true;
            }

            // 2. Drive outgoing transfers
            foreach (var kv in _outgoing)
            {
                var tr = kv.Value;
                if (tr.IsDone) continue;

                lock (tr.Sync)
                {
                    if (tr.IsDone) continue;

                    if (!tr.StartAccepted)
                    {
                        if (now - tr.CreatedMs >= StartTimeoutMs)
                        {
                            FailOutgoingLocked(tr, AuxTransferRejectReason.IoError, "Start ACK timed out");
                            continue;
                        }

                        if (now - tr.LastStartSendMs >= StartRetransmitMs)
                        {
                            tr.LastStartSendMs = now;
                            TransmitTransferStartPacket(tr);
                            didWork = true;
                        }

                        continue;
                    }

                    if (now - tr.LastActivityMs >= IdleTimeoutMs)
                    {
                        FailOutgoingLocked(tr, AuxTransferRejectReason.IoError, "Transfer timed out");
                        continue;
                    }

                    // Send burst of retransmits + new chunks within sliding window
                    int sentInTick = 0;
                    const int maxBurst = 10;

                    uint windowEnd = (uint)Math.Min((long)tr.TotalChunks, (long)tr.BaseSeq + WindowSize);

                    // First retransmit timed-out unacked chunks in window
                    for (uint seq = tr.BaseSeq; seq < tr.NextReadSeq && sentInTick < maxBurst; seq++)
                    {
                        if (tr.InFlight.TryGetValue(seq, out var entry) && !entry.Acked &&
                            now - entry.LastSentMs >= ChunkRetransmitMs)
                        {
                            entry.LastSentMs = now;
                            TransmitTransferChunkPacket(tr.TransferId, seq, entry.Crc32, entry.Payload);
                            sentInTick++;
                            didWork = true;
                        }
                    }

                    // Then read and transmit new chunks up to windowEnd
                    while (tr.NextReadSeq < windowEnd && sentInTick < maxBurst)
                    {
                        uint seq = tr.NextReadSeq;
                        byte[]? chunk = ReadNextChunkFromStream(tr.SourceStream, ChunkPayloadSize);
                        if (chunk == null || chunk.Length == 0)
                        {
                            // Unexpected EOF
                            FailOutgoingLocked(tr, AuxTransferRejectReason.IoError, "Unexpected end of stream");
                            break;
                        }

                        uint crc = ComputeCrc32(chunk);
                        tr.InFlight[seq] = new SentChunkEntry(chunk, crc, now);
                        tr.NextReadSeq++;
                        TransmitTransferChunkPacket(tr.TransferId, seq, crc, chunk);
                        sentInTick++;
                        didWork = true;
                    }
                }
            }

            // 3. Flush delayed ACKs & expire stalled incoming transfers
            foreach (var kv in _incoming)
            {
                var inc = kv.Value;
                bool sendAck = false;
                uint ackBase = 0;
                ulong ackMask = 0;
                bool expired = false;

                lock (inc.Sync)
                {
                    if (inc.IsDone) continue;
                    if (now - inc.LastActivityMs >= IdleTimeoutMs)
                    {
                        expired = true;
                    }
                    else if (inc.AckDirty && now - inc.LastAckSentMs >= 25)
                    {
                        sendAck = true;
                        ackBase = inc.BaseSeq;
                        ackMask = BuildSackMaskLocked(inc);
                        inc.AckDirty = false;
                        inc.ChunksSinceLastAck = 0;
                        inc.LastAckSentMs = now;
                    }
                }

                if (expired)
                {
                    CancelIncoming(inc.TransferId, AuxTransferRejectReason.IoError, notifyPeer: true);
                }
                else if (sendAck)
                {
                    TransmitTransferAckPacket(inc.TransferId, ackBase, ackMask, complete: false);
                    didWork = true;
                }
            }

            // Prune old completed records every ~5s
            foreach (var kv in _recentlyCompletedIncoming)
            {
                if (now - kv.Value.CompletedAtMs > 30000)
                {
                    _recentlyCompletedIncoming.TryRemove(kv.Key, out _);
                }
            }

            if (didWork)
            {
                Thread.Sleep(2);
            }
            else
            {
                _pumpWake.WaitOne(20);
            }
        }
    }

    private void HandleFeatureConfigPacket(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return;
        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(1, 4));
        bool sharedClipboard = data[5] != 0;
        bool fileTransfer = data[6] != 0;
        var direction = (FileTransferDirection)Math.Clamp((int)data[7], 0, 2);
        int maxFileMb = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2)), 1, 8192);
        int maxClipMb = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(10, 2)), 1, 64);

        bool hostAllows = GetHostAllowsFileTransfer?.Invoke() ?? true;
        TransmitFeatureConfigAckPacket(seq, hostAllows);

        if (seq != _lastRecvConfigSeq)
        {
            _lastRecvConfigSeq = seq;
            var cfg = new AuxFeatureConfig(sharedClipboard, fileTransfer, direction, maxFileMb, maxClipMb);
            try
            {
                RemoteFeatureConfigReceived?.Invoke(cfg);
            }
            catch
            {
            }
        }
    }

    private void HandleFeatureConfigAckPacket(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6) return;
        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(1, 4));
        bool hostAllows = data[5] != 0;

        lock (_configLock)
        {
            if (seq == _pendingConfigSeq)
            {
                _ackedConfigSeq = seq;
            }
        }

        try
        {
            HostFileTransferPolicyReceived?.Invoke(hostAllows);
        }
        catch
        {
        }
    }

    private void HandleTransferStartPacket(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24) return;
        uint transferId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(1, 4));
        var type = (AuxTransferType)data[5];
        long totalBytes = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(6, 8));
        uint totalChunks = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(14, 4));
        int itemCount = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(18, 4));
        ushort summaryLen = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(22, 2));
        if (24 + summaryLen > data.Length) return;
        string summaryName = summaryLen > 0 ? Encoding.UTF8.GetString(data.Slice(24, summaryLen)) : string.Empty;

        if (_recentlyCompletedIncoming.TryGetValue(transferId, out var completed))
        {
            TransmitTransferStartAckPacket(transferId, accepted: true, AuxTransferRejectReason.None);
            TransmitTransferAckPacket(transferId, completed.TotalChunks, 0, complete: true);
            return;
        }

        if (_incoming.ContainsKey(transferId))
        {
            TransmitTransferStartAckPacket(transferId, accepted: true, AuxTransferRejectReason.None);
            return;
        }

        if (totalBytes < 0 || itemCount < 0)
        {
            TransmitTransferStartAckPacket(transferId, accepted: false, AuxTransferRejectReason.IoError);
            return;
        }

        uint expectedChunks = totalBytes == 0 ? 0u : (uint)((totalBytes + ChunkPayloadSize - 1) / ChunkPayloadSize);
        if (totalChunks != expectedChunks)
        {
            TransmitTransferStartAckPacket(transferId, accepted: false, AuxTransferRejectReason.IoError);
            return;
        }

        var reject = ValidateIncomingTransfer?.Invoke(type, totalBytes) ?? AuxTransferRejectReason.None;
        if (reject != AuxTransferRejectReason.None)
        {
            TransmitTransferStartAckPacket(transferId, accepted: false, reject);
            if (type == AuxTransferType.FileBatch)
            {
                RaiseProgress(new AuxTransferProgress(
                    IsOutgoing: false,
                    TransferType: type,
                    SummaryName: summaryName,
                    ItemCount: itemCount,
                    TransferredBytes: 0,
                    TotalBytes: totalBytes,
                    State: AuxTransferState.Rejected,
                    RejectReason: reject));
            }

            return;
        }

        IncomingTransfer incoming;
        try
        {
            incoming = new IncomingTransfer(
                transferId, type, totalBytes, totalChunks, itemCount, summaryName, _stagingSubDir);
        }
        catch (Exception ex)
        {
            _log?.Warn($"Create IncomingTransfer failed: {ex.Message}");
            TransmitTransferStartAckPacket(transferId, accepted: false, AuxTransferRejectReason.IoError);
            return;
        }

        if (!_incoming.TryAdd(transferId, incoming))
        {
            incoming.DisposeAndCleanup(deleteStaging: true);
            TransmitTransferStartAckPacket(transferId, accepted: true, AuxTransferRejectReason.None);
            return;
        }

        TransmitTransferStartAckPacket(transferId, accepted: true, AuxTransferRejectReason.None);

        if (type == AuxTransferType.FileBatch)
        {
            RaiseProgress(new AuxTransferProgress(
                IsOutgoing: false,
                TransferType: type,
                SummaryName: summaryName,
                ItemCount: itemCount,
                TransferredBytes: 0,
                TotalBytes: totalBytes,
                State: AuxTransferState.Started));
        }

        if (totalChunks == 0)
        {
            FinalizeIncomingTransfer(incoming);
        }
    }

    private void HandleTransferStartAckPacket(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7) return;
        uint transferId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(1, 4));
        bool accepted = data[5] != 0;
        var reason = (AuxTransferRejectReason)data[6];

        if (!_outgoing.TryGetValue(transferId, out var tr)) return;

        lock (tr.Sync)
        {
            if (tr.IsDone) return;
            tr.LastActivityMs = Environment.TickCount64;
            if (!accepted)
            {
                tr.IsDone = true;
                _outgoing.TryRemove(transferId, out _);
                ClearLaneIdIfMatch(transferId);
                tr.Dispose();

                if (tr.TransferType == AuxTransferType.FileBatch || reason == AuxTransferRejectReason.TooLarge)
                {
                    RaiseProgress(new AuxTransferProgress(
                        IsOutgoing: true,
                        TransferType: tr.TransferType,
                        SummaryName: tr.SummaryName,
                        ItemCount: tr.ItemCount,
                        TransferredBytes: 0,
                        TotalBytes: tr.TotalBytes,
                        State: AuxTransferState.Rejected,
                        RejectReason: reason));
                }

                return;
            }

            tr.StartAccepted = true;
        }

        _pumpWake.Set();
    }

    private void HandleTransferChunkPacket(ReadOnlySpan<byte> data)
    {
        if (data.Length < 14) return;
        uint transferId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(1, 4));
        uint chunkIndex = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(5, 4));
        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(9, 4));
        ReadOnlySpan<byte> payload = data.Slice(13);

        if (ComputeCrc32(payload) != expectedCrc) return;

        if (_recentlyCompletedIncoming.TryGetValue(transferId, out var done))
        {
            TransmitTransferAckPacket(transferId, done.TotalChunks, 0, complete: true);
            return;
        }

        if (!_incoming.TryGetValue(transferId, out var inc)) return;

        bool completedNow = false;
        bool failedIo = false;
        string? ioError = null;
        bool sendAckNow = false;
        uint ackBase = 0;
        ulong ackMask = 0;
        long reportBytes = -1;

        lock (inc.Sync)
        {
            if (inc.IsDone) return;
            long now = Environment.TickCount64;
            inc.LastActivityMs = now;

            if (chunkIndex >= inc.TotalChunks) return;

            if (chunkIndex < inc.BaseSeq)
            {
                inc.AckDirty = true;
                inc.ChunksSinceLastAck++;
            }
            else if (chunkIndex < inc.BaseSeq + WindowSize)
            {
                bool isOutOfOrder = chunkIndex > inc.BaseSeq;
                if (!inc.OutOfOrder.ContainsKey(chunkIndex))
                {
                    inc.OutOfOrder[chunkIndex] = payload.ToArray();
                }

                try
                {
                    while (inc.OutOfOrder.Remove(inc.BaseSeq, out byte[]? nextBytes))
                    {
                        inc.WriteSequential(nextBytes);
                        inc.BaseSeq++;
                        inc.ReceivedBytes += nextBytes.Length;
                    }
                }
                catch (Exception ex)
                {
                    failedIo = true;
                    ioError = ex.Message;
                }

                if (!failedIo)
                {
                    inc.AckDirty = true;
                    inc.ChunksSinceLastAck++;
                    if (inc.BaseSeq >= inc.TotalChunks)
                    {
                        completedNow = true;
                    }
                    else if (isOutOfOrder || inc.ChunksSinceLastAck >= 8 || now - inc.LastAckSentMs >= 25)
                    {
                        sendAckNow = true;
                        ackBase = inc.BaseSeq;
                        ackMask = BuildSackMaskLocked(inc);
                        inc.AckDirty = false;
                        inc.ChunksSinceLastAck = 0;
                        inc.LastAckSentMs = now;
                    }

                    if (inc.TransferType == AuxTransferType.FileBatch &&
                        now - inc.LastProgressReportMs >= 160)
                    {
                        inc.LastProgressReportMs = now;
                        reportBytes = inc.ReceivedBytes;
                    }
                }
            }
        }

        if (failedIo)
        {
            _log?.Warn($"Incoming transfer {transferId} write error: {ioError}");
            CancelIncoming(transferId, AuxTransferRejectReason.IoError, notifyPeer: true);
            return;
        }

        if (completedNow)
        {
            FinalizeIncomingTransfer(inc);
            return;
        }

        if (sendAckNow)
        {
            TransmitTransferAckPacket(transferId, ackBase, ackMask, complete: false);
        }

        if (reportBytes >= 0)
        {
            RaiseProgress(new AuxTransferProgress(
                IsOutgoing: false,
                TransferType: inc.TransferType,
                SummaryName: inc.SummaryName,
                ItemCount: inc.ItemCount,
                TransferredBytes: reportBytes,
                TotalBytes: inc.TotalBytes,
                State: AuxTransferState.Progress));
        }
    }

    private void HandleTransferAckPacket(ReadOnlySpan<byte> data)
    {
        if (data.Length < 18) return;
        uint transferId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(1, 4));
        uint baseSeq = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(5, 4));
        ulong sackMask = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(9, 8));
        bool complete = (data[17] & 0x01) != 0;

        if (!_outgoing.TryGetValue(transferId, out var tr)) return;

        bool finishedNow = false;
        long reportBytes = -1;

        lock (tr.Sync)
        {
            if (tr.IsDone) return;
            long now = Environment.TickCount64;
            tr.LastActivityMs = now;
            tr.StartAccepted = true;

            if (baseSeq > tr.BaseSeq)
            {
                for (uint s = tr.BaseSeq; s < baseSeq; s++)
                {
                    if (tr.InFlight.Remove(s, out var doneEntry))
                    {
                        tr.AckedBytes += doneEntry.Payload.Length;
                    }
                }

                tr.BaseSeq = baseSeq;
            }

            if (sackMask != 0)
            {
                for (int bit = 0; bit < 64; bit++)
                {
                    if ((sackMask & (1UL << bit)) == 0) continue;
                    uint s = baseSeq + 1u + (uint)bit;
                    if (tr.InFlight.TryGetValue(s, out var entry) && !entry.Acked)
                    {
                        entry.Acked = true;
                        tr.AckedBytes += entry.Payload.Length;
                    }
                }
            }

            if (complete || tr.BaseSeq >= tr.TotalChunks)
            {
                tr.IsDone = true;
                finishedNow = true;
                _outgoing.TryRemove(transferId, out _);
                ClearLaneIdIfMatch(transferId);
                tr.Dispose();
            }
            else if (tr.TransferType == AuxTransferType.FileBatch &&
                     now - tr.LastProgressReportMs >= 160)
            {
                tr.LastProgressReportMs = now;
                reportBytes = Math.Min(tr.TotalBytes, tr.AckedBytes);
            }
        }

        if (finishedNow)
        {
            if (tr.TransferType == AuxTransferType.FileBatch)
            {
                RaiseProgress(new AuxTransferProgress(
                    IsOutgoing: true,
                    TransferType: tr.TransferType,
                    SummaryName: tr.SummaryName,
                    ItemCount: tr.ItemCount,
                    TransferredBytes: tr.TotalBytes,
                    TotalBytes: tr.TotalBytes,
                    State: AuxTransferState.Completed));
            }
        }
        else
        {
            if (reportBytes >= 0)
            {
                RaiseProgress(new AuxTransferProgress(
                    IsOutgoing: true,
                    TransferType: tr.TransferType,
                    SummaryName: tr.SummaryName,
                    ItemCount: tr.ItemCount,
                    TransferredBytes: reportBytes,
                    TotalBytes: tr.TotalBytes,
                    State: AuxTransferState.Progress));
            }

            _pumpWake.Set();
        }
    }

    private void HandleTransferCancelPacket(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6) return;
        uint transferId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(1, 4));
        var reason = (AuxTransferRejectReason)data[5];

        if (_outgoing.ContainsKey(transferId))
        {
            CancelOutgoing(transferId, reason, notifyPeer: false);
        }

        if (_incoming.ContainsKey(transferId))
        {
            CancelIncoming(transferId, reason, notifyPeer: false);
        }
    }

    private void FinalizeIncomingTransfer(IncomingTransfer inc)
    {
        byte[]? memBytes = null;
        IReadOnlyList<string>? stagedRoots = null;
        bool ok = false;

        lock (inc.Sync)
        {
            if (inc.IsDone) return;
            inc.IsDone = true;
            _incoming.TryRemove(inc.TransferId, out _);
            try
            {
                if (inc.ReceivedBytes != inc.TotalBytes)
                {
                    throw new InvalidDataException($"Byte count mismatch ({inc.ReceivedBytes} != {inc.TotalBytes})");
                }

                if (inc.TransferType == AuxTransferType.FileBatch)
                {
                    stagedRoots = inc.CompleteFileBatch();
                    ok = stagedRoots != null && stagedRoots.Count > 0;
                }
                else
                {
                    memBytes = inc.CompleteMemory();
                    ok = memBytes != null;
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"FinalizeIncomingTransfer({inc.TransferId}) failed: {ex.Message}");
                inc.DisposeAndCleanup(deleteStaging: true);
                ok = false;
            }
        }

        if (!ok)
        {
            TransmitTransferCancelPacket(inc.TransferId, AuxTransferRejectReason.IoError);
            if (inc.TransferType == AuxTransferType.FileBatch)
            {
                RaiseProgress(new AuxTransferProgress(
                    IsOutgoing: false,
                    TransferType: inc.TransferType,
                    SummaryName: inc.SummaryName,
                    ItemCount: inc.ItemCount,
                    TransferredBytes: inc.ReceivedBytes,
                    TotalBytes: inc.TotalBytes,
                    State: AuxTransferState.Failed,
                    RejectReason: AuxTransferRejectReason.IoError));
            }

            return;
        }

        _recentlyCompletedIncoming[inc.TransferId] = (inc.TotalChunks, Environment.TickCount64);

        // Send completion ACK multiple times so UDP loss won't stall sender
        for (int i = 0; i < 3; i++)
        {
            TransmitTransferAckPacket(inc.TransferId, inc.TotalChunks, 0, complete: true);
        }

        // Dispatch completion off the native recv thread
        _ = Task.Run(() =>
        {
            if (_disposed) return;
            try
            {
                if (inc.TransferType == AuxTransferType.ClipboardText && memBytes != null)
                {
                    string text = Encoding.UTF8.GetString(memBytes);
                    ClipboardTextReceived?.Invoke(text);
                }
                else if (inc.TransferType == AuxTransferType.ClipboardImage && memBytes != null)
                {
                    byte[]? dib = WindowsClipboardHelper.DecompressDib(memBytes, MaxDecompressedImageBytes);
                    if (dib != null && dib.Length >= 40)
                    {
                        ClipboardImageReceived?.Invoke(dib);
                    }
                }
                else if (inc.TransferType == AuxTransferType.FileBatch && stagedRoots != null)
                {
                    FilesReceived?.Invoke(stagedRoots, inc.ItemCount, inc.TotalBytes);
                    RaiseProgress(new AuxTransferProgress(
                        IsOutgoing: false,
                        TransferType: AuxTransferType.FileBatch,
                        SummaryName: inc.SummaryName,
                        ItemCount: inc.ItemCount,
                        TransferredBytes: inc.TotalBytes,
                        TotalBytes: inc.TotalBytes,
                        State: AuxTransferState.Completed));
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"Aux transfer delivery callback error: {ex.Message}");
            }
        });
    }

    private void FailOutgoingLocked(OutgoingTransfer tr, AuxTransferRejectReason reason, string message)
    {
        if (tr.IsDone) return;
        tr.IsDone = true;
        _outgoing.TryRemove(tr.TransferId, out _);
        ClearLaneIdIfMatch(tr.TransferId);
        tr.Dispose();
        TransmitTransferCancelPacket(tr.TransferId, reason);

        if (tr.TransferType == AuxTransferType.FileBatch)
        {
            RaiseProgress(new AuxTransferProgress(
                IsOutgoing: true,
                TransferType: tr.TransferType,
                SummaryName: tr.SummaryName,
                ItemCount: tr.ItemCount,
                TransferredBytes: tr.AckedBytes,
                TotalBytes: tr.TotalBytes,
                State: AuxTransferState.Failed,
                RejectReason: reason,
                ErrorMessage: message));
        }
    }

    private void CancelOutgoing(uint transferId, AuxTransferRejectReason reason, bool notifyPeer)
    {
        if (!_outgoing.TryRemove(transferId, out var tr)) return;
        bool wasActive;
        lock (tr.Sync)
        {
            wasActive = !tr.IsDone;
            tr.IsDone = true;
            tr.Dispose();
        }

        ClearLaneIdIfMatch(transferId);
        if (notifyPeer && wasActive)
        {
            TransmitTransferCancelPacket(transferId, reason);
        }

        if (wasActive && tr.TransferType == AuxTransferType.FileBatch)
        {
            RaiseProgress(new AuxTransferProgress(
                IsOutgoing: true,
                TransferType: tr.TransferType,
                SummaryName: tr.SummaryName,
                ItemCount: tr.ItemCount,
                TransferredBytes: tr.AckedBytes,
                TotalBytes: tr.TotalBytes,
                State: reason == AuxTransferRejectReason.Cancelled ? AuxTransferState.Cancelled : AuxTransferState.Failed,
                RejectReason: reason));
        }
    }

    private void CancelIncoming(uint transferId, AuxTransferRejectReason reason, bool notifyPeer)
    {
        if (!_incoming.TryRemove(transferId, out var inc)) return;
        bool wasActive;
        lock (inc.Sync)
        {
            wasActive = !inc.IsDone;
            inc.IsDone = true;
            inc.DisposeAndCleanup(deleteStaging: true);
        }

        if (notifyPeer && wasActive)
        {
            TransmitTransferCancelPacket(transferId, reason);
        }

        if (wasActive && inc.TransferType == AuxTransferType.FileBatch)
        {
            RaiseProgress(new AuxTransferProgress(
                IsOutgoing: false,
                TransferType: inc.TransferType,
                SummaryName: inc.SummaryName,
                ItemCount: inc.ItemCount,
                TransferredBytes: inc.ReceivedBytes,
                TotalBytes: inc.TotalBytes,
                State: reason == AuxTransferRejectReason.Cancelled ? AuxTransferState.Cancelled : AuxTransferState.Failed,
                RejectReason: reason));
        }
    }

    private void ClearLaneIdIfMatch(uint transferId)
    {
        lock (_laneLock)
        {
            if (_activeClipboardOutId == transferId) _activeClipboardOutId = 0;
            if (_activeFileOutId == transferId) _activeFileOutId = 0;
        }
    }

    private void RaiseProgress(AuxTransferProgress progress)
    {
        try
        {
            TransferProgressChanged?.Invoke(progress);
        }
        catch
        {
        }
    }

    private static ulong BuildSackMaskLocked(IncomingTransfer inc)
    {
        ulong mask = 0;
        for (int bit = 0; bit < 64; bit++)
        {
            if (inc.OutOfOrder.ContainsKey(inc.BaseSeq + 1u + (uint)bit))
            {
                mask |= 1UL << bit;
            }
        }

        return mask;
    }

    private void TransmitFeatureConfigPacket(uint seq, AuxFeatureConfig cfg)
    {
        byte[] buf = new byte[12];
        buf[0] = KindFeatureConfig;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), seq);
        buf[5] = (byte)(cfg.SharedClipboardEnabled ? 1 : 0);
        buf[6] = (byte)(cfg.FileTransferEnabled ? 1 : 0);
        buf[7] = (byte)cfg.FileTransferDirection;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8, 2), (ushort)Math.Clamp(cfg.MaxFileTransferMb, 1, 8192));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10, 2), (ushort)Math.Clamp(cfg.MaxClipboardMb, 1, 64));
        SafeSendRaw(buf, buf.Length);
    }

    private void TransmitFeatureConfigAckPacket(uint seq, bool hostAllowsFileTransfer)
    {
        byte[] buf = new byte[6];
        buf[0] = KindFeatureConfigAck;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), seq);
        buf[5] = (byte)(hostAllowsFileTransfer ? 1 : 0);
        SafeSendRaw(buf, buf.Length);
    }

    private void TransmitTransferStartPacket(OutgoingTransfer tr)
    {
        byte[] summaryBytes = Encoding.UTF8.GetBytes(tr.SummaryName);
        byte[] buf = new byte[24 + summaryBytes.Length];
        buf[0] = KindTransferStart;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), tr.TransferId);
        buf[5] = (byte)tr.TransferType;
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(6, 8), tr.TotalBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(14, 4), tr.TotalChunks);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(18, 4), tr.ItemCount);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(22, 2), (ushort)summaryBytes.Length);
        if (summaryBytes.Length > 0)
        {
            Buffer.BlockCopy(summaryBytes, 0, buf, 24, summaryBytes.Length);
        }

        SafeSendRaw(buf, buf.Length);
    }

    private void TransmitTransferStartAckPacket(uint transferId, bool accepted, AuxTransferRejectReason reason)
    {
        byte[] buf = new byte[7];
        buf[0] = KindTransferStartAck;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), transferId);
        buf[5] = (byte)(accepted ? 1 : 0);
        buf[6] = (byte)reason;
        SafeSendRaw(buf, buf.Length);
    }

    private void TransmitTransferChunkPacket(uint transferId, uint chunkIndex, uint crc32, byte[] payload)
    {
        byte[] buf = new byte[13 + payload.Length];
        buf[0] = KindTransferChunk;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), transferId);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(5, 4), chunkIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(9, 4), crc32);
        Buffer.BlockCopy(payload, 0, buf, 13, payload.Length);
        SafeSendRaw(buf, buf.Length);
    }

    private void TransmitTransferAckPacket(uint transferId, uint baseSeq, ulong sackMask, bool complete)
    {
        byte[] buf = new byte[18];
        buf[0] = KindTransferAck;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), transferId);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(5, 4), baseSeq);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(9, 8), sackMask);
        buf[17] = (byte)(complete ? 1 : 0);
        SafeSendRaw(buf, buf.Length);
    }

    private void TransmitTransferCancelPacket(uint transferId, AuxTransferRejectReason reason)
    {
        byte[] buf = new byte[6];
        buf[0] = KindTransferCancel;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), transferId);
        buf[5] = (byte)reason;
        SafeSendRaw(buf, buf.Length);
    }

    private void SafeSendRaw(byte[] data, int length)
    {
        if (_disposed) return;
        try
        {
            _sendRaw(data, length);
        }
        catch
        {
        }
    }

    private static byte[]? ReadNextChunkFromStream(Stream stream, int maxLen)
    {
        byte[] buf = new byte[maxLen];
        int totalRead = 0;
        while (totalRead < maxLen)
        {
            int n = stream.Read(buf, totalRead, maxLen - totalRead);
            if (n <= 0) break;
            totalRead += n;
        }

        if (totalRead == 0) return null;
        if (totalRead == maxLen) return buf;
        Array.Resize(ref buf, totalRead);
        return buf;
    }

    private static string TruncateUtf8(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
        var sb = new StringBuilder();
        int used = 0;
        foreach (Rune r in text.EnumerateRunes())
        {
            int sz = r.Utf8SequenceLength;
            if (used + sz > maxBytes) break;
            sb.Append(r.ToString());
            used += sz;
        }

        return sb.ToString();
    }

    private static uint[] BuildCrc32Table()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
            }

            table[i] = c;
        }

        return table;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
        {
            crc = Crc32Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _disposeCts.Cancel();
        }
        catch
        {
        }

        _pumpWake.Set();
        if (_pumpThread.IsAlive && Thread.CurrentThread != _pumpThread)
        {
            _pumpThread.Join(500);
        }

        foreach (var kv in _outgoing)
        {
            lock (kv.Value.Sync)
            {
                kv.Value.IsDone = true;
                kv.Value.Dispose();
            }
        }

        _outgoing.Clear();

        foreach (var kv in _incoming)
        {
            lock (kv.Value.Sync)
            {
                kv.Value.IsDone = true;
                kv.Value.DisposeAndCleanup(deleteStaging: true);
            }
        }

        _incoming.Clear();
        _pumpWake.Dispose();
        _disposeCts.Dispose();
    }

    private sealed record FileBatchBuildResult(
        FileBatchReadStream? Stream,
        int ItemCount,
        long TotalFileBytes,
        string SummaryName,
        AuxTransferRejectReason RejectReason);

    private static FileBatchBuildResult BuildFileBatch(IReadOnlyList<string> sourcePaths, long maxTotalBytes)
    {
        var manifest = new FileBatchManifest();
        var fileSources = new List<(string FullPath, long Size)>();
        var usedRootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalFileBytes = 0;
        int topLevelCount = 0;
        string firstName = string.Empty;

        var enumOpts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (string rawPath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) continue;
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(rawPath);
            }
            catch
            {
                continue;
            }

            bool isFile = File.Exists(fullPath);
            bool isDir = !isFile && Directory.Exists(fullPath);
            if (!isFile && !isDir) continue;

            string leaf = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(leaf)) continue;

            string rootName = MakeUniqueName(leaf, usedRootNames);
            manifest.Roots.Add(rootName);
            topLevelCount++;
            if (string.IsNullOrEmpty(firstName)) firstName = rootName;

            if (isFile)
            {
                var fi = new FileInfo(fullPath);
                totalFileBytes += fi.Length;
                if (totalFileBytes > maxTotalBytes)
                {
                    return new FileBatchBuildResult(null, topLevelCount, totalFileBytes, firstName,
                        AuxTransferRejectReason.TooLarge);
                }

                manifest.Items.Add(new FileBatchItem
                {
                    RelPath = rootName,
                    Size = fi.Length,
                    IsDirectory = false
                });
                fileSources.Add((fullPath, fi.Length));
            }
            else
            {
                manifest.Items.Add(new FileBatchItem
                {
                    RelPath = rootName,
                    Size = 0,
                    IsDirectory = true
                });

                var stack = new Stack<(string DirPath, string RelPrefix)>();
                stack.Push((fullPath, rootName));

                while (stack.Count > 0)
                {
                    var (curDir, relPrefix) = stack.Pop();
                    IEnumerable<string> subDirs;
                    IEnumerable<string> files;
                    try
                    {
                        subDirs = Directory.EnumerateDirectories(curDir, "*", enumOpts);
                        files = Directory.EnumerateFiles(curDir, "*", enumOpts);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (string sub in subDirs)
                    {
                        string subLeaf = Path.GetFileName(sub);
                        if (string.IsNullOrEmpty(subLeaf)) continue;
                        string rel = $"{relPrefix}/{subLeaf}";
                        manifest.Items.Add(new FileBatchItem
                        {
                            RelPath = rel,
                            Size = 0,
                            IsDirectory = true
                        });
                        stack.Push((sub, rel));
                    }

                    foreach (string f in files)
                    {
                        string fLeaf = Path.GetFileName(f);
                        if (string.IsNullOrEmpty(fLeaf)) continue;
                        long len;
                        try
                        {
                            len = new FileInfo(f).Length;
                        }
                        catch
                        {
                            continue;
                        }

                        totalFileBytes += len;
                        if (totalFileBytes > maxTotalBytes)
                        {
                            return new FileBatchBuildResult(null, topLevelCount, totalFileBytes, firstName,
                                AuxTransferRejectReason.TooLarge);
                        }

                        manifest.Items.Add(new FileBatchItem
                        {
                            RelPath = $"{relPrefix}/{fLeaf}",
                            Size = len,
                            IsDirectory = false
                        });
                        fileSources.Add((f, len));
                    }
                }
            }
        }

        if (manifest.Items.Count == 0)
        {
            return new FileBatchBuildResult(null, 0, 0, string.Empty, AuxTransferRejectReason.IoError);
        }

        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        if (manifestBytes.Length > MaxManifestBytes)
        {
            return new FileBatchBuildResult(null, topLevelCount, totalFileBytes, firstName,
                AuxTransferRejectReason.TooLarge);
        }

        string summary = topLevelCount <= 1 ? firstName : $"{firstName} (+{topLevelCount - 1})";
        var stream = new FileBatchReadStream(manifestBytes, fileSources, totalFileBytes);
        return new FileBatchBuildResult(stream, topLevelCount, totalFileBytes, summary, AuxTransferRejectReason.None);
    }

    private static string MakeUniqueName(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        for (int i = 2; i < 10000; i++)
        {
            string candidate = $"{stem} ({i}){ext}";
            if (used.Add(candidate)) return candidate;
        }

        string fallback = $"{stem}_{Guid.NewGuid():N}{ext}";
        used.Add(fallback);
        return fallback;
    }

    private sealed class SentChunkEntry
    {
        public byte[] Payload { get; }
        public uint Crc32 { get; }
        public long LastSentMs { get; set; }
        public bool Acked { get; set; }

        public SentChunkEntry(byte[] payload, uint crc32, long lastSentMs)
        {
            Payload = payload;
            Crc32 = crc32;
            LastSentMs = lastSentMs;
        }
    }

    private sealed class OutgoingTransfer : IDisposable
    {
        public object Sync { get; } = new();
        public uint TransferId { get; }
        public AuxTransferType TransferType { get; }
        public Stream SourceStream { get; }
        public long TotalBytes { get; }
        public uint TotalChunks { get; }
        public int ItemCount { get; }
        public string SummaryName { get; }
        public long CreatedMs { get; }
        public long LastStartSendMs { get; set; }
        public long LastActivityMs { get; set; }
        public long LastProgressReportMs { get; set; }
        public bool StartAccepted { get; set; }
        public bool IsDone { get; set; }
        public uint BaseSeq { get; set; }
        public uint NextReadSeq { get; set; }
        public long AckedBytes { get; set; }
        public Dictionary<uint, SentChunkEntry> InFlight { get; } = new();

        public OutgoingTransfer(
            uint transferId,
            AuxTransferType transferType,
            Stream sourceStream,
            long totalBytes,
            uint totalChunks,
            int itemCount,
            string summaryName)
        {
            TransferId = transferId;
            TransferType = transferType;
            SourceStream = sourceStream;
            TotalBytes = totalBytes;
            TotalChunks = totalChunks;
            ItemCount = itemCount;
            SummaryName = summaryName;
            long now = Environment.TickCount64;
            CreatedMs = now;
            LastStartSendMs = now;
            LastActivityMs = now;
            LastProgressReportMs = now;
        }

        public void Dispose()
        {
            InFlight.Clear();
            try
            {
                SourceStream.Dispose();
            }
            catch
            {
            }
        }
    }

    private sealed class IncomingTransfer
    {
        public object Sync { get; } = new();
        public uint TransferId { get; }
        public AuxTransferType TransferType { get; }
        public long TotalBytes { get; }
        public uint TotalChunks { get; }
        public int ItemCount { get; }
        public string SummaryName { get; }
        public long LastActivityMs { get; set; }
        public long LastAckSentMs { get; set; }
        public long LastProgressReportMs { get; set; }
        public bool AckDirty { get; set; }
        public int ChunksSinceLastAck { get; set; }
        public bool IsDone { get; set; }
        public uint BaseSeq { get; set; }
        public long ReceivedBytes { get; set; }
        public Dictionary<uint, byte[]> OutOfOrder { get; } = new();

        private MemoryStream? _memoryDest;
        private FileBatchWriteSink? _fileSink;

        public IncomingTransfer(
            uint transferId,
            AuxTransferType transferType,
            long totalBytes,
            uint totalChunks,
            int itemCount,
            string summaryName,
            string stagingSubDir)
        {
            TransferId = transferId;
            TransferType = transferType;
            TotalBytes = totalBytes;
            TotalChunks = totalChunks;
            ItemCount = itemCount;
            SummaryName = summaryName;
            long now = Environment.TickCount64;
            LastActivityMs = now;
            LastAckSentMs = now;
            LastProgressReportMs = now;

            if (transferType == AuxTransferType.FileBatch)
            {
                string batchDir = Path.Combine(
                    WindowsClipboardHelper.StagingRootDir,
                    stagingSubDir,
                    $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{transferId}_{Guid.NewGuid().ToString("N")[..6]}");
                _fileSink = new FileBatchWriteSink(batchDir, totalBytes);
            }
            else
            {
                _memoryDest = new MemoryStream((int)Math.Min(totalBytes, 1024 * 1024));
            }
        }

        public void WriteSequential(byte[] data)
        {
            if (_fileSink != null)
            {
                _fileSink.Write(data);
            }
            else
            {
                _memoryDest?.Write(data, 0, data.Length);
            }
        }

        public byte[]? CompleteMemory()
        {
            if (_memoryDest == null) return null;
            byte[] result = _memoryDest.ToArray();
            _memoryDest.Dispose();
            _memoryDest = null;
            OutOfOrder.Clear();
            return result;
        }

        public IReadOnlyList<string>? CompleteFileBatch()
        {
            if (_fileSink == null) return null;
            var roots = _fileSink.FinalizeAndGetRoots();
            _fileSink.Dispose();
            _fileSink = null;
            OutOfOrder.Clear();
            return roots;
        }

        public void DisposeAndCleanup(bool deleteStaging)
        {
            OutOfOrder.Clear();
            try
            {
                _memoryDest?.Dispose();
            }
            catch
            {
            }

            _memoryDest = null;

            if (_fileSink != null)
            {
                string dir = _fileSink.BatchRootDir;
                try
                {
                    _fileSink.Dispose();
                }
                catch
                {
                }

                _fileSink = null;
                if (deleteStaging)
                {
                    try
                    {
                        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    private sealed class FileBatchManifest
    {
        [JsonPropertyName("roots")]
        public List<string> Roots { get; set; } = [];

        [JsonPropertyName("items")]
        public List<FileBatchItem> Items { get; set; } = [];
    }

    private sealed class FileBatchItem
    {
        [JsonPropertyName("rel")]
        public string RelPath { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("dir")]
        public bool IsDirectory { get; set; }
    }

    private sealed class FileBatchReadStream : Stream
    {
        private readonly byte[] _headerAndManifest;
        private readonly List<(string FullPath, long Size)> _files;
        private readonly long _totalLength;

        private int _headerPos;
        private int _fileIndex;
        private FileStream? _currentFileStream;
        private long _currentFileRemaining;
        private long _position;

        public FileBatchReadStream(byte[] manifestBytes, List<(string FullPath, long Size)> files, long totalFileBytes)
        {
            _headerAndManifest = new byte[4 + manifestBytes.Length];
            BinaryPrimitives.WriteInt32LittleEndian(_headerAndManifest.AsSpan(0, 4), manifestBytes.Length);
            Buffer.BlockCopy(manifestBytes, 0, _headerAndManifest, 4, manifestBytes.Length);
            _files = files;
            _totalLength = _headerAndManifest.Length + totalFileBytes;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _totalLength;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int written = 0;
            while (written < count)
            {
                if (_headerPos < _headerAndManifest.Length)
                {
                    int take = Math.Min(count - written, _headerAndManifest.Length - _headerPos);
                    Buffer.BlockCopy(_headerAndManifest, _headerPos, buffer, offset + written, take);
                    _headerPos += take;
                    written += take;
                    _position += take;
                    continue;
                }

                if (_currentFileStream == null)
                {
                    while (_fileIndex < _files.Count && _files[_fileIndex].Size == 0)
                    {
                        _fileIndex++;
                    }

                    if (_fileIndex >= _files.Count) break;

                    var (path, size) = _files[_fileIndex];
                    _currentFileStream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 65536);
                    _currentFileRemaining = size;
                }

                int toRead = (int)Math.Min(count - written, _currentFileRemaining);
                int n = _currentFileStream.Read(buffer, offset + written, toRead);
                if (n <= 0)
                {
                    // File shrank on disk while reading: zero-pad remaining declared size so framing stays exact
                    Array.Clear(buffer, offset + written, toRead);
                    n = toRead;
                }

                written += n;
                _position += n;
                _currentFileRemaining -= n;

                if (_currentFileRemaining <= 0)
                {
                    _currentFileStream.Dispose();
                    _currentFileStream = null;
                    _fileIndex++;
                }
            }

            return written;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _currentFileStream?.Dispose();
                _currentFileStream = null;
            }

            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FileBatchWriteSink : IDisposable
    {
        public string BatchRootDir { get; }
        private readonly string _batchRootPrefix;
        private readonly long _expectedTotalBytes;

        private readonly byte[] _lenBuf = new byte[4];
        private int _lenBufPos;
        private byte[]? _manifestBuf;
        private int _manifestPos;
        private bool _manifestParsed;

        private FileBatchManifest? _manifest;
        private readonly List<(string FullPath, long Size)> _nonEmptyFiles = [];
        private int _fileIndex;
        private FileStream? _currentOut;
        private long _currentRemaining;

        public FileBatchWriteSink(string batchRootDir, long expectedTotalBytes)
        {
            BatchRootDir = Path.GetFullPath(batchRootDir);
            _batchRootPrefix = BatchRootDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                               Path.DirectorySeparatorChar;
            _expectedTotalBytes = expectedTotalBytes;
            Directory.CreateDirectory(BatchRootDir);
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                if (!_manifestParsed)
                {
                    if (_lenBufPos < 4)
                    {
                        int take = Math.Min(4 - _lenBufPos, data.Length - offset);
                        data.Slice(offset, take).CopyTo(_lenBuf.AsSpan(_lenBufPos));
                        _lenBufPos += take;
                        offset += take;
                        if (_lenBufPos == 4)
                        {
                            int mLen = BinaryPrimitives.ReadInt32LittleEndian(_lenBuf);
                            if (mLen <= 0 || mLen > MaxManifestBytes || 4L + mLen > _expectedTotalBytes)
                            {
                                throw new InvalidDataException("Invalid file batch manifest length");
                            }

                            _manifestBuf = new byte[mLen];
                        }

                        continue;
                    }

                    if (_manifestBuf != null && _manifestPos < _manifestBuf.Length)
                    {
                        int take = Math.Min(_manifestBuf.Length - _manifestPos, data.Length - offset);
                        data.Slice(offset, take).CopyTo(_manifestBuf.AsSpan(_manifestPos));
                        _manifestPos += take;
                        offset += take;
                        if (_manifestPos == _manifestBuf.Length)
                        {
                            ParseAndPrepareManifest();
                        }

                        continue;
                    }
                }

                if (_currentOut == null)
                {
                    if (_fileIndex >= _nonEmptyFiles.Count)
                    {
                        throw new InvalidDataException("Received excess bytes past end of file batch");
                    }

                    var (destPath, size) = _nonEmptyFiles[_fileIndex];
                    string? parent = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    _currentOut = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
                    _currentRemaining = size;
                }

                int writeCount = (int)Math.Min(data.Length - offset, _currentRemaining);
                _currentOut.Write(data.Slice(offset, writeCount));
                offset += writeCount;
                _currentRemaining -= writeCount;

                if (_currentRemaining <= 0)
                {
                    _currentOut.Flush();
                    _currentOut.Dispose();
                    _currentOut = null;
                    _fileIndex++;
                }
            }
        }

        private void ParseAndPrepareManifest()
        {
            var manifest = JsonSerializer.Deserialize<FileBatchManifest>(_manifestBuf!)
                           ?? throw new InvalidDataException("Empty file batch manifest");
            if (manifest.Items.Count == 0 || manifest.Roots.Count == 0)
            {
                throw new InvalidDataException("Manifest has no items");
            }

            long declaredFileBytes = 0;
            foreach (var item in manifest.Items)
            {
                if (item.Size < 0) throw new InvalidDataException("Negative file size");
                string safeFull = ResolveSafePath(item.RelPath);
                if (item.IsDirectory)
                {
                    Directory.CreateDirectory(safeFull);
                }
                else
                {
                    declaredFileBytes += item.Size;
                    string? parent = Path.GetDirectoryName(safeFull);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    if (item.Size == 0)
                    {
                        using var _ = File.Create(safeFull);
                    }
                    else
                    {
                        _nonEmptyFiles.Add((safeFull, item.Size));
                    }
                }
            }

            long expectedPayload = _expectedTotalBytes - 4L - _manifestBuf!.Length;
            if (declaredFileBytes != expectedPayload)
            {
                throw new InvalidDataException("Declared file size mismatch");
            }

            _manifest = manifest;
            _manifestBuf = null;
            _manifestParsed = true;
        }

        public IReadOnlyList<string> FinalizeAndGetRoots()
        {
            if (!_manifestParsed || _manifest == null)
            {
                throw new InvalidDataException("File batch incomplete");
            }

            if (_currentOut != null || _fileIndex != _nonEmptyFiles.Count)
            {
                throw new InvalidDataException("Not all files finished writing");
            }

            var roots = new List<string>(_manifest.Roots.Count);
            foreach (string r in _manifest.Roots)
            {
                string safeRoot = ResolveSafePath(r);
                if (File.Exists(safeRoot) || Directory.Exists(safeRoot))
                {
                    roots.Add(safeRoot);
                }
            }

            return roots;
        }

        private string ResolveSafePath(string relPath)
        {
            if (string.IsNullOrWhiteSpace(relPath))
                throw new InvalidDataException("Empty relative path");
            if (relPath.Contains(':') || relPath.Contains('\0'))
                throw new InvalidDataException("Forbidden character in path");

            string normalized = relPath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            foreach (string seg in normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (seg == "." || seg == "..")
                    throw new InvalidDataException("Path traversal detected");
            }

            string combined = Path.GetFullPath(Path.Combine(BatchRootDir, normalized));
            if (!combined.StartsWith(_batchRootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Path escaped staging directory");

            return combined;
        }

        public void Dispose()
        {
            try
            {
                _currentOut?.Dispose();
            }
            catch
            {
            }

            _currentOut = null;
        }
    }
}
