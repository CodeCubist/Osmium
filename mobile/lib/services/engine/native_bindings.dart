import 'dart:ffi';
import 'dart:io';

import 'package:ffi/ffi.dart';

final class OsmiumMobileClientConfigStruct extends Struct {
  @Int32()
  external int desiredWidth;
  @Int32()
  external int desiredHeight;
  @Int32()
  external int desiredFps;
  @Int32()
  external int desiredBitrateKbps;
  @Int32()
  external int codecPreference;
  @Int32()
  external int enableHdr;
  @Int32()
  external int enableYuv444;
  @Int32()
  external int enableAudio;
  @Int32()
  external int mouseMode;
}

final class OsmiumMobileStatsStruct extends Struct {
  @Uint32()
  external int fps;
  @Uint32()
  external int rttMs;
  @Uint32()
  external int bitrateKbps;
  @Uint32()
  external int packetLossPercentX100;
  @Uint32()
  external int decodeTimeUs;
  @Uint32()
  external int videoWidth;
  @Uint32()
  external int videoHeight;
  @Uint32()
  external int codecId;
  @Uint32()
  external int colorFlags;
  @Int32()
  external int transportRoute;
  @Uint32()
  external int receivedFrames;
  @Uint32()
  external int droppedFrames;
  @Int32()
  external int firstFrameRendered;
  @Int32()
  external int sessionActive;
}

final class OsmiumMobileGamepadStateStruct extends Struct {
  @Uint8()
  external int controllerIndex;
  @Uint8()
  external int connected;
  @Uint16()
  external int buttonsMask;
  @Uint8()
  external int leftTrigger;
  @Uint8()
  external int rightTrigger;
  @Int16()
  external int leftThumbX;
  @Int16()
  external int leftThumbY;
  @Int16()
  external int rightThumbX;
  @Int16()
  external int rightThumbY;
}

final class OsmiumMobileEventStruct extends Struct {
  @Int32()
  external int type;
  @Int32()
  external int flags;
  @Int32()
  external int payloadLen;
}

class NativeBindings {
  NativeBindings._(this._lib) {
    generateKeyPair = _lib
        .lookupFunction<
          Int32 Function(
            Pointer<Uint8>,
            Pointer<Int32>,
            Pointer<Pointer<Void>>,
          ),
          int Function(Pointer<Uint8>, Pointer<Int32>, Pointer<Pointer<Void>>)
        >('OsmiumMobile_GenerateKeyPair');

    deriveSessionMaterial = _lib
        .lookupFunction<
          Int32 Function(
            Pointer<Void>,
            Pointer<Uint8>,
            Int32,
            Pointer<Uint8>,
            Pointer<Uint8>,
            Int32,
            Pointer<Utf8>,
            Pointer<Uint8>,
            Pointer<Uint8>,
          ),
          int Function(
            Pointer<Void>,
            Pointer<Uint8>,
            int,
            Pointer<Uint8>,
            Pointer<Uint8>,
            int,
            Pointer<Utf8>,
            Pointer<Uint8>,
            Pointer<Uint8>,
          )
        >('OsmiumMobile_DeriveSessionMaterial');

    destroyKeyPair = _lib
        .lookupFunction<
          Void Function(Pointer<Void>),
          void Function(Pointer<Void>)
        >('OsmiumMobile_DestroyKeyPair');

    createTransport = _lib
        .lookupFunction<
          Int32 Function(
            Pointer<Utf8>,
            Uint16,
            Pointer<Pointer<Void>>,
            Pointer<Utf8>,
            Int32,
          ),
          int Function(
            Pointer<Utf8>,
            int,
            Pointer<Pointer<Void>>,
            Pointer<Utf8>,
            int,
          )
        >('OsmiumMobile_CreateTransport');

    connectPeerP2P = _lib
        .lookupFunction<
          Int32 Function(
            Pointer<Void>,
            Pointer<Uint8>,
            Pointer<Utf8>,
            Int32,
            Pointer<Int32>,
            Pointer<Utf8>,
            Int32,
          ),
          int Function(
            Pointer<Void>,
            Pointer<Uint8>,
            Pointer<Utf8>,
            int,
            Pointer<Int32>,
            Pointer<Utf8>,
            int,
          )
        >('OsmiumMobile_ConnectPeerP2P');

    enableRelay = _lib
        .lookupFunction<
          Int32 Function(Pointer<Void>, Pointer<Uint8>, Pointer<Utf8>),
          int Function(Pointer<Void>, Pointer<Uint8>, Pointer<Utf8>)
        >('OsmiumMobile_EnableRelay');

    pushRelayBatch = _lib
        .lookupFunction<
          Int32 Function(Pointer<Void>, Pointer<Uint8>, Int32),
          int Function(Pointer<Void>, Pointer<Uint8>, int)
        >('OsmiumMobile_PushRelayBatch');

    pollRelayOutgoing = _lib
        .lookupFunction<
          Int32 Function(Pointer<Void>, Pointer<Uint8>, Int32, Pointer<Int32>),
          int Function(Pointer<Void>, Pointer<Uint8>, int, Pointer<Int32>)
        >('OsmiumMobile_PollRelayOutgoing');

    setTransportKey = _lib
        .lookupFunction<
          Int32 Function(Pointer<Void>, Pointer<Uint8>),
          int Function(Pointer<Void>, Pointer<Uint8>)
        >('OsmiumMobile_SetTransportKey');

    setTransportEncryption = _lib
        .lookupFunction<
          Int32 Function(Pointer<Void>, Int32),
          int Function(Pointer<Void>, int)
        >('OsmiumMobile_SetTransportEncryption');

    authenticateClient = _lib
        .lookupFunction<
          Int32 Function(
            Pointer<Void>,
            Pointer<Uint8>,
            Pointer<OsmiumMobileClientConfigStruct>,
            Int32,
            Pointer<Utf8>,
            Int32,
          ),
          int Function(
            Pointer<Void>,
            Pointer<Uint8>,
            Pointer<OsmiumMobileClientConfigStruct>,
            int,
            Pointer<Utf8>,
            int,
          )
        >('OsmiumMobile_AuthenticateClient');

    destroyTransport = _lib
        .lookupFunction<
          Void Function(Pointer<Void>),
          void Function(Pointer<Void>)
        >('OsmiumMobile_DestroyTransport');

    startSession = _lib
        .lookupFunction<
          Int32 Function(
            Pointer<Void>,
            Int64,
            Pointer<OsmiumMobileClientConfigStruct>,
            Pointer<Pointer<Void>>,
          ),
          int Function(
            Pointer<Void>,
            int,
            Pointer<OsmiumMobileClientConfigStruct>,
            Pointer<Pointer<Void>>,
          )
        >('OsmiumMobile_StartSession');

    stopSession = _lib
        .lookupFunction<
          Void Function(Pointer<Void>),
          void Function(Pointer<Void>)
        >('OsmiumMobile_StopSession');

    updateSurface = _lib
        .lookupFunction<
          Void Function(Pointer<Void>, Int64),
          void Function(Pointer<Void>, int)
        >('OsmiumMobile_UpdateSurface');

    getStats = _lib
        .lookupFunction<
          Int32 Function(Pointer<Void>, Pointer<OsmiumMobileStatsStruct>),
          int Function(Pointer<Void>, Pointer<OsmiumMobileStatsStruct>)
        >('OsmiumMobile_GetStats');

    pollEvent = _lib
        .lookupFunction<
          Int32 Function(
            Pointer<Void>,
            Pointer<OsmiumMobileEventStruct>,
            Pointer<Uint8>,
            Int32,
          ),
          int Function(
            Pointer<Void>,
            Pointer<OsmiumMobileEventStruct>,
            Pointer<Uint8>,
            int,
          )
        >('OsmiumMobile_PollEvent');

    sendMouseMove = _lib
        .lookupFunction<
          Void Function(Pointer<Void>, Int32, Float, Float),
          void Function(Pointer<Void>, int, double, double)
        >('OsmiumMobile_SendMouseMove');

    sendMouseButton = _lib
        .lookupFunction<
          Void Function(Pointer<Void>, Uint8, Int32),
          void Function(Pointer<Void>, int, int)
        >('OsmiumMobile_SendMouseButton');

    sendMouseWheel = _lib
        .lookupFunction<
          Void Function(Pointer<Void>, Int16, Int16),
          void Function(Pointer<Void>, int, int)
        >('OsmiumMobile_SendMouseWheel');

    sendKey = _lib
        .lookupFunction<
          Void Function(Pointer<Void>, Uint16, Uint16, Int32, Int32),
          void Function(Pointer<Void>, int, int, int, int)
        >('OsmiumMobile_SendKey');

    sendUnicodeText = _lib
        .lookupFunction<
          Void Function(Pointer<Void>, Pointer<Utf8>, Int32),
          void Function(Pointer<Void>, Pointer<Utf8>, int)
        >('OsmiumMobile_SendUnicodeText');

    sendGamepad = _lib
        .lookupFunction<
          Void Function(Pointer<Void>, Pointer<OsmiumMobileGamepadStateStruct>),
          void Function(Pointer<Void>, Pointer<OsmiumMobileGamepadStateStruct>)
        >('OsmiumMobile_SendGamepad');

    switchDisplay = _lib
        .lookupFunction<
          Void Function(Pointer<Void>, Int32),
          void Function(Pointer<Void>, int)
        >('OsmiumMobile_SwitchDisplay');

    updateVideoParams = _lib
        .lookupFunction<
          Void Function(
            Pointer<Void>,
            Int32,
            Int32,
            Int32,
            Int32,
            Int32,
            Int32,
          ),
          void Function(Pointer<Void>, int, int, int, int, int, int)
        >('OsmiumMobile_UpdateVideoParams');

    setMouseMode = _lib
        .lookupFunction<
          Void Function(Pointer<Void>, Int32, Int32),
          void Function(Pointer<Void>, int, int)
        >('OsmiumMobile_SetMouseMode');

    setAudioMute = _lib
        .lookupFunction<
          Void Function(Pointer<Void>, Int32),
          void Function(Pointer<Void>, int)
        >('OsmiumMobile_SetAudioMute');

    setVirtualAudio = _lib
        .lookupFunction<
          Void Function(Pointer<Void>, Int32, Int32),
          void Function(Pointer<Void>, int, int)
        >('OsmiumMobile_SetVirtualAudio');

    setMicCaptureEnabled = _lib
        .lookupFunction<
          Void Function(Pointer<Void>, Int32),
          void Function(Pointer<Void>, int)
        >('OsmiumMobile_SetMicCaptureEnabled');

    setTextFocusTracking = _lib
        .lookupFunction<
          Void Function(Pointer<Void>, Int32),
          void Function(Pointer<Void>, int)
        >('OsmiumMobile_SetTextFocusTracking');

    sendSystemAction = _lib
        .lookupFunction<
          Void Function(Pointer<Void>, Int32),
          void Function(Pointer<Void>, int)
        >('OsmiumMobile_SendSystemAction');

    requestIdr = _lib
        .lookupFunction<
          Void Function(Pointer<Void>),
          void Function(Pointer<Void>)
        >('OsmiumMobile_RequestIdr');

    sendAuxData = _lib
        .lookupFunction<
          Int32 Function(Pointer<Void>, Pointer<Uint8>, Int32),
          int Function(Pointer<Void>, Pointer<Uint8>, int)
        >('OsmiumMobile_SendAuxData');
  }

  final DynamicLibrary _lib;

  static NativeBindings? _instance;

  static NativeBindings? tryLoad() {
    if (_instance != null) return _instance;
    if (!Platform.isAndroid) return null;
    try {
      final lib = DynamicLibrary.open('libosmium_mobile_engine.so');
      _instance = NativeBindings._(lib);
      return _instance;
    } catch (_) {
      return null;
    }
  }

  late final int Function(
    Pointer<Uint8>,
    Pointer<Int32>,
    Pointer<Pointer<Void>>,
  )
  generateKeyPair;
  late final int Function(
    Pointer<Void>,
    Pointer<Uint8>,
    int,
    Pointer<Uint8>,
    Pointer<Uint8>,
    int,
    Pointer<Utf8>,
    Pointer<Uint8>,
    Pointer<Uint8>,
  )
  deriveSessionMaterial;
  late final void Function(Pointer<Void>) destroyKeyPair;

  late final int Function(
    Pointer<Utf8>,
    int,
    Pointer<Pointer<Void>>,
    Pointer<Utf8>,
    int,
  )
  createTransport;
  late final int Function(
    Pointer<Void>,
    Pointer<Uint8>,
    Pointer<Utf8>,
    int,
    Pointer<Int32>,
    Pointer<Utf8>,
    int,
  )
  connectPeerP2P;
  late final int Function(Pointer<Void>, Pointer<Uint8>, Pointer<Utf8>)
  enableRelay;
  late final int Function(Pointer<Void>, Pointer<Uint8>, int) pushRelayBatch;
  late final int Function(Pointer<Void>, Pointer<Uint8>, int, Pointer<Int32>)
  pollRelayOutgoing;
  late final int Function(Pointer<Void>, Pointer<Uint8>) setTransportKey;
  late final int Function(Pointer<Void>, int) setTransportEncryption;
  late final int Function(
    Pointer<Void>,
    Pointer<Uint8>,
    Pointer<OsmiumMobileClientConfigStruct>,
    int,
    Pointer<Utf8>,
    int,
  )
  authenticateClient;
  late final void Function(Pointer<Void>) destroyTransport;

  late final int Function(
    Pointer<Void>,
    int,
    Pointer<OsmiumMobileClientConfigStruct>,
    Pointer<Pointer<Void>>,
  )
  startSession;
  late final void Function(Pointer<Void>) stopSession;
  late final void Function(Pointer<Void>, int) updateSurface;
  late final int Function(Pointer<Void>, Pointer<OsmiumMobileStatsStruct>)
  getStats;
  late final int Function(
    Pointer<Void>,
    Pointer<OsmiumMobileEventStruct>,
    Pointer<Uint8>,
    int,
  )
  pollEvent;

  late final void Function(Pointer<Void>, int, double, double) sendMouseMove;
  late final void Function(Pointer<Void>, int, int) sendMouseButton;
  late final void Function(Pointer<Void>, int, int) sendMouseWheel;
  late final void Function(Pointer<Void>, int, int, int, int) sendKey;
  late final void Function(Pointer<Void>, Pointer<Utf8>, int) sendUnicodeText;
  late final void Function(
    Pointer<Void>,
    Pointer<OsmiumMobileGamepadStateStruct>,
  )
  sendGamepad;
  late final void Function(Pointer<Void>, int) switchDisplay;
  late final void Function(Pointer<Void>, int, int, int, int, int, int)
  updateVideoParams;
  late final void Function(Pointer<Void>, int, int) setMouseMode;
  late final void Function(Pointer<Void>, int) setAudioMute;
  late final void Function(Pointer<Void>, int, int) setVirtualAudio;
  late final void Function(Pointer<Void>, int) setMicCaptureEnabled;
  late final void Function(Pointer<Void>, int) setTextFocusTracking;
  late final void Function(Pointer<Void>, int) sendSystemAction;
  late final void Function(Pointer<Void>) requestIdr;
  late final int Function(Pointer<Void>, Pointer<Uint8>, int) sendAuxData;
}
