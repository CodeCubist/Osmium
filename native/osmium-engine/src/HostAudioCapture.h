#pragma once

#include "OsmiumCommon.h"
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <wrl/client.h>
#include <opus.h>

extern "C" {
#include <libswresample/swresample.h>
}

#include <atomic>
#include <functional>
#include <mutex>
#include <thread>
#include <vector>

namespace osmium
{
    class UsbIpVirtualDevices;

    class HostAudioCapture
    {
    public:
        HostAudioCapture() = default;
        ~HostAudioCapture();

        using EncodedAudioCallback = std::function<void(const uint8_t* opusData, int bytes, int sampleCount)>;

        void SetVirtualDevices(UsbIpVirtualDevices* virtualDevices) { m_VirtualDevices = virtualDevices; }

        bool Start(EncodedAudioCallback callback);
        void Stop();

        void PushClientMicOpus(const uint8_t* opusData, int len, int sampleRate, int channels, int sampleCount);

    private:
        void CaptureThreadProc();
        void ShutdownMicDecoder();

        UsbIpVirtualDevices* m_VirtualDevices = nullptr;
        EncodedAudioCallback m_Callback;
        std::thread m_Thread;
        std::atomic<bool> m_StopFlag{false};

        OpusEncoder* m_Opus = nullptr;
        SwrContext* m_Swr = nullptr;

        std::mutex m_MicDecMutex;
        OpusDecoder* m_MicOpus = nullptr;
        int m_MicSampleRate = 48000;
        int m_MicChannels = 2;
        std::vector<int16_t> m_MicPcmBuf;
    };
}
