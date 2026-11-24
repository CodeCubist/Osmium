#pragma once

#include <stdint.h>

namespace osmium::proto
{
    constexpr uint8_t kMagic[4] = {'O', 'S', 'M', '1'};
    constexpr uint8_t kVersion = 1;

    enum class WireMsgType : uint8_t
    {
        StunReq = 0x01,
        StunResp = 0x02,
        Punch = 0x06,
        PunchAck = 0x07,
        EncryptedData = 0x10,
        PlainData = 0x11
    };

    enum class PeerRole : uint8_t
    {
        Client = 1,
        Host = 2
    };

    enum class AppPacketType : uint8_t
    {
        AuthRequest = 0x10,
        AuthResponse = 0x11,
        Ping = 0x12,
        Pong = 0x13,
        Disconnect = 0x14,

        VideoChunk = 0x20,
        VideoIdrRequest = 0x21,
        AudioFrame = 0x22,
        ClientMicAudioFrame = 0x23,

        InputKey = 0x30,
        InputMouseMove = 0x31,
        InputMouseButton = 0x32,
        InputMouseWheel = 0x33,
        InputUnicodeText = 0x34,
        InputGamepad = 0x35,
        ControlFeedback = 0x36,
        InputTouch = 0x37,

        ControlSwitchDisplay = 0x38,
        ControlVideoParams = 0x39,
        ControlMouseMode = 0x3A,
        ControlTextFocus = 0x3B,
        ControlVirtualAudio = 0x3C,
        ControlSystemAction = 0x3D,
        HostTextFocus = 0x3E,
        HostStatusJson = 0x3F,
        AuxData = 0x40
    };

    enum class VideoCodecId : uint8_t
    {
        H264 = 1,
        HEVC = 2,
        AV1 = 3
    };

    constexpr uint8_t kVideoFlagHdr = 0x01;
    constexpr uint8_t kVideoFlagYuv444 = 0x02;
    constexpr uint8_t kAppFlagHasActionSeq = 0x01;

    enum class MouseModeId : int32_t
    {
        HostMouse = 0,
        VirtualMouse = 1,
        SmartMode = 2
    };

    enum class SystemActionId : int32_t
    {
        SendCtrlAltDel = 1,
        OpenTaskManager = 2,
        InstallVirtualDrivers = 4,
        UninstallVirtualDrivers = 5
    };

#pragma pack(push, 1)

    struct EncryptedFrameHeader
    {
        uint8_t magic[4];
        uint8_t version;
        uint8_t msgType;
        uint16_t flags;
        uint32_t sequence;
        uint8_t nonce[12];
        uint8_t tag[16];
    };

    struct AppPacketHeader
    {
        uint8_t type;
        uint8_t flags;
        uint16_t reserved;
    };

    struct AuthRequestPayload
    {
        AppPacketHeader hdr;
        uint8_t authTag[32];
        int32_t desiredWidth;
        int32_t desiredHeight;
        int32_t desiredFps;
        int32_t desiredBitrateKbps;
        int32_t codecPreference;
        int32_t enableHdr;
    };

    struct AuthResponsePayload
    {
        AppPacketHeader hdr;
        int32_t accepted;
        int32_t errorCode;
        char message[96];
    };

    struct PingPongPayload
    {
        AppPacketHeader hdr;
        uint64_t timestampUs;
    };

    struct VideoChunkHeader
    {
        AppPacketHeader hdr;
        uint32_t frameId;
        uint16_t chunkIndex;
        uint16_t totalChunks;
        uint8_t codecId;
        uint8_t isKeyFrame;
        uint16_t width;
        uint16_t height;
        uint8_t colorFlags;
        uint8_t reserved3;
        uint64_t captureTimeUs;
    };

    struct AudioFrameHeader
    {
        AppPacketHeader hdr;
        uint32_t sequence;
        uint16_t sampleRate;
        uint8_t channels;
        uint8_t reserved2;
        uint16_t sampleCount;
        uint16_t payloadBytes;
    };

    struct InputKeyPayload
    {
        AppPacketHeader hdr;
        uint16_t vkCode;
        uint16_t scanCode;
        uint8_t isDown;
        uint8_t isExtended;
        uint16_t reserved2;
    };

    struct InputMouseMovePayload
    {
        AppPacketHeader hdr;
        uint8_t isAbsolute;
        uint8_t reserved2[3];
        float xOrDeltaX;
        float yOrDeltaY;
    };

    struct InputMouseButtonPayload
    {
        AppPacketHeader hdr;
        uint8_t button;
        uint8_t isDown;
        uint16_t reserved2;
    };

    struct InputMouseWheelPayload
    {
        AppPacketHeader hdr;
        int16_t deltaX;
        int16_t deltaY;
    };

    struct InputGamepadPayload
    {
        AppPacketHeader hdr;
        uint8_t controllerIndex;
        uint8_t connected;
        uint16_t buttonsMask;
        uint8_t leftTrigger;
        uint8_t rightTrigger;
        int16_t leftThumbX;
        int16_t leftThumbY;
        int16_t rightThumbX;
        int16_t rightThumbY;
    };

    enum class TouchPhase : uint8_t
    {
        Down = 1,
        Move = 2,
        Up = 3,
        Cancel = 4
    };

    constexpr uint8_t kMaxTouchContacts = 10;

    struct InputTouchContact
    {
        uint8_t id;
        uint8_t phase;
        uint16_t reserved;
        float x;
        float y;
    };

    // Client -> host: every contact currently on the client's screen, plus
    // the ones lifted since the previous frame (phase Up/Cancel). Contacts
    // missing from a frame are lifted, so a frame with count 0 releases all.
    // Coordinates are normalized to the streamed display. `seq` grows by one
    // per frame; repeats and late arrivals are dropped. Sent with only
    // `count` contacts, and only to hosts that report `touchInput`.
    struct InputTouchPayload
    {
        AppPacketHeader hdr;
        uint8_t count;
        uint8_t reserved2;
        uint16_t seq;
        InputTouchContact contacts[kMaxTouchContacts];
    };

    constexpr size_t kInputTouchHeaderSize = sizeof(AppPacketHeader) + 4;

    struct ControlSwitchDisplayPayload
    {
        AppPacketHeader hdr;
        int32_t displayIndex;
    };

    struct ControlVideoParamsPayload
    {
        AppPacketHeader hdr;
        int32_t targetWidth;
        int32_t targetHeight;
        int32_t fps;
        int32_t bitrateKbps;
        int32_t enableHdr;
        int32_t enableYuv444;
    };

    struct ControlMouseModePayload
    {
        AppPacketHeader hdr;
        int32_t mouseMode;
        int32_t hideHostCursor;
        int32_t reserved2;
    };

    struct ControlVirtualAudioPayload
    {
        AppPacketHeader hdr;
        int32_t virtualSpeakerEnabled;
        int32_t virtualMicEnabled;
    };

    struct ControlSystemActionPayload
    {
        AppPacketHeader hdr;
        int32_t action;
    };

    // Client -> host: start (1) or stop (0) reporting keyboard focus. Touch
    // clients opt in; older hosts ignore the packet.
    struct ControlTextFocusPayload
    {
        AppPacketHeader hdr;
        int32_t enabled;
    };

    // Host -> client: whether an editable control has keyboard focus, and the
    // caret (or the control) normalized to the streamed display. `serial`
    // moves whenever focus lands on another control.
    struct HostTextFocusPayload
    {
        AppPacketHeader hdr;
        uint8_t active;
        uint8_t hasRect;
        uint16_t serial;
        float x;
        float y;
        float w;
        float h;
    };

#pragma pack(pop)

    constexpr size_t kMaxVideoChunkPayload = 1240;
    constexpr size_t kMaxLanVideoChunkPayload = 1380;
    constexpr size_t kMaxRelayVideoChunkPayload = 16384;
    constexpr size_t kMaxAuxDataPayload = 1240;
}
