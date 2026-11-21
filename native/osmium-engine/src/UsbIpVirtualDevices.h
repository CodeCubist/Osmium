#pragma once

#include "OsmiumCommon.h"

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace osmium
{
    struct VirtualMouseReport
    {
        uint8_t buttons = 0;
        int16_t dx = 0;
        int16_t dy = 0;
        int8_t wheel = 0;
        int8_t hwheel = 0;
    };

    class UsbIpVirtualDevices
    {
    public:
        UsbIpVirtualDevices() = default;
        ~UsbIpVirtualDevices();

        UsbIpVirtualDevices(const UsbIpVirtualDevices&) = delete;
        UsbIpVirtualDevices& operator=(const UsbIpVirtualDevices&) = delete;

        static bool IsDriverAvailable();
        static bool EnsureDriverInstalled();
        static bool UninstallDriver();

        bool StartServer();
        void StopAll();

        bool SetVirtualMouseEnabled(bool enabled);
        bool IsVirtualMouseActive() const { return m_MouseActive.load(); }
        bool HasActiveHidConsumer() const { return m_MouseActive.load() && m_MouseHidReady.load(); }
        void PushMouseDelta(int16_t dx, int16_t dy);
        void PushMouseButton(uint8_t buttonMaskBit, bool isDown);
        void PushMouseWheel(int8_t wheel, int8_t hwheel);

        bool SetVirtualMicEnabled(bool enabled);
        bool IsVirtualMicActive() const { return m_MicActive.load(); }
        void PushMicPcm48kStereo(const int16_t* samples, size_t frameCount);

        bool SetVirtualSpeakerEnabled(bool enabled);
        bool IsVirtualSpeakerActive() const { return m_SpeakerActive.load(); }
        size_t PopSpeakerPcm48kStereo(int16_t* outSamples, size_t maxFrames);
        bool IsHostPlaybackRoutedToVirtualSpeaker() const { return m_DefaultRenderSwitched.load(); }

    private:
        struct PendingMouseSubmit;
        class VirtualDeviceBase;
        class HidMouseDevice;
        class Uac1AudioBase;
        class UacMicDevice;
        class UacSpeakerDevice;

        void AcceptLoop();
        void HandleClientSocket(SOCKET clientSock);

        bool AttachBusId(const char* busId, bool lowLatency, int& outPort);
        void DetachPort(int& ioPort);
        void DetachAllOsmiumPorts();

        bool SwitchDefaultPlaybackToVirtualSpeaker();
        void RestoreDefaultPlaybackDevice();
        static std::wstring FindVirtualAudioEndpointId(const wchar_t* friendlyMatch, bool isCapture);
        static bool SetDefaultAudioEndpoint(const std::wstring& deviceId);

        std::atomic<bool> m_ServerRunning{false};
        SOCKET m_ListenSock = INVALID_SOCKET;
        uint16_t m_ListenPort = 0;
        std::thread m_AcceptThread;

        std::mutex m_ConnThreadsMutex;
        std::vector<std::thread> m_ConnThreads;

        std::mutex m_DeviceMutex;
        HidMouseDevice* m_MouseDev = nullptr;
        UacMicDevice* m_MicDev = nullptr;
        UacSpeakerDevice* m_SpeakerDev = nullptr;

        int m_MousePort = 0;
        int m_MicPort = 0;
        int m_SpeakerPort = 0;

        std::atomic<bool> m_MouseActive{false};
        std::atomic<bool> m_MouseHidReady{false};
        std::atomic<bool> m_MicActive{false};
        std::atomic<bool> m_SpeakerActive{false};
        std::atomic<uint8_t> m_MouseButtons{0};

        std::mutex m_AudioPolicyMutex;
        std::wstring m_OriginalDefaultRenderId;
        std::atomic<bool> m_DefaultRenderSwitched{false};
    };
}
