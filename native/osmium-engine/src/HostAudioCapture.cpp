#include "HostAudioCapture.h"
#include "UsbIpVirtualDevices.h"

#include <algorithm>
#include <cmath>
#include <cstring>

#pragma comment(lib, "ole32.lib")

namespace osmium
{
    using Microsoft::WRL::ComPtr;

    HostAudioCapture::~HostAudioCapture()
    {
        Stop();
    }

    bool HostAudioCapture::Start(EncodedAudioCallback callback)
    {
        Stop();
        m_Callback = std::move(callback);
        m_StopFlag.store(false);
        m_Thread = std::thread(&HostAudioCapture::CaptureThreadProc, this);
        return true;
    }

    void HostAudioCapture::Stop()
    {
        m_StopFlag.store(true);
        if (m_Thread.joinable())
        {
            m_Thread.join();
        }
        if (m_Swr)
        {
            swr_free(&m_Swr);
            m_Swr = nullptr;
        }
        if (m_Opus)
        {
            opus_encoder_destroy(m_Opus);
            m_Opus = nullptr;
        }
        ShutdownMicDecoder();
    }

    void HostAudioCapture::ShutdownMicDecoder()
    {
        std::lock_guard<std::mutex> lock(m_MicDecMutex);
        if (m_MicOpus)
        {
            opus_decoder_destroy(m_MicOpus);
            m_MicOpus = nullptr;
        }
        m_MicPcmBuf.clear();
    }

    void HostAudioCapture::PushClientMicOpus(const uint8_t* opusData, int len, int sampleRate, int channels,
                                             int sampleCount)
    {
        if (!m_VirtualDevices || !m_VirtualDevices->IsVirtualMicActive() || !opusData || len <= 0) return;

        std::lock_guard<std::mutex> lock(m_MicDecMutex);
        if (!m_MicOpus || sampleRate != m_MicSampleRate || channels != m_MicChannels)
        {
            if (m_MicOpus)
            {
                opus_decoder_destroy(m_MicOpus);
                m_MicOpus = nullptr;
            }
            int err = OPUS_OK;
            m_MicOpus = opus_decoder_create(sampleRate > 0 ? sampleRate : 48000,
                                            channels == 1 ? 1 : 2, &err);
            if (!m_MicOpus || err != OPUS_OK)
            {
                m_MicOpus = nullptr;
                return;
            }
            m_MicSampleRate = sampleRate > 0 ? sampleRate : 48000;
            m_MicChannels = (channels == 1 ? 1 : 2);
        }

        int maxSamples = std::max(sampleCount, 1920);
        if (static_cast<int>(m_MicPcmBuf.size()) < maxSamples * 2)
        {
            m_MicPcmBuf.resize(static_cast<size_t>(maxSamples) * 2);
        }

        int decoded = opus_decode(m_MicOpus, opusData, len, m_MicPcmBuf.data(), maxSamples, 0);
        if (decoded <= 0) return;

        if (m_MicChannels == 1)
        {
            for (int i = decoded - 1; i >= 0; --i)
            {
                int16_t s = m_MicPcmBuf[static_cast<size_t>(i)];
                m_MicPcmBuf[static_cast<size_t>(i) * 2] = s;
                m_MicPcmBuf[static_cast<size_t>(i) * 2 + 1] = s;
            }
        }

        m_VirtualDevices->PushMicPcm48kStereo(m_MicPcmBuf.data(), static_cast<size_t>(decoded));
    }

    void HostAudioCapture::CaptureThreadProc()
    {
        // Deliberately avoid registering with Windows MMCSS ("Pro Audio") here:
        // keeping an MMCSS audio task active activates Windows NDIS NetworkThrottlingIndex
        // (10 pkt/ms cap) even while the host is silent.
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
        HRESULT hrCom = CoInitializeEx(nullptr, COINIT_MULTITHREADED);

        int opusErr = OPUS_OK;
        m_Opus = opus_encoder_create(48000, 2, OPUS_APPLICATION_RESTRICTED_LOWDELAY, &opusErr);
        if (!m_Opus || opusErr != OPUS_OK)
        {
            OSM_LOG_ERROR("opus_encoder_create failed: %d", opusErr);
            if (SUCCEEDED(hrCom)) CoUninitialize();
            return;
        }
        opus_encoder_ctl(m_Opus, OPUS_SET_BITRATE(96000));
        opus_encoder_ctl(m_Opus, OPUS_SET_COMPLEXITY(5));
        opus_encoder_ctl(m_Opus, OPUS_SET_INBAND_FEC(1));
        opus_encoder_ctl(m_Opus, OPUS_SET_PACKET_LOSS_PERC(5));
        opus_encoder_ctl(m_Opus, OPUS_SET_DTX(1));

        ComPtr<IMMDeviceEnumerator> enumerator;
        ComPtr<IMMDevice> device;
        ComPtr<IAudioClient> audioClient;
        ComPtr<IAudioCaptureClient> captureClient;
        WAVEFORMATEX* mixFmt = nullptr;
        // Use a 5ms high-res waitable timer instead of AUDCLNT_STREAMFLAGS_EVENTCALLBACK
        // on WASAPI loopback: loopback event-driven mode is notoriously flaky/bursty on
        // Realtek/Nahimic/USB audio drivers, whereas steady polling avoids audio packet bursts.
        HANDLE pollTimer = CreateWaitableTimerExW(nullptr, nullptr, 0x00000002 /*HIGH_RESOLUTION*/, TIMER_ALL_ACCESS);
        if (!pollTimer)
        {
            pollTimer = CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_ALL_ACCESS);
        }

        auto initWasapiLoopback = [&]() -> bool
        {
            if (mixFmt)
            {
                CoTaskMemFree(mixFmt);
                mixFmt = nullptr;
            }
            captureClient.Reset();
            if (audioClient)
            {
                audioClient->Stop();
                audioClient.Reset();
            }
            device.Reset();
            if (m_Swr)
            {
                swr_free(&m_Swr);
                m_Swr = nullptr;
            }

            if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)))
                ||
                FAILED(enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device)) ||
                FAILED(
                    device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(audioClient.
                        GetAddressOf()))) ||
                FAILED(audioClient->GetMixFormat(&mixFmt)) || !mixFmt)
            {
                return false;
            }

            if (FAILED(audioClient->Initialize(
                AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 200000, 0, mixFmt, nullptr)))
            {
                return false;
            }

            if (FAILED(audioClient->GetService(IID_PPV_ARGS(&captureClient))))
            {
                return false;
            }

            AVChannelLayout outLayout{}, inLayout{};
            av_channel_layout_default(&outLayout, 2);
            av_channel_layout_default(&inLayout, mixFmt->nChannels);

            AVSampleFormat inSampleFmt = AV_SAMPLE_FMT_FLT;
            if (mixFmt->wFormatTag == WAVE_FORMAT_PCM ||
                (mixFmt->wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
                    reinterpret_cast<WAVEFORMATEXTENSIBLE*>(mixFmt)->SubFormat == KSDATAFORMAT_SUBTYPE_PCM))
            {
                inSampleFmt = (mixFmt->wBitsPerSample == 16) ? AV_SAMPLE_FMT_S16 : AV_SAMPLE_FMT_S32;
            }

            if (swr_alloc_set_opts2(&m_Swr, &outLayout, AV_SAMPLE_FMT_FLT, 48000,
                                    &inLayout, inSampleFmt, static_cast<int>(mixFmt->nSamplesPerSec), 0, nullptr) < 0 ||
                swr_init(m_Swr) < 0)
            {
                return false;
            }

            if (FAILED(audioClient->Start()))
            {
                return false;
            }

            OSM_LOG_INFO("WASAPI loopback audio capture started (%u Hz, %u ch -> 48kHz Opus 20ms, DTX+silence-gate)",
                         mixFmt->nSamplesPerSec, mixFmt->nChannels);
            return true;
        };

        bool wasapiReady = initWasapiLoopback();
        if (!wasapiReady)
        {
            OSM_LOG_WARN("WASAPI loopback endpoint initially unavailable");
        }

        // 20ms Opus frames (960 samples @ 48kHz = 50 pkt/s) cut audio packet/mutex/WS overhead in half
        // compared to 10ms frames while preserving ultra-low latency and enabling in-band FEC.
        constexpr int kFrameSamples = 960;
        constexpr size_t kFrameFloats = static_cast<size_t>(kFrameSamples) * 2;
        constexpr uint32_t kSilenceTailFrames = 10; // 200ms trailing silence before gating off
        std::vector<int16_t> vspkPcm(kFrameSamples * 2);
        size_t vspkHave = 0;
        std::vector<float> pcmFifo;
        pcmFifo.reserve(kFrameFloats * 8);
        size_t fifoReadPos = 0;
        std::vector<float> resampled(4096 * 2);
        uint8_t opusBuf[1500]{};
        bool prevVirtualMode = false;
        uint32_t consecutiveSilentFrames = kSilenceTailFrames;

        auto waitShort = [&](int ms)
        {
            if (pollTimer)
            {
                LARGE_INTEGER due{};
                due.QuadPart = -static_cast<LONGLONG>(ms) * 10000LL;
                if (SetWaitableTimer(pollTimer, &due, 0, nullptr, nullptr, FALSE))
                {
                    WaitForSingleObject(pollTimer, static_cast<DWORD>(ms + 5));
                    return;
                }
            }
            Sleep(static_cast<DWORD>(ms));
        };

        while (!m_StopFlag.load())
        {
            bool virtSpeakerActive = m_VirtualDevices &&
                m_VirtualDevices->IsVirtualSpeakerActive() &&
                m_VirtualDevices->IsHostPlaybackRoutedToVirtualSpeaker();

            if (virtSpeakerActive)
            {
                if (!prevVirtualMode)
                {
                    prevVirtualMode = true;
                    pcmFifo.clear();
                    fifoReadPos = 0;
                    vspkHave = 0;
                    consecutiveSilentFrames = kSilenceTailFrames;
                    OSM_LOG_INFO(
                        "HostAudioCapture reading directly from Osmium Virtual Speaker USB/IP sink (host muted)");
                }

                size_t need = static_cast<size_t>(kFrameSamples) - vspkHave;
                size_t got = m_VirtualDevices->PopSpeakerPcm48kStereo(vspkPcm.data() + vspkHave * 2, need);
                vspkHave += got;
                if (vspkHave >= static_cast<size_t>(kFrameSamples))
                {
                    bool frameSilent = true;
                    for (size_t i = 0; i < vspkPcm.size(); i += 4)
                    {
                        if (std::abs(vspkPcm[i]) > 2)
                        {
                            frameSilent = false;
                            break;
                        }
                    }
                    if (!frameSilent) consecutiveSilentFrames = 0;
                    else if (consecutiveSilentFrames < kSilenceTailFrames) consecutiveSilentFrames++;

                    vspkHave = 0;
                    if (!frameSilent || consecutiveSilentFrames < kSilenceTailFrames)
                    {
                        int encoded = opus_encode(m_Opus, vspkPcm.data(), kFrameSamples, opusBuf, sizeof(opusBuf));
                        if (encoded > 2 && m_Callback)
                        {
                            m_Callback(opusBuf, encoded, kFrameSamples);
                        }
                    }
                }
                else
                {
                    waitShort(4);
                }
                continue;
            }

            if (prevVirtualMode)
            {
                prevVirtualMode = false;
                vspkHave = 0;
                consecutiveSilentFrames = kSilenceTailFrames;
                Sleep(120);
                wasapiReady = initWasapiLoopback();
            }

            if (!wasapiReady || !captureClient)
            {
                Sleep(200);
                wasapiReady = initWasapiLoopback();
                continue;
            }

            UINT32 packetLength = 0;
            if (FAILED(captureClient->GetNextPacketSize(&packetLength)))
            {
                wasapiReady = initWasapiLoopback();
                Sleep(30);
                continue;
            }

            if (packetLength == 0)
            {
                waitShort(5);
                continue;
            }

            while (packetLength > 0 && !m_StopFlag.load())
            {
                BYTE* pData = nullptr;
                UINT32 numFrames = 0;
                DWORD flags = 0;
                if (FAILED(captureClient->GetBuffer(&pData, &numFrames, &flags, nullptr, nullptr)))
                {
                    break;
                }

                if (numFrames > 0)
                {
                    if (flags & AUDCLNT_BUFFERFLAGS_SILENT)
                    {
                        if (consecutiveSilentFrames < kSilenceTailFrames)
                        {
                            size_t outCount = static_cast<size_t>((uint64_t(numFrames) * 48000ULL) / mixFmt->
                                nSamplesPerSec);
                            pcmFifo.insert(pcmFifo.end(), outCount * 2, 0.0f);
                        }
                    }
                    else
                    {
                        const uint8_t* inPtr[1] = {pData};
                        int maxOut = static_cast<int>(swr_get_out_samples(m_Swr, static_cast<int>(numFrames))) + 64;
                        if (static_cast<int>(resampled.size()) < maxOut * 2)
                        {
                            resampled.resize(static_cast<size_t>(maxOut) * 2);
                        }
                        uint8_t* outPtr[1] = {reinterpret_cast<uint8_t*>(resampled.data())};
                        int converted = swr_convert(m_Swr, outPtr, maxOut, inPtr, static_cast<int>(numFrames));
                        if (converted > 0)
                        {
                            pcmFifo.insert(pcmFifo.end(), resampled.data(),
                                           resampled.data() + static_cast<size_t>(converted) * 2);
                        }
                    }
                }

                captureClient->ReleaseBuffer(numFrames);

                // Drop stale backlog if WASAPI delivered a late burst (> 60ms) so we never
                // flood the UDP socket with back-to-back audio bursts.
                const size_t avail = pcmFifo.size() - fifoReadPos;
                if (avail > kFrameFloats * 3)
                {
                    fifoReadPos = pcmFifo.size() - kFrameFloats * 2;
                }

                while (pcmFifo.size() - fifoReadPos >= kFrameFloats)
                {
                    const float* framePtr = pcmFifo.data() + fifoReadPos;
                    bool frameSilent = true;
                    for (size_t i = 0; i < kFrameFloats; i += 4)
                    {
                        if (std::fabs(framePtr[i]) > 1e-4f)
                        {
                            frameSilent = false;
                            break;
                        }
                    }
                    if (!frameSilent) consecutiveSilentFrames = 0;
                    else if (consecutiveSilentFrames < kSilenceTailFrames) consecutiveSilentFrames++;

                    if (!frameSilent || consecutiveSilentFrames < kSilenceTailFrames)
                    {
                        int encoded = opus_encode_float(
                            m_Opus,
                            framePtr,
                            kFrameSamples,
                            opusBuf,
                            sizeof(opusBuf));
                        // Opus DTX returns 1-2 byte TOC-only frames for silence; skip those.
                        if (encoded > 2 && m_Callback)
                        {
                            m_Callback(opusBuf, encoded, kFrameSamples);
                        }
                    }
                    fifoReadPos += kFrameFloats;
                }

                if (fifoReadPos == pcmFifo.size())
                {
                    pcmFifo.clear();
                    fifoReadPos = 0;
                }
                else if (fifoReadPos >= kFrameFloats * 4)
                {
                    pcmFifo.erase(pcmFifo.begin(), pcmFifo.begin() + static_cast<ptrdiff_t>(fifoReadPos));
                    fifoReadPos = 0;
                }

                if (FAILED(captureClient->GetNextPacketSize(&packetLength))) break;
            }
        }

        if (pollTimer) CloseHandle(pollTimer);
        if (mixFmt) CoTaskMemFree(mixFmt);
        captureClient.Reset();
        if (audioClient) audioClient->Stop();
        audioClient.Reset();
        device.Reset();
        enumerator.Reset();
        if (SUCCEEDED(hrCom)) CoUninitialize();
    }
}
