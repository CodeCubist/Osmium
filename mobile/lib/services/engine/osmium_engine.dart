import 'dart:convert';
import 'dart:ffi';
import 'dart:math' as math;
import 'dart:typed_data';

import 'package:ffi/ffi.dart';

import '../../core/models/host_status.dart';
import '../../core/models/osmium_settings.dart';
import 'native_bindings.dart';

class NativeKeyPair {
  NativeKeyPair({required this.handle, required this.publicKeySpkiDer});

  final Pointer<Void> handle;
  final Uint8List publicKeySpkiDer;

  String get publicKeyBase64 => base64Encode(publicKeySpkiDer);
}

class DerivedSessionMaterial {
  const DerivedSessionMaterial({
    required this.aesKey32,
    required this.authTag32,
  });

  final Uint8List aesKey32;
  final Uint8List authTag32;

  String get authTagBase64 => base64Encode(authTag32);
}

class TransportCreationResult {
  const TransportCreationResult({
    required this.handle,
    required this.candidatesJson,
  });

  final Pointer<Void> handle;
  final String candidatesJson;
}

class P2PConnectResult {
  const P2PConnectResult({
    required this.success,
    required this.route,
    required this.endpoint,
  });

  final bool success;
  final int route;
  final String endpoint;
}

class NativeEngineEvent {
  const NativeEngineEvent({
    required this.type,
    required this.flags,
    required this.payload,
  });

  final int type;
  final int flags;
  final Uint8List payload;
}

class OsmiumEngine {
  OsmiumEngine() : _bindings = NativeBindings.tryLoad();

  final NativeBindings? _bindings;

  bool get isAvailable => _bindings != null;

  NativeKeyPair? generateKeyPair() {
    final b = _bindings;
    if (b == null) return null;

    final pubBuf = calloc<Uint8>(256);
    final pubLen = calloc<Int32>()..value = 256;
    final outHandle = calloc<Pointer<Void>>();
    try {
      final ok = b.generateKeyPair(pubBuf, pubLen, outHandle);
      if (ok == 0 || outHandle.value == nullptr) return null;
      final len = pubLen.value;
      final bytes = Uint8List.fromList(pubBuf.asTypedList(len));
      return NativeKeyPair(handle: outHandle.value, publicKeySpkiDer: bytes);
    } finally {
      calloc.free(pubBuf);
      calloc.free(pubLen);
      calloc.free(outHandle);
    }
  }

  DerivedSessionMaterial? deriveSessionMaterial({
    required Pointer<Void> keyHandle,
    required Uint8List peerPubSpkiDer,
    required Uint8List sessionToken16,
    required Uint8List challengeNonce,
    required String accessCode,
  }) {
    final b = _bindings;
    if (b == null || keyHandle == nullptr) return null;

    final peerPtr = calloc<Uint8>(peerPubSpkiDer.length);
    final tokenPtr = calloc<Uint8>(16);
    final noncePtr = calloc<Uint8>(challengeNonce.length);
    final codePtr = accessCode.toNativeUtf8();
    final aesOut = calloc<Uint8>(32);
    final tagOut = calloc<Uint8>(32);

    try {
      peerPtr.asTypedList(peerPubSpkiDer.length).setAll(0, peerPubSpkiDer);
      tokenPtr.asTypedList(16).setAll(0, sessionToken16.take(16));
      noncePtr.asTypedList(challengeNonce.length).setAll(0, challengeNonce);

      final ok = b.deriveSessionMaterial(
        keyHandle,
        peerPtr,
        peerPubSpkiDer.length,
        tokenPtr,
        noncePtr,
        challengeNonce.length,
        codePtr,
        aesOut,
        tagOut,
      );
      if (ok == 0) return null;
      return DerivedSessionMaterial(
        aesKey32: Uint8List.fromList(aesOut.asTypedList(32)),
        authTag32: Uint8List.fromList(tagOut.asTypedList(32)),
      );
    } finally {
      calloc.free(peerPtr);
      calloc.free(tokenPtr);
      calloc.free(noncePtr);
      calloc.free(codePtr);
      calloc.free(aesOut);
      calloc.free(tagOut);
    }
  }

  void destroyKeyPair(Pointer<Void>? handle) {
    if (handle == null || handle == nullptr) return;
    _bindings?.destroyKeyPair(handle);
  }

  TransportCreationResult? createTransport({
    required String stunHost,
    required int stunPort,
  }) {
    final b = _bindings;
    if (b == null) return null;

    final hostPtr = stunHost.toNativeUtf8();
    final outTransport = calloc<Pointer<Void>>();
    final jsonBuf = calloc<Uint8>(16384);
    try {
      final ok = b.createTransport(
        hostPtr,
        stunPort,
        outTransport,
        jsonBuf.cast<Utf8>(),
        16384,
      );
      if (ok == 0 || outTransport.value == nullptr) return null;
      final jsonStr = jsonBuf.cast<Utf8>().toDartString();
      return TransportCreationResult(
        handle: outTransport.value,
        candidatesJson: jsonStr,
      );
    } finally {
      calloc.free(hostPtr);
      calloc.free(outTransport);
      calloc.free(jsonBuf);
    }
  }

  P2PConnectResult connectPeerP2P({
    required Pointer<Void> transport,
    required Uint8List sessionToken16,
    required String peerCandidatesJson,
    int timeoutMs = 4500,
  }) {
    final b = _bindings;
    if (b == null || transport == nullptr) {
      return const P2PConnectResult(success: false, route: 0, endpoint: '');
    }

    final tokenPtr = calloc<Uint8>(16);
    final candPtr = peerCandidatesJson.toNativeUtf8();
    final outRoute = calloc<Int32>();
    final outEp = calloc<Uint8>(256);
    try {
      tokenPtr.asTypedList(16).setAll(0, sessionToken16.take(16));
      final ok = b.connectPeerP2P(
        transport,
        tokenPtr,
        candPtr,
        timeoutMs,
        outRoute,
        outEp.cast<Utf8>(),
        256,
      );
      return P2PConnectResult(
        success: ok != 0,
        route: outRoute.value,
        endpoint: outEp.cast<Utf8>().toDartString(),
      );
    } finally {
      calloc.free(tokenPtr);
      calloc.free(candPtr);
      calloc.free(outRoute);
      calloc.free(outEp);
    }
  }

  bool enableRelay({
    required Pointer<Void> transport,
    required Uint8List sessionToken16,
    String label = 'wss-relay',
  }) {
    final b = _bindings;
    if (b == null || transport == nullptr) return false;
    final tokenPtr = calloc<Uint8>(16);
    final labelPtr = label.toNativeUtf8();
    try {
      tokenPtr.asTypedList(16).setAll(0, sessionToken16.take(16));
      return b.enableRelay(transport, tokenPtr, labelPtr) != 0;
    } finally {
      calloc.free(tokenPtr);
      calloc.free(labelPtr);
    }
  }

  bool pushRelayBatch(Pointer<Void> transport, Uint8List bytes) {
    final b = _bindings;
    if (b == null || transport == nullptr || bytes.isEmpty) return false;
    final ptr = calloc<Uint8>(bytes.length);
    try {
      ptr.asTypedList(bytes.length).setAll(0, bytes);
      return b.pushRelayBatch(transport, ptr, bytes.length) != 0;
    } finally {
      calloc.free(ptr);
    }
  }

  Uint8List? pollRelayOutgoing(Pointer<Void> transport) {
    final b = _bindings;
    if (b == null || transport == nullptr) return null;
    // Native returns -len (without popping) when the head packet is larger
    // than the supplied buffer. Grow once and retry so large clipboard/aux
    // packets cannot permanently stall the relay send queue.
    var cap = 4096;
    for (var attempt = 0; attempt < 2; attempt++) {
      final buf = calloc<Uint8>(cap);
      final droppable = calloc<Int32>();
      try {
        final n = b.pollRelayOutgoing(transport, buf, cap, droppable);
        if (n > 0) {
          return Uint8List.fromList(buf.asTypedList(n));
        }
        if (n == 0) return null;
        final required = -n;
        if (required <= cap) return null;
        cap = required;
      } finally {
        calloc.free(buf);
        calloc.free(droppable);
      }
    }
    return null;
  }

  void configureTransportCrypto({
    required Pointer<Void> transport,
    required Uint8List aesKey32,
    required bool enabled,
  }) {
    final b = _bindings;
    if (b == null || transport == nullptr) return;
    final keyPtr = calloc<Uint8>(32);
    try {
      keyPtr.asTypedList(32).setAll(0, aesKey32.take(32));
      b.setTransportKey(transport, keyPtr);
      b.setTransportEncryption(transport, enabled ? 1 : 0);
    } finally {
      calloc.free(keyPtr);
    }
  }

  void setTransportKcpEnabled({
    required Pointer<Void> transport,
    required bool enabled,
  }) {
    final b = _bindings;
    if (b == null || transport == nullptr) return;
    b.setTransportKcpEnabled(transport, enabled ? 1 : 0);
  }

  /// Returns the native status (1 = accepted, 0 = timed out, -1 = rejected by
  /// host) plus the host-supplied error text. Matches Windows
  /// `Osmium_AuthenticateClient`, which retries over the relay on timeout but
  /// not on an explicit rejection.
  (int, String) authenticateClient({
    required Pointer<Void> transport,
    required Uint8List authTag32,
    required OsmiumSettings settings,
    int timeoutMs = 6000,
  }) {
    final b = _bindings;
    if (b == null || transport == nullptr) return (0, '引擎未就绪');
    final tagPtr = calloc<Uint8>(32);
    final cfgPtr = _allocConfig(settings);
    final errBuf = calloc<Uint8>(512);
    try {
      tagPtr.asTypedList(32).setAll(0, authTag32.take(32));
      final status = b.authenticateClient(
        transport,
        tagPtr,
        cfgPtr,
        timeoutMs,
        errBuf.cast<Utf8>(),
        512,
      );
      return (status, errBuf.cast<Utf8>().toDartString());
    } finally {
      calloc.free(tagPtr);
      calloc.free(cfgPtr);
      calloc.free(errBuf);
    }
  }

  void destroyTransport(Pointer<Void>? transport) {
    if (transport == null || transport == nullptr) return;
    _bindings?.destroyTransport(transport);
  }

  Pointer<Void>? startSession({
    required Pointer<Void> transport,
    required int surfaceHandleId,
    required OsmiumSettings settings,
  }) {
    final b = _bindings;
    if (b == null || transport == nullptr) return null;
    final cfgPtr = _allocConfig(settings);
    final outSession = calloc<Pointer<Void>>();
    try {
      final ok = b.startSession(transport, surfaceHandleId, cfgPtr, outSession);
      if (ok == 0 || outSession.value == nullptr) return null;
      return outSession.value;
    } finally {
      calloc.free(cfgPtr);
      calloc.free(outSession);
    }
  }

  void stopSession(Pointer<Void>? session) {
    if (session == null || session == nullptr) return;
    _bindings?.stopSession(session);
  }

  void updateSurface(Pointer<Void>? session, int surfaceHandleId) {
    if (session == null || session == nullptr) return;
    _bindings?.updateSurface(session, surfaceHandleId);
  }

  SessionStatsSnapshot getStats(Pointer<Void>? session) {
    final b = _bindings;
    if (b == null || session == null || session == nullptr) {
      return const SessionStatsSnapshot();
    }
    final s = calloc<OsmiumMobileStatsStruct>();
    try {
      if (b.getStats(session, s) == 0) return const SessionStatsSnapshot();
      final r = s.ref;
      return SessionStatsSnapshot(
        fps: r.fps,
        rttMs: r.rttMs,
        bitrateKbps: r.bitrateKbps,
        packetLossPercent: r.packetLossPercentX100 / 100.0,
        decodeTimeMs: r.decodeTimeUs / 1000.0,
        videoWidth: r.videoWidth,
        videoHeight: r.videoHeight,
        codecId: r.codecId,
        colorFlags: r.colorFlags,
        transportRoute: r.transportRoute,
        receivedFrames: r.receivedFrames,
        droppedFrames: r.droppedFrames,
        firstFrameRendered: r.firstFrameRendered != 0,
        sessionActive: r.sessionActive != 0,
      );
    } finally {
      calloc.free(s);
    }
  }

  NativeEngineEvent? pollEvent(Pointer<Void>? session) {
    final b = _bindings;
    if (b == null || session == null || session == nullptr) return null;

    final ev = calloc<OsmiumMobileEventStruct>();
    var cap = 4096;
    var buf = calloc<Uint8>(cap);
    try {
      var res = b.pollEvent(session, ev, buf, cap);
      if (res == -1 &&
          ev.ref.payloadLen > cap &&
          ev.ref.payloadLen <= 2 * 1024 * 1024) {
        calloc.free(buf);
        cap = ev.ref.payloadLen;
        buf = calloc<Uint8>(cap);
        res = b.pollEvent(session, ev, buf, cap);
      }
      if (res <= 0) return null;
      final len = ev.ref.payloadLen.clamp(0, cap);
      return NativeEngineEvent(
        type: ev.ref.type,
        flags: ev.ref.flags,
        payload: len > 0
            ? Uint8List.fromList(buf.asTypedList(len))
            : Uint8List(0),
      );
    } finally {
      calloc.free(ev);
      calloc.free(buf);
    }
  }

  void sendMouseMove(
    Pointer<Void>? session, {
    required bool absolute,
    required double x,
    required double y,
  }) {
    if (session == null || session == nullptr) return;
    _bindings?.sendMouseMove(session, absolute ? 1 : 0, x, y);
  }

  void sendMouseButton(
    Pointer<Void>? session, {
    required int button,
    required bool isDown,
  }) {
    if (session == null || session == nullptr) return;
    _bindings?.sendMouseButton(session, button, isDown ? 1 : 0);
  }

  void sendMouseWheel(
    Pointer<Void>? session, {
    required int dx,
    required int dy,
  }) {
    if (session == null || session == nullptr) return;
    _bindings?.sendMouseWheel(session, dx, dy);
  }

  /// Sends one touch frame: every contact on the screen, plus those lifted
  /// since the last frame.
  void sendTouch(
    Pointer<Void>? session, {
    required int seq,
    required List<TouchContact> contacts,
  }) {
    final b = _bindings;
    if (b == null || session == null || session == nullptr) return;
    final count = math.min(contacts.length, TouchContact.maxContacts);
    final buf = _touchBuf ??= calloc<Uint8>(
      TouchContact.maxContacts * TouchContact.byteSize,
    );
    final data = ByteData.sublistView(
      buf.asTypedList(TouchContact.maxContacts * TouchContact.byteSize),
    );
    for (var i = 0; i < count; i++) {
      final c = contacts[i];
      final o = i * TouchContact.byteSize;
      data
        ..setUint8(o, c.id)
        ..setUint8(o + 1, c.phase.index + 1)
        ..setUint16(o + 2, 0, Endian.little)
        ..setFloat32(o + 4, c.x.clamp(0.0, 1.0), Endian.little)
        ..setFloat32(o + 8, c.y.clamp(0.0, 1.0), Endian.little);
    }
    b.sendTouch(session, seq & 0xFFFF, buf, count);
  }

  Pointer<Uint8>? _touchBuf;

  void sendKey(
    Pointer<Void>? session, {
    required int vk,
    required int scan,
    required bool isDown,
    bool isExtended = false,
  }) {
    if (session == null || session == nullptr) return;
    _bindings?.sendKey(session, vk, scan, isDown ? 1 : 0, isExtended ? 1 : 0);
  }

  void sendUnicodeText(Pointer<Void>? session, String text) {
    if (session == null || session == nullptr || text.isEmpty) return;
    final bytes = utf8.encode(text);
    final ptr = calloc<Uint8>(bytes.length + 1);
    try {
      ptr.asTypedList(bytes.length).setAll(0, bytes);
      ptr[bytes.length] = 0;
      _bindings?.sendUnicodeText(session, ptr.cast<Utf8>(), bytes.length);
    } finally {
      calloc.free(ptr);
    }
  }

  void sendGamepad(
    Pointer<Void>? session, {
    required int controllerIndex,
    required bool connected,
    required int buttonsMask,
    required int leftTrigger,
    required int rightTrigger,
    required int leftThumbX,
    required int leftThumbY,
    required int rightThumbX,
    required int rightThumbY,
  }) {
    if (session == null || session == nullptr) return;
    final gp = calloc<OsmiumMobileGamepadStateStruct>();
    try {
      gp.ref
        ..controllerIndex = controllerIndex
        ..connected = connected ? 1 : 0
        ..buttonsMask = buttonsMask
        ..leftTrigger = leftTrigger
        ..rightTrigger = rightTrigger
        ..leftThumbX = leftThumbX
        ..leftThumbY = leftThumbY
        ..rightThumbX = rightThumbX
        ..rightThumbY = rightThumbY;
      _bindings?.sendGamepad(session, gp);
    } finally {
      calloc.free(gp);
    }
  }

  void switchDisplay(Pointer<Void>? session, int displayIndex) {
    if (session == null || session == nullptr) return;
    _bindings?.switchDisplay(session, displayIndex);
  }

  void updateVideoParams(
    Pointer<Void>? session, {
    required int width,
    required int height,
    required int fps,
    required int bitrateKbps,
    required bool hdr,
    required bool yuv444,
  }) {
    if (session == null || session == nullptr) return;
    _bindings?.updateVideoParams(
      session,
      width,
      height,
      fps,
      bitrateKbps,
      hdr ? 1 : 0,
      yuv444 ? 1 : 0,
    );
  }

  void setMouseMode(
    Pointer<Void>? session, {
    required int mouseMode,
    required bool hideHostCursor,
  }) {
    if (session == null || session == nullptr) return;
    _bindings?.setMouseMode(session, mouseMode, hideHostCursor ? 1 : 0);
  }

  void setAudioMute(Pointer<Void>? session, bool muted) {
    if (session == null || session == nullptr) return;
    _bindings?.setAudioMute(session, muted ? 1 : 0);
  }

  void setVirtualAudio(
    Pointer<Void>? session, {
    required bool virtualSpeaker,
    required bool virtualMic,
  }) {
    if (session == null || session == nullptr) return;
    _bindings?.setVirtualAudio(
      session,
      virtualSpeaker ? 1 : 0,
      virtualMic ? 1 : 0,
    );
  }

  void setMicCaptureEnabled(Pointer<Void>? session, bool enabled) {
    if (session == null || session == nullptr) return;
    _bindings?.setMicCaptureEnabled(session, enabled ? 1 : 0);
  }

  void setTextFocusTracking(Pointer<Void>? session, bool enabled) {
    if (session == null || session == nullptr) return;
    _bindings?.setTextFocusTracking(session, enabled ? 1 : 0);
  }

  void sendSystemAction(Pointer<Void>? session, int action) {
    if (session == null || session == nullptr) return;
    _bindings?.sendSystemAction(session, action);
  }

  void requestIdr(Pointer<Void>? session) {
    if (session == null || session == nullptr) return;
    _bindings?.requestIdr(session);
  }

  bool sendAuxData(Pointer<Void>? session, Uint8List data) {
    if (session == null || session == nullptr || data.isEmpty) return false;
    final ptr = calloc<Uint8>(data.length);
    try {
      ptr.asTypedList(data.length).setAll(0, data);
      return (_bindings?.sendAuxData(session, ptr, data.length) ?? 0) != 0;
    } finally {
      calloc.free(ptr);
    }
  }

  Pointer<OsmiumMobileClientConfigStruct> _allocConfig(OsmiumSettings s) {
    final ptr = calloc<OsmiumMobileClientConfigStruct>();
    ptr.ref
      ..desiredWidth = s.desiredWidth
      ..desiredHeight = s.desiredHeight
      ..desiredFps = s.desiredFps
      ..desiredBitrateKbps = s.desiredBitrateKbps
      ..codecPreference = s.codecPref.index
      ..enableHdr = s.enableHdr ? 1 : 0
      ..enableYuv444 = s.enableYuv444 ? 1 : 0
      ..enableAudio = s.enableAudio ? 1 : 0
      ..mouseMode =
          0; // 0 = Absolute (used by both DirectTouch & virtual cursor mapping)
    return ptr;
  }
}
