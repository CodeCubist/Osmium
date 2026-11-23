import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:osmium_mobile/app.dart';
import 'package:osmium_mobile/core/models/account_models.dart';
import 'package:osmium_mobile/core/models/device_identity.dart';
import 'package:osmium_mobile/core/models/osmium_settings.dart';
import 'package:osmium_mobile/services/account/account_service.dart';
import 'package:osmium_mobile/services/session/aux_channel.dart';
import 'package:osmium_mobile/services/signaling/signaling_client.dart';
import 'package:osmium_mobile/services/storage/device_wallpaper_store.dart';
import 'package:osmium_mobile/services/storage/secure_storage_service.dart';
import 'package:osmium_mobile/services/storage/settings_repository.dart';
import 'package:osmium_mobile/state/app_controller.dart';

// 1x1 solid-blue PNG (valid image decodable by Flutter's Image widget).
final Uint8List _samplePng = base64Decode(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC',
);

final Uint8List _sampleJpeg = Uint8List.fromList([
  0xFF,
  0xD8,
  0xFF,
  0xE0,
  0x00,
  0x10,
  0x4A,
  0x46,
  0x49,
  0x46,
  0x00,
  0x01,
  0xFF,
  0xD9,
]);

int _crc32(Uint8List data) {
  var crc = 0xFFFFFFFF;
  for (final byte in data) {
    crc ^= byte;
    for (var bit = 0; bit < 8; bit++) {
      crc = (crc & 1) != 0 ? (0xEDB88320 ^ (crc >>> 1)) : (crc >>> 1);
    }
  }
  return (crc ^ 0xFFFFFFFF) & 0xFFFFFFFF;
}

Uint8List _buildStartPacket({
  required int id,
  required int wireType,
  required int totalBytes,
  required int totalChunks,
  String summary = 'wallpaper.jpg',
}) {
  final nameBytes = utf8.encode(summary);
  final buf = Uint8List(24 + nameBytes.length);
  final bd = ByteData.sublistView(buf);
  bd.setUint8(0, 10); // TransferStart
  bd.setUint32(1, id, Endian.little);
  bd.setUint8(5, wireType);
  bd.setInt64(6, totalBytes, Endian.little);
  bd.setUint32(14, totalChunks, Endian.little);
  bd.setInt32(18, 1, Endian.little);
  bd.setUint16(22, nameBytes.length, Endian.little);
  buf.setAll(24, nameBytes);
  return buf;
}

Uint8List _buildChunkPacket({
  required int id,
  required int chunkIndex,
  required Uint8List payload,
}) {
  final buf = Uint8List(13 + payload.length);
  final bd = ByteData.sublistView(buf);
  bd.setUint8(0, 12); // TransferChunk
  bd.setUint32(1, id, Endian.little);
  bd.setUint32(5, chunkIndex, Endian.little);
  bd.setUint32(9, _crc32(payload), Endian.little);
  buf.setAll(13, payload);
  return buf;
}

class _WallpaperTestAppController extends AppController {
  _WallpaperTestAppController({
    required this.devices,
    required this.wallpapers,
  }) : super(
         settingsRepo: SettingsRepository(),
         accountService: AccountService(SecureStorageService()),
         signaling: SignalingClient(),
       );

  final List<AccountDeviceDto> devices;
  final Map<String, String> wallpapers;
  final _unreachable = StreamController<void>.broadcast();

  @override
  Future<void> initialize() async {}

  @override
  Stream<void> get serverUnreachable => _unreachable.stream;

  @override
  bool get isLoggedIn => true;

  @override
  DeviceIdentity get identity =>
      const DeviceIdentity(deviceId: '771234562', deviceName: 'Osmium Phone');

  @override
  OsmiumSettings get settings => const OsmiumSettings();

  @override
  List<AccountDeviceDto> get accountDevices => devices;

  @override
  bool get refreshingDevices => false;

  @override
  SignalingConnectionState get signalingState =>
      SignalingConnectionState.connected;

  @override
  String? wallpaperPathFor(String deviceId) =>
      wallpapers[DeviceIdentity.normalizeDeviceId(deviceId)];

  @override
  void dispose() {
    _unreachable.close();
    super.dispose();
  }
}

void main() {
  group('DeviceWallpaperStore', () {
    late Directory tempDir;

    setUp(() async {
      tempDir = await Directory.systemTemp.createTemp('osmium_wp_test_');
    });

    tearDown(() async {
      if (await tempDir.exists()) {
        await tempDir.delete(recursive: true);
      }
    });

    test('saves, replaces with fresh path, loads, and deletes', () async {
      var clock = 1700000000000;
      final store = DeviceWallpaperStore(
        baseDirectoryProvider: () async => tempDir,
        nowMillis: () => clock,
      );

      final firstPath = await store.saveWallpaper('123 456 789', _sampleJpeg);
      expect(firstPath, isNotNull);
      expect(File(firstPath!).existsSync(), isTrue);
      expect(firstPath.endsWith('.jpg'), isTrue);

      clock += 1000;
      final secondPath = await store.saveWallpaper('123456789', _samplePng);
      expect(secondPath, isNotNull);
      expect(secondPath, isNot(equals(firstPath)));
      expect(File(firstPath).existsSync(), isFalse);
      expect(File(secondPath!).existsSync(), isTrue);
      expect(secondPath.endsWith('.png'), isTrue);

      final loaded = await store.loadAll();
      expect(loaded, {'123456789': secondPath});

      await store.deleteWallpaper('123456789');
      expect(File(secondPath).existsSync(), isFalse);
      expect(await store.loadAll(), isEmpty);
    });

    test('rejects invalid device ids and non-image payloads', () async {
      final store = DeviceWallpaperStore(
        baseDirectoryProvider: () async => tempDir,
      );

      expect(await store.saveWallpaper('12345', _sampleJpeg), isNull);
      expect(await store.saveWallpaper('123456789', Uint8List(0)), isNull);
      expect(
        await store.saveWallpaper(
          '123456789',
          Uint8List.fromList(utf8.encode('not-an-image')),
        ),
        isNull,
      );
      expect(await store.loadAll(), isEmpty);
    });
  });

  group('AuxTransferChannel desktopWallpaper', () {
    test('reassembles multi-chunk wallpaper transfer and emits bytes', () async {
      final sentPackets = <Uint8List>[];
      final channel = AuxTransferChannel(
        sendRaw: (bytes) {
          sentPackets.add(Uint8List.fromList(bytes));
          return true;
        },
      );
      addTearDown(channel.dispose);

      // Build a 2500-byte fake JPEG payload spanning 3 chunks (1160 + 1160 + 180).
      final payload = Uint8List(2500);
      payload.setRange(0, _sampleJpeg.length, _sampleJpeg);
      for (var i = _sampleJpeg.length; i < payload.length; i++) {
        payload[i] = i & 0xFF;
      }

      final receivedFuture = channel.desktopWallpaperStream.first;

      channel.handleIncomingPacket(
        _buildStartPacket(
          id: 42,
          wireType: AuxTransferType.desktopWallpaper.wireValue,
          totalBytes: payload.length,
          totalChunks: 3,
        ),
      );

      expect(sentPackets, hasLength(1));
      expect(sentPackets.first[0], 11); // TransferStartAck
      expect(sentPackets.first[5], 1); // accepted

      channel.handleIncomingPacket(
        _buildChunkPacket(
          id: 42,
          chunkIndex: 0,
          payload: Uint8List.sublistView(payload, 0, 1160),
        ),
      );
      channel.handleIncomingPacket(
        _buildChunkPacket(
          id: 42,
          chunkIndex: 2,
          payload: Uint8List.sublistView(payload, 2320, 2500),
        ),
      );
      channel.handleIncomingPacket(
        _buildChunkPacket(
          id: 42,
          chunkIndex: 1,
          payload: Uint8List.sublistView(payload, 1160, 2320),
        ),
      );

      final received = await receivedFuture;
      expect(received, equals(payload));
      expect(sentPackets.last[0], 13); // TransferAck
      expect(sentPackets.last[17], 1); // complete
    });

    test('rejects oversized desktopWallpaper transfer', () {
      final sentPackets = <Uint8List>[];
      final channel = AuxTransferChannel(
        sendRaw: (bytes) {
          sentPackets.add(Uint8List.fromList(bytes));
          return true;
        },
      );
      addTearDown(channel.dispose);

      channel.handleIncomingPacket(
        _buildStartPacket(
          id: 99,
          wireType: AuxTransferType.desktopWallpaper.wireValue,
          totalBytes: 5 * 1024 * 1024,
          totalChunks: 5000,
        ),
      );

      expect(sentPackets, hasLength(1));
      expect(sentPackets.first[0], 11); // TransferStartAck
      expect(sentPackets.first[5], 0); // rejected
      expect(sentPackets.first[6], 2); // TooLarge
    });
  });

  group('DevicesTab device card wallpaper', () {
    late Directory tempDir;
    late File wallpaperFile;

    setUp(() async {
      tempDir = await Directory.systemTemp.createTemp('osmium_wp_ui_');
      wallpaperFile = File(
        '${tempDir.path}${Platform.pathSeparator}123456789_1000.png',
      );
      wallpaperFile.writeAsBytesSync(_samplePng, flush: true);
    });

    tearDown(() async {
      PaintingBinding.instance.imageCache.clear();
      PaintingBinding.instance.imageCache.clearLiveImages();
      if (await tempDir.exists()) {
        try {
          await tempDir.delete(recursive: true);
        } catch (_) {}
      }
    });

    testWidgets(
      'displays cached desktop wallpaper on connected device card only',
      (tester) async {
        final controller = _WallpaperTestAppController(
          devices: const [
            AccountDeviceDto(
              deviceId: '123456789',
              deviceName: 'Workstation',
              osVersion: 'Windows 11',
              isOnline: true,
            ),
            AccountDeviceDto(
              deviceId: '987654321',
              deviceName: 'Fresh PC',
              osVersion: 'Windows 11',
              isOnline: true,
            ),
          ],
          wallpapers: {'123456789': wallpaperFile.path},
        );

        await tester.pumpWidget(
          ProviderScope(
            overrides: [appControllerProvider.overrideWith((_) => controller)],
            child: const OsmiumApp(),
          ),
        );
        await tester.pump(const Duration(milliseconds: 400));
        await tester.pump(const Duration(milliseconds: 400));

        expect(find.text('Workstation'), findsOneWidget);
        expect(find.text('Fresh PC'), findsOneWidget);

        final imageWidgets = tester
            .widgetList<Image>(find.byType(Image))
            .where((w) => w.image is FileImage)
            .toList();
        expect(imageWidgets, hasLength(1));
        expect(
          (imageWidgets.first.image as FileImage).file.path,
          equals(wallpaperFile.path),
        );

        await tester.pumpWidget(const SizedBox.shrink());
        await tester.pump();
      },
    );
  });
}
