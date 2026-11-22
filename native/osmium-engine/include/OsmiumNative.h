#pragma once

#include <stdint.h>

#ifdef OSMIUM_NATIVE_EXPORTS
#define OSMIUM_API extern "C" __declspec(dllexport)
#else
#define OSMIUM_API extern "C" __declspec(dllimport)
#endif

#pragma pack(push, 8)

typedef struct OsmiumCapabilities
{
    int32_t hasHardwareDecoder;
    int32_t hasHardwareEncoder;
    int32_t decoderSupportsH264;
    int32_t decoderSupportsHevc;
    int32_t decoderSupportsAv1;
    int32_t encoderSupportsH264;
    int32_t encoderSupportsHevc;
    int32_t encoderSupportsAv1;
    int32_t supportsHdr;
    int32_t maxWidth;
    int32_t maxHeight;
    char preferredDecoderName[64];
    char preferredEncoderName[64];
} OsmiumCapabilities;

typedef struct OsmiumDisplayInfo
{
    int32_t index;
    int32_t width;
    int32_t height;
    int32_t refreshRate;
    int32_t left;
    int32_t top;
    int32_t isPrimary;
    char name[128];
    char deviceName[128];
} OsmiumDisplayInfo;

typedef struct OsmiumAudioDeviceInfo
{
    int32_t index;
    int32_t isDefault;
    char id[256];
    char name[256];
} OsmiumAudioDeviceInfo;

typedef struct OsmiumGamepadInfo
{
    int32_t joystickIndex;
    int32_t instanceId;
    int32_t isGameController;
    int32_t buttonCount;
    int32_t axisCount;
    int32_t hatCount;
    char name[128];
    char guid[64];
} OsmiumGamepadInfo;

typedef struct OsmiumGamepadLiveState
{
    int32_t connected;
    int32_t buttonsMask;
    int16_t axes[8];
    uint8_t hats[4];
    int32_t lastPressedButton;
    int32_t lastMovedAxis;
    int32_t lastMovedAxisDirection;
} OsmiumGamepadLiveState;

typedef struct OsmiumClientStats
{
    uint32_t receivedFrames;
    uint32_t decodedFrames;
    uint32_t renderedFrames;
    uint32_t droppedFrames;
    double fps;
    double bitrateMbps;
    double decodeLatencyMs;
    uint32_t rttMs;
    int32_t videoWidth;
    int32_t videoHeight;
    int32_t nativeWidth;
    int32_t nativeHeight;
    int32_t transportRoute;
    int32_t localPort;
    double packetLossPercent;
    uint64_t receivedPackets;
    uint64_t lostPackets;
    int32_t activeDisplayIndex;
    int32_t mouseMode;
    int32_t virtualMouseEngaged;
    int32_t enableHdr;
    int32_t enableYuv444;
    char codecName[32];
    char decoderName[64];
    char peerEndpoint[64];
    char localEndpoint[64];
} OsmiumClientStats;

typedef struct OsmiumHostStats
{
    uint32_t encodedFrames;
    uint32_t droppedFrames;
    double fps;
    double bitrateMbps;
    double encodeLatencyMs;
    uint32_t rttMs;
    int32_t captureWidth;
    int32_t captureHeight;
    int32_t transportRoute;
    char codecName[32];
    char encoderName[64];
    char peerEndpoint[64];
} OsmiumHostStats;

typedef struct OsmiumHostConfig
{
    int32_t displayIndex;
    int32_t maxWidth;
    int32_t maxHeight;
    int32_t maxFps;
    int32_t maxBitrateKbps;
    int32_t codecPreference;
    int32_t encoderPreference;
    int32_t captureAudio;
    int32_t allowInputControl;
    int32_t captureCursor;
    int32_t allowRemoteDriverMgmt;
    int32_t requireLocalDriverConfirm;
} OsmiumHostConfig;

typedef struct OsmiumClientConfig
{
    const char* windowTitle;
    int32_t targetWidth;
    int32_t targetHeight;
    int32_t fps;
    int32_t bitrateKbps;
    int32_t windowMode;
    int32_t codecPreference;
    int32_t decoderPreference;
    int32_t captureSysKeysMode;
    int32_t enableVsync;
    int32_t enableHdr;
    int32_t enableYuv444;
    int32_t showPerformanceOverlay;
    int32_t absoluteMouseMode;
    int32_t mouseMode;
    int32_t absoluteTouchMode;
    int32_t swapMouseButtons;
    int32_t reverseScrollDirection;
    int32_t multiController;
    int32_t swapFaceButtons;
    int32_t gamepadMouse;
    int32_t backgroundGamepad;
    int32_t muteOnFocusLoss;
    int32_t keepAwake;
    int32_t enableVirtualSpeaker;
    int32_t enableVirtualMic;
    const char* localMicDeviceId;
} OsmiumClientConfig;

typedef void (*OsmiumLogCallback)(const char* message);
typedef void (*OsmiumClientStartedCallback)(int32_t width, int32_t height, const char* codecName,
                                            const char* decoderName);
typedef void (*OsmiumClientStatsCallback)(const OsmiumClientStats* stats);
typedef void (*OsmiumClientEndedCallback)(int32_t reasonCode, const char* reasonMessage);
typedef void (*OsmiumClientStatusJsonCallback)(const char* statusJson);
typedef void (*OsmiumHostStatsCallback)(const OsmiumHostStats* stats);
typedef void (*OsmiumHostEndedCallback)(int32_t reasonCode, const char* reasonMessage);
typedef int32_t (*OsmiumHostDriverConfirmCallback)(int32_t actionType);
typedef void (*OsmiumAuxDataCallback)(const uint8_t* data, int32_t length);
typedef int32_t (*OsmiumRelaySendCallback)(const uint8_t* data, int32_t length, int32_t isDroppable);

typedef struct OsmiumClientCallbacks
{
    OsmiumClientStartedCallback onStarted;
    OsmiumClientStatsCallback onStatsUpdated;
    OsmiumClientEndedCallback onEnded;
    OsmiumLogCallback onLog;
    OsmiumClientStatusJsonCallback onStatusJson;
    OsmiumAuxDataCallback onAuxData;
} OsmiumClientCallbacks;

typedef struct OsmiumHostCallbacks
{
    OsmiumHostStatsCallback onStatsUpdated;
    OsmiumHostEndedCallback onEnded;
    OsmiumLogCallback onLog;
    OsmiumHostDriverConfirmCallback onConfirmDriverAction;
    OsmiumAuxDataCallback onAuxData;
} OsmiumHostCallbacks;

#pragma pack(pop)

OSMIUM_API int32_t Osmium_Init(const char* gameControllerDbPath, OsmiumLogCallback logCallback);
OSMIUM_API void Osmium_Shutdown(void);

OSMIUM_API int32_t Osmium_ProbeCapabilities(OsmiumCapabilities* outCaps);
OSMIUM_API int32_t Osmium_EnumDisplays(OsmiumDisplayInfo* outDisplays, int32_t maxCount);
OSMIUM_API int32_t Osmium_EnumMicDevices(OsmiumAudioDeviceInfo* outDevices, int32_t maxCount);
OSMIUM_API int32_t Osmium_GetDriverStatus(int32_t* outHasUsbIp, int32_t* outIsElevated, int32_t* outIsSystem);
OSMIUM_API int32_t Osmium_InstallVirtualDrivers(void);
OSMIUM_API int32_t Osmium_UninstallVirtualDrivers(void);
OSMIUM_API int32_t Osmium_RunSystemService(void);
OSMIUM_API int32_t Osmium_RunOneShotSasService(void);
OSMIUM_API int32_t Osmium_RunInputAgent(void);
OSMIUM_API int32_t Osmium_InstallSystemService(void);
OSMIUM_API int32_t Osmium_UninstallSystemService(void);
OSMIUM_API int32_t Osmium_IsSystemServiceRunning(void);

OSMIUM_API int32_t Osmium_GetConnectedGamepads(OsmiumGamepadInfo* outGamepads, int32_t maxCount);
OSMIUM_API int32_t Osmium_PollGamepadState(int32_t joystickIndex, OsmiumGamepadLiveState* outState);
OSMIUM_API int32_t Osmium_AddGamepadMapping(const char* mappingString);

OSMIUM_API int32_t Osmium_CreateTransport(
    const char* stunHost,
    uint16_t stunPort,
    void** outTransport,
    char* outLocalCandidatesJson,
    int32_t jsonBufLen);

OSMIUM_API void Osmium_DestroyTransport(void* transport);

OSMIUM_API int32_t Osmium_ConnectPeerP2P(
    void* transport,
    const uint8_t* sessionToken16,
    int32_t localRole,
    const char* peerCandidatesJson,
    int32_t punchTimeoutMs,
    int32_t* outTransportRoute,
    char* outActiveEndpoint,
    int32_t endpointBufLen);

OSMIUM_API int32_t Osmium_EnableTransportRelay(
    void* transport,
    const uint8_t* sessionToken16,
    int32_t localRole,
    OsmiumRelaySendCallback onSendRelay,
    const char* relayEndpointLabel);

OSMIUM_API int32_t Osmium_PushTransportRelayPacket(
    void* transport,
    const uint8_t* data,
    int32_t length);

OSMIUM_API int32_t Osmium_PushTransportRelayBatch(
    void* transport,
    const uint8_t* batchData,
    int32_t batchLength);

OSMIUM_API int32_t Osmium_SetTransportKey(void* transport, const uint8_t* aesKey32);
OSMIUM_API int32_t Osmium_SetTransportEncryption(void* transport, int32_t enableEncryption);

OSMIUM_API int32_t Osmium_AuthenticateClient(
    void* transport,
    const uint8_t* authTag32,
    const OsmiumClientConfig* requestedProfile,
    int32_t timeoutMs);

OSMIUM_API int32_t Osmium_WaitClientAuthHost(
    void* transport,
    const uint8_t* expectedTempTag32,
    const uint8_t* expectedFixedTag32,
    int32_t hasFixedCode,
    int32_t timeoutMs,
    OsmiumClientConfig* outNegotiatedProfile);

OSMIUM_API int32_t Osmium_StartHostSession(
    void* transport,
    const OsmiumHostConfig* config,
    const OsmiumClientConfig* negotiatedClientProfile,
    const OsmiumHostCallbacks* callbacks,
    void** outHostHandle);

OSMIUM_API void Osmium_StopHostSession(void* hostHandle);
OSMIUM_API int32_t Osmium_HostSendAuxData(void* hostHandle, const uint8_t* data, int32_t length);

OSMIUM_API int32_t Osmium_StartClientEmbedded(
    void* transport,
    void* swapChainPanelIUnknown,
    int32_t initialWidth,
    int32_t initialHeight,
    float dpiScale,
    const OsmiumClientConfig* config,
    const OsmiumClientCallbacks* callbacks,
    void** outClientHandle);

OSMIUM_API void Osmium_StopClientEmbedded(void* clientHandle);
OSMIUM_API void Osmium_ClientResize(void* clientHandle, int32_t width, int32_t height, float dpiScale);
OSMIUM_API void Osmium_ClientSwitchDisplay(void* clientHandle, int32_t displayIndex);
OSMIUM_API void Osmium_ClientUpdateVideoParams(
    void* clientHandle,
    int32_t targetWidth,
    int32_t targetHeight,
    int32_t fps,
    int32_t bitrateKbps,
    int32_t enableHdr,
    int32_t enableYuv444);
OSMIUM_API void Osmium_ClientSetMouseMode(void* clientHandle, int32_t mouseMode, int32_t hideHostCursor);
OSMIUM_API void Osmium_ClientSetVirtualAudio(
    void* clientHandle,
    int32_t virtualSpeaker,
    int32_t virtualMic,
    const char* localMicDeviceId);
OSMIUM_API void Osmium_ClientSendSystemAction(void* clientHandle, int32_t action);
OSMIUM_API void Osmium_ClientSendKey(void* clientHandle, uint16_t vkCode, uint16_t scanCode, int32_t isDown,
                                     int32_t isExtended);
OSMIUM_API void Osmium_ClientSendMouseMove(void* clientHandle, int32_t isAbsolute, float xOrDeltaX, float yOrDeltaY);
OSMIUM_API void Osmium_ClientSendMouseButton(void* clientHandle, uint8_t button, int32_t isDown);
OSMIUM_API void Osmium_ClientSendMouseWheel(void* clientHandle, int16_t deltaX, int16_t deltaY);
OSMIUM_API void Osmium_ClientPollInput(void* clientHandle, int32_t windowFocused);
OSMIUM_API void Osmium_ClientSetOverlayVisible(void* clientHandle, int32_t visible);
OSMIUM_API void Osmium_ClientSetVsync(void* clientHandle, int32_t enableVsync);
OSMIUM_API void Osmium_ClientSetMute(void* clientHandle, int32_t muted);
OSMIUM_API int32_t Osmium_ClientSendAuxData(void* clientHandle, const uint8_t* data, int32_t length);

OSMIUM_API int32_t Osmium_RunClientSession(
    void* transport,
    const OsmiumClientConfig* config,
    const OsmiumClientCallbacks* callbacks);

OSMIUM_API void Osmium_StopClientSession(void);
OSMIUM_API void Osmium_ToggleClientOverlay(void);
