#include "HostAudioCapture.h"
#include "UsbIpVirtualDevices.h"

extern "C" {
#include <libavutil/opt.h>
}

#include <algorithm>
#include <cmath>
#include <cstring>

#pragma comment(lib, "ole32.lib")

namespace osmium
{
    using Microsoft::WRL::ComPtr;

    namespace
    {
        // Smooth soft-knee limiter above 0.92f so transient Windows mixer overshoots
        // never hard-clip into crackling artifacts.
        inline float SoftLimitSample(float x)
        {
            const float ax = std::fabs(x);
            if (ax <= 0.92f) return x;
            const float excess = ax - 0.92f;
            const float shaped = 0.92f + 0.075f * std::tanh(excess / 0.075f);
            return (x < 0.0f) ? -shaped : shaped;
        }
    }

    HostAudioCapture::~HostAudioCapture()
    {
        Stop();
    }

    void HostAudioCapture::SetTargetBitrateKbps(int kbps)
    {
        int clamped = std::clamp(kbps, 64, 320);
        m_TargetBitrateKbps.store(clamped, std::memory_order_release);
    }

    void HostAudioCapture::SetExpectedLossPerc(int perc)
    {
        int clamped = std::clamp(perc, 0, 30);
        m_ExpectedLossPerc.store(clamped, std::memory_order_release);
    }

    bool HostAudioCapture::Start(EncodedAudioCallback callback)
    {
        std::lock_guard<std::mutex> lock(m_LifecycleMutex);
        m_StopFlag.store(true, std::memory_order_release);
        if (m_Thread.joinable())
        {
            if (m_Thread.get_id() == std::this_thread::get_id()) m_Thread.detach();
            else m_Thread.join();
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
        m_Callback = std::move(callback);
        m_StopFlag.store(false, std::memory_order_release);
        m_Thread = std::thread(&HostAudioCapture::CaptureThreadProc, this);
        return true;
    }

    void HostAudioCapture::Stop()
    {
        m_StopFlag.store(true, std::memory_order_release);
        std::lock_guard<std::mutex> lock(m_LifecycleMutex);
        if (m_Thread.joinable())
        {
            if (m_Thread.get_id() == std::this_thread::get_id()) m_Thread.detach();
            else m_Thread.join();
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
        // OPUS_APPLICATION_AUDIO preserves full stereo music/game psychacoustic fidelity
        // (instead of RESTRICTED_LOWDELAY which degraded timbre and broke FEC).
        m_Opus = opus_encoder_create(48000, 2, OPUS_APPLICATION_AUDIO, &opusErr);
        if (!m_Opus || opusErr != OPUS_OK)
        {
            OSM_LOG_ERROR("opus_encoder_create failed: %d", opusErr);
            if (SUCCEEDED(hrCom)) CoUninitialize();
            return;
        }
        int appliedBitrateKbps = m_TargetBitrateKbps.load(std::memory_order_acquire);
        int appliedLossPerc = m_ExpectedLossPerc.load(std::memory_order_acquire);
        opus_encoder_ctl(m_Opus, OPUS_SET_BITRATE(appliedBitrateKbps * 1000));
        opus_encoder_ctl(m_Opus, OPUS_SET_COMPLEXITY(8));
        opus_encoder_ctl(m_Opus, OPUS_SET_MAX_BANDWIDTH(OPUS_BANDWIDTH_FULLBAND));
        opus_encoder_ctl(m_Opus, OPUS_SET_VBR(1));
        opus_encoder_ctl(m_Opus, OPUS_SET_VBR_CONSTRAINT(1));
        opus_encoder_ctl(m_Opus, OPUS_SET_INBAND_FEC(1));
        opus_encoder_ctl(m_Opus, OPUS_SET_PACKET_LOSS_PERC(appliedLossPerc));
        // Disable DTX so soft music passages / reverb tails never produce artificial gaps.
        opus_encoder_ctl(m_Opus, OPUS_SET_DTX(0));

        ComPtr<IMMDeviceEnumerator> enumerator;
        ComPtr<IMMDevice> device;
        ComPtr<IAudioClient> audioClient;
        ComPtr<IAudioCaptureClient> captureClient;
        WAVEFORMATEX* mixFmt = nullptr;
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

            // Use a 100ms WASAPI loopback ring buffer (zero extra latency — WASAPI still
            // delivers packets every ~10ms engine period — while eliminating Windows loopback
            // DATA_DISCONTINUITY drops under scheduling jitter).
            if (FAILED(audioClient->Initialize(
                AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 1000000, 0, mixFmt, nullptr)))
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
                                    &inLayout, inSampleFmt, static_cast<int>(mixFmt->nSamplesPerSec), 0, nullptr) < 0)
            {
                return false;
            }
            // Prevent 5.1/7.1 surround -> stereo downmix from exceeding +/-1.0f and clipping.
            av_opt_set_double(m_Swr, "rematrix_maxval", 1.0, 0);
            if (swr_init(m_Swr) < 0)
            {
                swr_free(&m_Swr);
                m_Swr = nullptr;
                return false;
            }

            if (FAILED(audioClient->Start()))
            {
                return false;
            }

            OSM_LOG_INFO("WASAPI loopback audio capture started (%u Hz, %u ch -> 48kHz Opus AUDIO %d kbps 20ms)",
                         mixFmt->nSamplesPerSec, mixFmt->nChannels, appliedBitrateKbps);
            return true;
        };

        bool wasapiReady = initWasapiLoopback();
        if (!wasapiReady)
        {
            OSM_LOG_WARN("WASAPI loopback endpoint initially unavailable");
        }

        constexpr int kFrameSamples = 960;
        constexpr size_t kFrameFloats = static_cast<size_t>(kFrameSamples) * 2;
        constexpr uint32_t kSilenceTailFrames = 75; // 1500ms trailing silence avoids re-priming during speech pauses
        std::vector<int16_t> vspkPcm(kFrameSamples * 2);
        std::vector<float> encodeFloatFrame(kFrameFloats);
        size_t vspkHave = 0;
        std::vector<float> pcmFifo;
        pcmFifo.reserve(kFrameFloats * 8);
        size_t fifoReadPos = 0;
        std::vector<float> resampled(4096 * 2);
        uint8_t opusBuf[1500]{};
        uint8_t prevOpusBuf[1500]{};
        int prevOpusLen = 0;
        bool prevVirtualMode = false;
        bool streamActive = false;
        uint32_t consecutiveSilentFrames = kSilenceTailFrames;

        auto applyDynamicEncoderParams = [&]()
        {
            const int desiredKbps = m_TargetBitrateKbps.load(std::memory_order_acquire);
            if (desiredKbps != appliedBitrateKbps)
            {
                appliedBitrateKbps = desiredKbps;
                opus_encoder_ctl(m_Opus, OPUS_SET_BITRATE(appliedBitrateKbps * 1000));
            }
            const int desiredLoss = m_ExpectedLossPerc.load(std::memory_order_acquire);
            if (desiredLoss != appliedLossPerc)
            {
                appliedLossPerc = desiredLoss;
                opus_encoder_ctl(m_Opus, OPUS_SET_PACKET_LOSS_PERC(appliedLossPerc));
            }
        };

        auto emitEncodedFloatFrame = [&](const float* srcFloats, uint64_t captureTimeUs, bool frameSilent)
        {
            if (m_Paused.load(std::memory_order_acquire))
            {
                streamActive = false;
                prevOpusLen = 0;
                consecutiveSilentFrames = kSilenceTailFrames;
                return;
            }

            if (!frameSilent)
            {
                consecutiveSilentFrames = 0;
            }
            else if (consecutiveSilentFrames < kSilenceTailFrames)
            {
                consecutiveSilentFrames++;
            }

            const bool shouldEmit = (!frameSilent || consecutiveSilentFrames <= kSilenceTailFrames) &&
                                    (streamActive || !frameSilent);
            if (!shouldEmit)
            {
                streamActive = false;
                prevOpusLen = 0;
                return;
            }

            applyDynamicEncoderParams();

            for (size_t i = 0; i < kFrameFloats; ++i)
            {
                encodeFloatFrame[i] = SoftLimitSample(srcFloats[i]);
            }

            int encoded = opus_encode_float(
                m_Opus,
                encodeFloatFrame.data(),
                kFrameSamples,
                opusBuf,
                sizeof(opusBuf));

            if (encoded > 0 && m_Callback)
            {
                uint8_t aFlags = 0;
                if (!streamActive)
                {
                    aFlags |= proto::kAudioFlagStreamStart;
                    prevOpusLen = 0;
                }
                const bool isTailEnd = (frameSilent && consecutiveSilentFrames == kSilenceTailFrames);
                if (isTailEnd)
                {
                    aFlags |= proto::kAudioFlagSilenceEnd;
                }

                const uint8_t* redPtr = nullptr;
                int redLen = 0;
                if (m_RedundancyEnabled.load(std::memory_order_acquire) &&
                    prevOpusLen > 0 &&
                    (aFlags & proto::kAudioFlagStreamStart) == 0 &&
                    (encoded + prevOpusLen) <= 1320)
                {
                    aFlags |= proto::kAudioFlagHasPrevRedundant;
                    redPtr = prevOpusBuf;
                    redLen = prevOpusLen;
                }

                m_Callback(opusBuf, encoded, kFrameSamples, captureTimeUs, aFlags, redPtr, redLen);

                if (isTailEnd)
                {
                    streamActive = false;
                    prevOpusLen = 0;
                }
                else
                {
                    streamActive = true;
                    if (encoded <= static_cast<int>(sizeof(prevOpusBuf)))
                    {
                        memcpy(prevOpusBuf, opusBuf, static_cast<size_t>(encoded));
                        prevOpusLen = encoded;
                    }
                    else
                    {
                        prevOpusLen = 0;
                    }
                }
            }
        };

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

        while (!m_StopFlag.load(std::memory_order_acquire))
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
                    streamActive = false;
                    prevOpusLen = 0;
                    consecutiveSilentFrames = kSilenceTailFrames;
                    OSM_LOG_INFO(
                        "HostAudioCapture reading directly from Osmium Virtual Speaker USB/IP sink (host muted)");
                }

                size_t need = static_cast<size_t>(kFrameSamples) - vspkHave;
                size_t got = m_VirtualDevices->PopSpeakerPcm48kStereo(vspkPcm.data() + vspkHave * 2, need);
                vspkHave += got;
                if (vspkHave >= static_cast<size_t>(kFrameSamples))
                {
                    const uint64_t nowUs = NowMicros();
                    const uint64_t captureTimeUs = (nowUs > 20000ULL) ? (nowUs - 20000ULL) : nowUs;
                    bool frameSilent = true;
                    for (size_t i = 0; i < kFrameFloats; ++i)
                    {
                        float f = static_cast<float>(vspkPcm[i]) / 32768.0f;
                        encodeFloatFrame[i] = f;
                        if (frameSilent && std::fabs(f) > 3e-5f)
                        {
                            frameSilent = false;
                        }
                    }
                    vspkHave = 0;
                    emitEncodedFloatFrame(encodeFloatFrame.data(), captureTimeUs, frameSilent);
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
                streamActive = false;
                prevOpusLen = 0;
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
                waitShort(3);
                continue;
            }

            while (packetLength > 0 && !m_StopFlag.load(std::memory_order_acquire))
            {
                BYTE* pData = nullptr;
                UINT32 numFrames = 0;
                DWORD flags = 0;
                if (FAILED(captureClient->GetBuffer(&pData, &numFrames, &flags, nullptr, nullptr)))
                {
                    break;
                }
                const uint64_t captureNowUs = NowMicros();

                if (numFrames > 0)
                {
                    if (m_Paused.load(std::memory_order_acquire))
                    {
                        pcmFifo.clear();
                        fifoReadPos = 0;
                        streamActive = false;
                        prevOpusLen = 0;
                        consecutiveSilentFrames = kSilenceTailFrames;
                    }
                    else if (flags & AUDCLNT_BUFFERFLAGS_SILENT)
                    {
                        if (streamActive || consecutiveSilentFrames < kSilenceTailFrames)
                        {
                            size_t outCount = static_cast<size_t>((uint64_t(numFrames) * 48000ULL) / mixFmt->nSamplesPerSec);
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

                // Click-free crossfade backlog trim only if WASAPI delivered a severe late burst (> 120ms).
                const size_t avail = pcmFifo.size() - fifoReadPos;
                if (avail > kFrameFloats * 6)
                {
                    const size_t targetPos = pcmFifo.size() - kFrameFloats * 2;
                    constexpr size_t kCrossfadeSamples = 192; // 4ms @ 48kHz
                    if (targetPos >= fifoReadPos + kCrossfadeSamples * 2 &&
                        targetPos + kCrossfadeSamples * 2 <= pcmFifo.size())
                    {
                        for (size_t s = 0; s < kCrossfadeSamples; ++s)
                        {
                            const float alpha = static_cast<float>(s) / static_cast<float>(kCrossfadeSamples);
                            const size_t oldIdx = fifoReadPos + s * 2;
                            const size_t newIdx = targetPos + s * 2;
                            pcmFifo[newIdx] = pcmFifo[oldIdx] * (1.0f - alpha) + pcmFifo[newIdx] * alpha;
                            pcmFifo[newIdx + 1] = pcmFifo[oldIdx + 1] * (1.0f - alpha) + pcmFifo[newIdx + 1] * alpha;
                        }
                    }
                    fifoReadPos = targetPos;
                }

                while (pcmFifo.size() - fifoReadPos >= kFrameFloats)
                {
                    const float* framePtr = pcmFifo.data() + fifoReadPos;
                    const uint64_t remainingSamples = static_cast<uint64_t>((pcmFifo.size() - fifoReadPos) / 2);
                    const uint64_t offsetUs = (remainingSamples * 1000000ULL) / 48000ULL;
                    const uint64_t frameCaptureUs = (captureNowUs > offsetUs) ? (captureNowUs - offsetUs) : captureNowUs;

                    bool frameSilent = true;
                    for (size_t i = 0; i < kFrameFloats; i += 2)
                    {
                        if (std::fabs(framePtr[i]) > 3e-5f || std::fabs(framePtr[i + 1]) > 3e-5f)
                        {
                            frameSilent = false;
                            break;
                        }
                    }

                    emitEncodedFloatFrame(framePtr, frameCaptureUs, frameSilent);
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
