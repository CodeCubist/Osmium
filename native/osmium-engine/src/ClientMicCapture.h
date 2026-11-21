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
#include <string>
#include <thread>
#include <vector>

namespace osmium
{
    class ClientMicCapture
    {
    public:
        ClientMicCapture() = default;
        ~ClientMicCapture();

        using EncodedMicCallback = std::function<void(const uint8_t* opusData, int bytes, int sampleCount)>;

        static std::vector<OsmiumAudioDeviceInfo> EnumerateMicDevices();

        bool Start(const std::string& deviceIdUtf8, EncodedMicCallback callback);
        void Stop();
        void SetMuted(bool muted) { m_Muted.store(muted); }
        bool IsRunning() const { return m_Running.load(); }

    private:
        void CaptureThreadProc(std::string deviceIdUtf8);

        EncodedMicCallback m_Callback;
        std::thread m_Thread;
        std::atomic<bool> m_StopFlag{false};
        std::atomic<bool> m_Running{false};
        std::atomic<bool> m_Muted{false};
    };
}
