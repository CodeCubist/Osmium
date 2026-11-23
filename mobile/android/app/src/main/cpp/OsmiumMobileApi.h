#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(__GNUC__) || defined(__clang__)
#define OSMIUM_MOBILE_API __attribute__((visibility("default")))
#else
#define OSMIUM_MOBILE_API
#endif

typedef struct OsmiumMobileClientConfig
{
    int32_t desiredWidth;
    int32_t desiredHeight;
    int32_t desiredFps;
    int32_t desiredBitrateKbps;
    int32_t codecPreference; // 0=Auto, 1=H264, 2=HEVC, 3=AV1
    int32_t enableHdr;
    int32_t enableYuv444;
    int32_t enableAudio;
    int32_t mouseMode;
} OsmiumMobileClientConfig;

typedef struct OsmiumMobileStats
{
    uint32_t fps;
    uint32_t rttMs;
    uint32_t bitrateKbps;
    uint32_t packetLossPercentX100;
    uint32_t decodeTimeUs;
    uint32_t videoWidth;
    uint32_t videoHeight;
    uint32_t codecId;
    uint32_t colorFlags;
    int32_t transportRoute; // 0=Unknown, 1=LAN, 2=IPv6, 3=P2P, 4=Relay
    uint32_t receivedFrames;
    uint32_t droppedFrames;
    int32_t firstFrameRendered;
    int32_t sessionActive;
} OsmiumMobileStats;

typedef struct OsmiumMobileGamepadState
{
    uint8_t controllerIndex;
    uint8_t connected;
    uint16_t buttonsMask;
    uint8_t leftTrigger;
    uint8_t rightTrigger;
    int16_t leftThumbX;
    int16_t leftThumbY;
    int16_t rightThumbX;
    int16_t rightThumbY;
} OsmiumMobileGamepadState;

// Event types polled safely from Dart via OsmiumMobile_PollEvent
// 1 = FirstFrameRendered (no payload)
// 2 = HostStatusJson (UTF-8 JSON payload)
// 3 = AuxData (binary payload)
// 4 = SessionEnded (UTF-8 reason payload)
// 5 = RelayOutgoingPacket (wire packet to send over WSS relay; flags = isDroppable)
// 6 = HostTextFocus (u8 active, u8 hasRect, u16 serial, f32 x/y/w/h; little-endian)
typedef struct OsmiumMobileEvent
{
    int32_t type;
    int32_t flags;
    int32_t payloadLen;
} OsmiumMobileEvent;

// ---------------------------------------------------------------------------
// Crypto (ECDH P-256 SPKI DER, HKDF-SHA256, AuthTag HMAC-SHA256)
// ---------------------------------------------------------------------------
OSMIUM_MOBILE_API int32_t OsmiumMobile_GenerateKeyPair(
    uint8_t* outPubSpkiDer,
    int32_t* ioPubLen,
    void** outKeyHandle);

OSMIUM_MOBILE_API int32_t OsmiumMobile_DeriveSessionMaterial(
    void* keyHandle,
    const uint8_t* peerPubSpkiDer,
    int32_t peerPubLen,
    const uint8_t* sessionToken16,
    const uint8_t* challengeNonce,
    int32_t nonceLen,
    const char* accessCodeUtf8,
    uint8_t* outAesKey32,
    uint8_t* outAuthTag32);

OSMIUM_MOBILE_API void OsmiumMobile_DestroyKeyPair(void* keyHandle);

// ---------------------------------------------------------------------------
// Transport & Handshake
// ---------------------------------------------------------------------------
OSMIUM_MOBILE_API int32_t OsmiumMobile_CreateTransport(
    const char* stunHost,
    uint16_t stunPort,
    void** outTransport,
    char* outCandidatesJson,
    int32_t jsonCap);

OSMIUM_MOBILE_API int32_t OsmiumMobile_ConnectPeerP2P(
    void* transport,
    const uint8_t* sessionToken16,
    const char* peerCandidatesJson,
    int32_t timeoutMs,
    int32_t* outRoute,
    char* outEndpoint,
    int32_t epCap);

OSMIUM_MOBILE_API int32_t OsmiumMobile_EnableRelay(
    void* transport,
    const uint8_t* sessionToken16,
    const char* label);

OSMIUM_MOBILE_API int32_t OsmiumMobile_PushRelayBatch(
    void* transport,
    const uint8_t* batch,
    int32_t len);

OSMIUM_MOBILE_API int32_t OsmiumMobile_PollRelayOutgoing(
    void* transport,
    uint8_t* outBuf,
    int32_t bufCap,
    int32_t* outIsDroppable);

OSMIUM_MOBILE_API int32_t OsmiumMobile_SetTransportKey(
    void* transport,
    const uint8_t* aesKey32);

OSMIUM_MOBILE_API int32_t OsmiumMobile_SetTransportEncryption(
    void* transport,
    int32_t enabled);

OSMIUM_MOBILE_API int32_t OsmiumMobile_AuthenticateClient(
    void* transport,
    const uint8_t* authTag32,
    const OsmiumMobileClientConfig* profile,
    int32_t timeoutMs,
    char* outErrorMsg,
    int32_t errorCap);

OSMIUM_MOBILE_API void OsmiumMobile_DestroyTransport(void* transport);

// ---------------------------------------------------------------------------
// Streaming Session Lifecycle
// ---------------------------------------------------------------------------
OSMIUM_MOBILE_API int32_t OsmiumMobile_StartSession(
    void* transport,
    int64_t surfaceHandleId,
    const OsmiumMobileClientConfig* cfg,
    void** outSession);

OSMIUM_MOBILE_API void OsmiumMobile_StopSession(void* session);

OSMIUM_MOBILE_API void OsmiumMobile_UpdateSurface(
    void* session,
    int64_t surfaceHandleId);

OSMIUM_MOBILE_API int32_t OsmiumMobile_GetStats(
    void* session,
    OsmiumMobileStats* outStats);

// Returns >0 if event written, 0 if queue empty, -1 if buffer too small (outEvent->payloadLen set)
OSMIUM_MOBILE_API int32_t OsmiumMobile_PollEvent(
    void* session,
    OsmiumMobileEvent* outEvent,
    uint8_t* outPayloadBuf,
    int32_t payloadCap);

// ---------------------------------------------------------------------------
// Hot-path Input & Control
// ---------------------------------------------------------------------------
OSMIUM_MOBILE_API void OsmiumMobile_SendMouseMove(
    void* session,
    int32_t isAbsolute,
    float xOrDx,
    float yOrDy);

OSMIUM_MOBILE_API void OsmiumMobile_SendMouseButton(
    void* session,
    uint8_t button,
    int32_t isDown);

OSMIUM_MOBILE_API void OsmiumMobile_SendMouseWheel(
    void* session,
    int16_t dx,
    int16_t dy);

OSMIUM_MOBILE_API void OsmiumMobile_SendKey(
    void* session,
    uint16_t vk,
    uint16_t scan,
    int32_t isDown,
    int32_t isExtended);

OSMIUM_MOBILE_API void OsmiumMobile_SendUnicodeText(
    void* session,
    const char* utf8Text,
    int32_t len);

OSMIUM_MOBILE_API void OsmiumMobile_SendGamepad(
    void* session,
    const OsmiumMobileGamepadState* state);

OSMIUM_MOBILE_API void OsmiumMobile_SwitchDisplay(
    void* session,
    int32_t displayIndex);

OSMIUM_MOBILE_API void OsmiumMobile_UpdateVideoParams(
    void* session,
    int32_t w,
    int32_t h,
    int32_t fps,
    int32_t bitrateKbps,
    int32_t hdr,
    int32_t yuv444);

OSMIUM_MOBILE_API void OsmiumMobile_SetMouseMode(
    void* session,
    int32_t mouseMode,
    int32_t hideHostCursor);

OSMIUM_MOBILE_API void OsmiumMobile_SetAudioMute(
    void* session,
    int32_t muted);

OSMIUM_MOBILE_API void OsmiumMobile_SetVirtualAudio(
    void* session,
    int32_t virtualSpeaker,
    int32_t virtualMic);

OSMIUM_MOBILE_API void OsmiumMobile_SetMicCaptureEnabled(
    void* session,
    int32_t enabled);

// Asks the host to report which control has keyboard focus (event 6).
OSMIUM_MOBILE_API void OsmiumMobile_SetTextFocusTracking(
    void* session,
    int32_t enabled);

OSMIUM_MOBILE_API void OsmiumMobile_SendSystemAction(
    void* session,
    int32_t action);

OSMIUM_MOBILE_API void OsmiumMobile_RequestIdr(void* session);

OSMIUM_MOBILE_API int32_t OsmiumMobile_SendAuxData(
    void* session,
    const uint8_t* data,
    int32_t len);

#ifdef __cplusplus
}
#endif
