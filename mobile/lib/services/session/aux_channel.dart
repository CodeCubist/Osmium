import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:path_provider/path_provider.dart';

enum AuxTransferType {
  clipboardText(1),
  clipboardImage(2),
  fileBatch(3),
  desktopWallpaper(4);

  const AuxTransferType(this.wireValue);
  final int wireValue;

  static AuxTransferType? fromWire(int v) => switch (v) {
    1 => AuxTransferType.clipboardText,
    2 => AuxTransferType.clipboardImage,
    3 => AuxTransferType.fileBatch,
    4 => AuxTransferType.desktopWallpaper,
    _ => null,
  };
}

class AuxTransferProgressEvent {
  const AuxTransferProgressEvent({
    required this.isOutgoing,
    required this.type,
    required this.summaryName,
    required this.transferredBytes,
    required this.totalBytes,
    required this.completed,
    this.failed = false,
    this.message = '',
  });

  final bool isOutgoing;
  final AuxTransferType type;
  final String summaryName;
  final int transferredBytes;
  final int totalBytes;
  final bool completed;
  final bool failed;
  final String message;

  double get progress =>
      totalBytes <= 0 ? 0.0 : (transferredBytes / totalBytes).clamp(0.0, 1.0);
}

class _OutgoingTransfer {
  _OutgoingTransfer({
    required this.id,
    required this.type,
    required this.payload,
    required this.itemCount,
    required this.summaryName,
    required this.totalChunks,
  }) : createdMs = DateTime.now().millisecondsSinceEpoch,
       lastActivityMs = DateTime.now().millisecondsSinceEpoch;

  final int id;
  final AuxTransferType type;
  final Uint8List payload;
  final int itemCount;
  final String summaryName;
  final int totalChunks;

  bool startAccepted = false;
  int baseSeq = 0;
  int nextSendSeq = 0;
  int createdMs;
  int lastStartSendMs = 0;
  int lastActivityMs;
  final Map<int, int> inFlightLastSentMs = {};
  final Set<int> acked = {};
}

class _IncomingTransfer {
  _IncomingTransfer({
    required this.id,
    required this.type,
    required this.totalBytes,
    required this.totalChunks,
    required this.itemCount,
    required this.summaryName,
  }) : lastActivityMs = DateTime.now().millisecondsSinceEpoch;

  final int id;
  final AuxTransferType type;
  final int totalBytes;
  final int totalChunks;
  final int itemCount;
  final String summaryName;

  int baseSeq = 0;
  int receivedBytes = 0;
  int lastActivityMs;
  int lastAckSentMs = 0;
  bool ackDirty = false;
  int chunksSinceAck = 0;
  final Map<int, Uint8List> outOfOrder = {};
  final BytesBuilder assembled = BytesBuilder(copy: false);
}

class AuxTransferChannel {
  AuxTransferChannel({
    required bool Function(Uint8List data) sendRaw,
    bool reliableUnderlyingTransport = false,
  }) : _startRetransmitMs = reliableUnderlyingTransport ? 600 : 200,
       _chunkRetransmitMs = reliableUnderlyingTransport ? 600 : 140,
       _maxBurst = reliableUnderlyingTransport ? 20 : 10 {
    _sendRaw = sendRaw;
    _pumpTimer = Timer.periodic(
      const Duration(milliseconds: 20),
      (_) => _pump(),
    );
  }

  static const int _kindFeatureConfig = 1;
  static const int _kindFeatureConfigAck = 2;
  static const int _kindTransferStart = 10;
  static const int _kindTransferStartAck = 11;
  static const int _kindTransferChunk = 12;
  static const int _kindTransferAck = 13;
  static const int _kindTransferCancel = 14;

  static const int _chunkSize = 1160;
  static const int _windowSize = 48;

  static final List<int> _crc32Table = _buildCrc32Table();

  late final bool Function(Uint8List data) _sendRaw;
  final int _startRetransmitMs;
  final int _chunkRetransmitMs;
  final int _maxBurst;
  Timer? _pumpTimer;
  int _nextId = 1;
  int _configSeq = 0;
  bool _disposed = false;

  final Map<int, _OutgoingTransfer> _outgoing = {};
  final Map<int, _IncomingTransfer> _incoming = {};

  final _clipboardTextCtrl = StreamController<String>.broadcast();
  final _filesReceivedCtrl = StreamController<List<String>>.broadcast();
  final _desktopWallpaperCtrl = StreamController<Uint8List>.broadcast();
  final _progressCtrl = StreamController<AuxTransferProgressEvent>.broadcast();

  Stream<String> get clipboardTextStream => _clipboardTextCtrl.stream;
  Stream<List<String>> get filesReceivedStream => _filesReceivedCtrl.stream;
  Stream<Uint8List> get desktopWallpaperStream => _desktopWallpaperCtrl.stream;
  Stream<AuxTransferProgressEvent> get progressStream => _progressCtrl.stream;

  void sendFeatureConfig({
    required bool sharedClipboard,
    required bool fileTransfer,
    int maxFileMb = 512,
    int maxClipMb = 16,
  }) {
    if (_disposed) return;
    _configSeq++;
    final bd = ByteData(12);
    bd.setUint8(0, _kindFeatureConfig);
    bd.setUint32(1, _configSeq, Endian.little);
    bd.setUint8(5, sharedClipboard ? 1 : 0);
    bd.setUint8(6, fileTransfer ? 1 : 0);
    bd.setUint8(7, 2); // Bidirectional
    bd.setUint16(8, maxFileMb.clamp(1, 8192), Endian.little);
    bd.setUint16(10, maxClipMb.clamp(1, 64), Endian.little);
    _sendRaw(bd.buffer.asUint8List());
  }

  void sendClipboardText(String text) {
    if (_disposed || text.isEmpty) return;
    final bytes = Uint8List.fromList(utf8.encode(text));
    _startOutgoing(AuxTransferType.clipboardText, bytes, 1, 'clipboard.txt');
  }

  Future<void> sendFile(File file) async {
    if (_disposed || !await file.exists()) return;
    final raw = await file.readAsBytes();
    final name = file.uri.pathSegments.isNotEmpty
        ? file.uri.pathSegments.last
        : 'file.bin';
    // Pack Osmium single-file manifest format:
    // [4B manifestLen][UTF-8 JSON manifest][file bytes]
    final manifestJson = jsonEncode({
      'entries': [
        {'relativePath': name, 'size': raw.length, 'isDirectory': false},
      ],
    });
    final manifestBytes = utf8.encode(manifestJson);
    final builder = BytesBuilder(copy: false);
    final hdr = ByteData(4)..setUint32(0, manifestBytes.length, Endian.little);
    builder.add(hdr.buffer.asUint8List());
    builder.add(manifestBytes);
    builder.add(raw);
    _startOutgoing(AuxTransferType.fileBatch, builder.takeBytes(), 1, name);
  }

  void _startOutgoing(
    AuxTransferType type,
    Uint8List payload,
    int itemCount,
    String summary,
  ) {
    final id = _nextId;
    _nextId += 2; // Odd IDs for client/controller
    final totalChunks = payload.isEmpty
        ? 1
        : ((payload.length + _chunkSize - 1) ~/ _chunkSize);
    final tr = _OutgoingTransfer(
      id: id,
      type: type,
      payload: payload,
      itemCount: itemCount,
      summaryName: summary,
      totalChunks: totalChunks,
    );
    _outgoing[id] = tr;
    _sendTransferStart(tr);
    tr.lastStartSendMs = DateTime.now().millisecondsSinceEpoch;

    if (type == AuxTransferType.fileBatch) {
      _progressCtrl.add(
        AuxTransferProgressEvent(
          isOutgoing: true,
          type: type,
          summaryName: summary,
          transferredBytes: 0,
          totalBytes: payload.length,
          completed: false,
        ),
      );
    }
  }

  void handleIncomingPacket(Uint8List data) {
    if (_disposed || data.isEmpty) return;
    final kind = data[0];
    switch (kind) {
      case _kindFeatureConfig:
        if (data.length >= 12) {
          final seq = ByteData.sublistView(data).getUint32(1, Endian.little);
          final ack = ByteData(6)
            ..setUint8(0, _kindFeatureConfigAck)
            ..setUint32(1, seq, Endian.little)
            ..setUint8(5, 1);
          _sendRaw(ack.buffer.asUint8List());
        }
        break;
      case _kindFeatureConfigAck:
        break;
      case _kindTransferStart:
        _handleTransferStart(data);
        break;
      case _kindTransferStartAck:
        _handleTransferStartAck(data);
        break;
      case _kindTransferChunk:
        _handleTransferChunk(data);
        break;
      case _kindTransferAck:
        _handleTransferAck(data);
        break;
      case _kindTransferCancel:
        if (data.length >= 6) {
          final id = ByteData.sublistView(data).getUint32(1, Endian.little);
          _outgoing.remove(id);
          _incoming.remove(id);
        }
        break;
    }
  }

  void _handleTransferStart(Uint8List data) {
    if (data.length < 24) return;
    final bd = ByteData.sublistView(data);
    final id = bd.getUint32(1, Endian.little);
    final type = AuxTransferType.fromWire(data[5]);
    final totalBytes = bd.getInt64(6, Endian.little);
    final totalChunks = bd.getUint32(14, Endian.little);
    final itemCount = bd.getInt32(18, Endian.little);
    final summaryLen = bd.getUint16(22, Endian.little);
    if (type == null || 24 + summaryLen > data.length) {
      _sendStartAck(id, false, 4);
      return;
    }
    if (type == AuxTransferType.desktopWallpaper &&
        (totalBytes <= 0 || totalBytes > 4 * 1024 * 1024)) {
      _sendStartAck(id, false, 2);
      return;
    }
    final summary = summaryLen > 0
        ? utf8.decode(data.sublist(24, 24 + summaryLen), allowMalformed: true)
        : '';
    _incoming.putIfAbsent(
      id,
      () => _IncomingTransfer(
        id: id,
        type: type,
        totalBytes: totalBytes,
        totalChunks: totalChunks,
        itemCount: itemCount,
        summaryName: summary,
      ),
    );
    _sendStartAck(id, true, 0);
  }

  void _handleTransferStartAck(Uint8List data) {
    if (data.length < 7) return;
    final bd = ByteData.sublistView(data);
    final id = bd.getUint32(1, Endian.little);
    final accepted = data[5] != 0;
    final tr = _outgoing[id];
    if (tr == null) return;
    tr.lastActivityMs = DateTime.now().millisecondsSinceEpoch;
    if (!accepted) {
      _outgoing.remove(id);
      if (tr.type == AuxTransferType.fileBatch) {
        _progressCtrl.add(
          AuxTransferProgressEvent(
            isOutgoing: true,
            type: tr.type,
            summaryName: tr.summaryName,
            transferredBytes: 0,
            totalBytes: tr.payload.length,
            completed: false,
            failed: true,
            message: '主机拒绝接收',
          ),
        );
      }
      return;
    }
    tr.startAccepted = true;
  }

  void _handleTransferChunk(Uint8List data) {
    if (data.length < 14) return;
    final bd = ByteData.sublistView(data);
    final id = bd.getUint32(1, Endian.little);
    final chunkIndex = bd.getUint32(5, Endian.little);
    final expectedCrc = bd.getUint32(9, Endian.little);
    final payload = Uint8List.sublistView(data, 13);
    if (_crc32(payload) != expectedCrc) return;

    final inc = _incoming[id];
    if (inc == null) return;
    final now = DateTime.now().millisecondsSinceEpoch;
    inc.lastActivityMs = now;

    if (chunkIndex >= inc.baseSeq && chunkIndex < inc.baseSeq + _windowSize) {
      inc.outOfOrder.putIfAbsent(chunkIndex, () => Uint8List.fromList(payload));
      while (inc.outOfOrder.containsKey(inc.baseSeq)) {
        final next = inc.outOfOrder.remove(inc.baseSeq)!;
        inc.assembled.add(next);
        inc.baseSeq++;
        inc.receivedBytes += next.length;
      }
    }

    inc.ackDirty = true;
    inc.chunksSinceAck++;

    if (inc.baseSeq >= inc.totalChunks) {
      _sendTransferAck(id, inc.totalChunks, 0, complete: true);
      _incoming.remove(id);
      _finalizeIncoming(inc);
    } else if (inc.chunksSinceAck >= 6 || now - inc.lastAckSentMs >= 25) {
      _sendTransferAck(id, inc.baseSeq, _buildSackMask(inc), complete: false);
      inc.ackDirty = false;
      inc.chunksSinceAck = 0;
      inc.lastAckSentMs = now;
    }
  }

  void _handleTransferAck(Uint8List data) {
    if (data.length < 18) return;
    final bd = ByteData.sublistView(data);
    final id = bd.getUint32(1, Endian.little);
    final baseSeq = bd.getUint32(5, Endian.little);
    final sackLow = bd.getUint32(9, Endian.little);
    final sackHigh = bd.getUint32(13, Endian.little);
    final complete = (data[17] & 0x01) != 0;

    final tr = _outgoing[id];
    if (tr == null) return;
    tr.lastActivityMs = DateTime.now().millisecondsSinceEpoch;
    tr.startAccepted = true;

    if (baseSeq > tr.baseSeq) {
      for (var seq = tr.baseSeq; seq < baseSeq; seq++) {
        tr.inFlightLastSentMs.remove(seq);
        tr.acked.remove(seq);
      }
      tr.baseSeq = baseSeq;
    }

    for (var bit = 0; bit < 32; bit++) {
      if ((sackLow & (1 << bit)) != 0) tr.acked.add(baseSeq + bit);
      if ((sackHigh & (1 << bit)) != 0) tr.acked.add(baseSeq + 32 + bit);
    }

    if (complete || tr.baseSeq >= tr.totalChunks) {
      _outgoing.remove(id);
      if (tr.type == AuxTransferType.fileBatch) {
        _progressCtrl.add(
          AuxTransferProgressEvent(
            isOutgoing: true,
            type: tr.type,
            summaryName: tr.summaryName,
            transferredBytes: tr.payload.length,
            totalBytes: tr.payload.length,
            completed: true,
          ),
        );
      }
    } else if (tr.type == AuxTransferType.fileBatch) {
      _progressCtrl.add(
        AuxTransferProgressEvent(
          isOutgoing: true,
          type: tr.type,
          summaryName: tr.summaryName,
          transferredBytes: (tr.baseSeq * _chunkSize).clamp(
            0,
            tr.payload.length,
          ),
          totalBytes: tr.payload.length,
          completed: false,
        ),
      );
    }
  }

  Future<void> _finalizeIncoming(_IncomingTransfer inc) async {
    final bytes = inc.assembled.takeBytes();
    if (inc.type == AuxTransferType.clipboardText) {
      final text = utf8.decode(bytes, allowMalformed: true);
      if (text.isNotEmpty) {
        _clipboardTextCtrl.add(text);
      }
    } else if (inc.type == AuxTransferType.desktopWallpaper) {
      if (bytes.isNotEmpty && bytes.length == inc.totalBytes) {
        _desktopWallpaperCtrl.add(bytes);
      }
    } else if (inc.type == AuxTransferType.fileBatch && bytes.length >= 4) {
      try {
        final manifestLen = ByteData.sublistView(bytes)
            .getUint32(0, Endian.little);
        if (4 + manifestLen <= bytes.length) {
          final manifest = jsonDecode(
            utf8.decode(bytes.sublist(4, 4 + manifestLen)),
          ) as Map<String, dynamic>;
          final entries = (manifest['entries'] as List<dynamic>? ?? const []);
          final dir = await getApplicationDocumentsDirectory();
          final outDir = Directory('${dir.path}/OsmiumReceived');
          if (!await outDir.exists()) await outDir.create(recursive: true);

          var offset = 4 + manifestLen;
          final savedPaths = <String>[];
          for (final item in entries.whereType<Map<String, dynamic>>()) {
            final rel = (item['relativePath'] ?? 'file.bin')
                .toString()
                .replaceAll('\\', '/');
            final safeName = rel.split('/').last;
            final size = (item['size'] as num?)?.toInt() ?? 0;
            final isDir = item['isDirectory'] == true;
            if (isDir || offset + size > bytes.length) continue;
            final outFile = File('${outDir.path}/$safeName');
            await outFile.writeAsBytes(bytes.sublist(offset, offset + size));
            savedPaths.add(outFile.path);
            offset += size;
          }
          if (savedPaths.isNotEmpty) {
            _filesReceivedCtrl.add(savedPaths);
            _progressCtrl.add(
              AuxTransferProgressEvent(
                isOutgoing: false,
                type: AuxTransferType.fileBatch,
                summaryName: inc.summaryName,
                transferredBytes: inc.totalBytes,
                totalBytes: inc.totalBytes,
                completed: true,
              ),
            );
          }
        }
      } catch (_) {}
    }
  }

  void _pump() {
    if (_disposed) return;
    final now = DateTime.now().millisecondsSinceEpoch;

    final expiredOut = <int>[];
    for (final tr in _outgoing.values) {
      if (!tr.startAccepted) {
        if (now - tr.createdMs >= 8000) {
          expiredOut.add(tr.id);
        } else if (now - tr.lastStartSendMs >= _startRetransmitMs) {
          tr.lastStartSendMs = now;
          _sendTransferStart(tr);
        }
        continue;
      }

      if (now - tr.lastActivityMs >= 18000) {
        expiredOut.add(tr.id);
        continue;
      }

      var burst = 0;
      final winEnd = (tr.baseSeq + _windowSize).clamp(0, tr.totalChunks);

      for (var seq = tr.baseSeq; seq < tr.nextSendSeq && burst < _maxBurst; seq++) {
        if (tr.acked.contains(seq)) continue;
        final last = tr.inFlightLastSentMs[seq] ?? 0;
        if (now - last >= _chunkRetransmitMs) {
          tr.inFlightLastSentMs[seq] = now;
          _sendTransferChunk(tr, seq);
          burst++;
        }
      }

      while (tr.nextSendSeq < winEnd && burst < _maxBurst) {
        final seq = tr.nextSendSeq++;
        tr.inFlightLastSentMs[seq] = now;
        _sendTransferChunk(tr, seq);
        burst++;
      }
    }
    for (final id in expiredOut) {
      _outgoing.remove(id);
    }

    for (final inc in _incoming.values) {
      if (inc.ackDirty && now - inc.lastAckSentMs >= 30) {
        _sendTransferAck(
          inc.id,
          inc.baseSeq,
          _buildSackMask(inc),
          complete: false,
        );
        inc.ackDirty = false;
        inc.chunksSinceAck = 0;
        inc.lastAckSentMs = now;
      }
    }
  }

  void _sendTransferStart(_OutgoingTransfer tr) {
    final nameBytes = utf8.encode(tr.summaryName);
    final buf = Uint8List(24 + nameBytes.length);
    final bd = ByteData.sublistView(buf);
    bd.setUint8(0, _kindTransferStart);
    bd.setUint32(1, tr.id, Endian.little);
    bd.setUint8(5, tr.type.wireValue);
    bd.setInt64(6, tr.payload.length, Endian.little);
    bd.setUint32(14, tr.totalChunks, Endian.little);
    bd.setInt32(18, tr.itemCount, Endian.little);
    bd.setUint16(22, nameBytes.length, Endian.little);
    buf.setAll(24, nameBytes);
    _sendRaw(buf);
  }

  void _sendStartAck(int id, bool accepted, int reason) {
    final bd = ByteData(7)
      ..setUint8(0, _kindTransferStartAck)
      ..setUint32(1, id, Endian.little)
      ..setUint8(5, accepted ? 1 : 0)
      ..setUint8(6, reason);
    _sendRaw(bd.buffer.asUint8List());
  }

  void _sendTransferChunk(_OutgoingTransfer tr, int seq) {
    final start = seq * _chunkSize;
    final end = (start + _chunkSize).clamp(0, tr.payload.length);
    final slice = Uint8List.sublistView(tr.payload, start, end);
    final crc = _crc32(slice);
    final buf = Uint8List(13 + slice.length);
    final bd = ByteData.sublistView(buf);
    bd.setUint8(0, _kindTransferChunk);
    bd.setUint32(1, tr.id, Endian.little);
    bd.setUint32(5, seq, Endian.little);
    bd.setUint32(9, crc, Endian.little);
    buf.setAll(13, slice);
    _sendRaw(buf);
  }

  int _buildSackMask(_IncomingTransfer inc) {
    var mask = 0;
    for (final seq in inc.outOfOrder.keys) {
      final bit = seq - inc.baseSeq;
      if (bit >= 0 && bit < 53) {
        mask |= (1 << bit);
      }
    }
    return mask;
  }

  void _sendTransferAck(
    int id,
    int baseSeq,
    int sackMask, {
    required bool complete,
  }) {
    final bd = ByteData(18)
      ..setUint8(0, _kindTransferAck)
      ..setUint32(1, id, Endian.little)
      ..setUint32(5, baseSeq, Endian.little)
      ..setUint32(9, sackMask & 0xFFFFFFFF, Endian.little)
      ..setUint32(13, (sackMask >> 32) & 0xFFFFFFFF, Endian.little)
      ..setUint8(17, complete ? 1 : 0);
    _sendRaw(bd.buffer.asUint8List());
  }

  static List<int> _buildCrc32Table() {
    final table = List<int>.filled(256, 0);
    for (var i = 0; i < 256; i++) {
      var c = i;
      for (var j = 0; j < 8; j++) {
        c = (c & 1) != 0 ? (0xEDB88320 ^ (c >>> 1)) : (c >>> 1);
      }
      table[i] = c & 0xFFFFFFFF;
    }
    return table;
  }

  static int _crc32(Uint8List data) {
    var crc = 0xFFFFFFFF;
    for (var i = 0; i < data.length; i++) {
      crc = _crc32Table[(crc ^ data[i]) & 0xFF] ^ (crc >>> 8);
    }
    return (crc ^ 0xFFFFFFFF) & 0xFFFFFFFF;
  }

  void dispose() {
    _disposed = true;
    _pumpTimer?.cancel();
    _outgoing.clear();
    _incoming.clear();
    _clipboardTextCtrl.close();
    _filesReceivedCtrl.close();
    _desktopWallpaperCtrl.close();
    _progressCtrl.close();
  }
}
