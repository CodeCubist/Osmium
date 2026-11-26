#include "OsmiumNative.h"
#include "OsmiumCommon.h"
#include "OsmiumProtocol.h"
#include "VideoDecodeGate.h"
#include "OsmiumTransport.h"
#include "DriverSetupHelper.h"
#include "UsbIpVirtualDevices.h"
#include "HostSystemAgent.h"
#include "HostCapture.h"
#include "HostPrivacyScreen.h"
#include "HostVideoEncoder.h"
#include "HostAudioCapture.h"
#include "HostInputInjector.h"
#include "HostTextFocus.h"
#include "ClientVideoDecoder.h"
#include "ClientAudioPlayer.h"
#include "ClientMicCapture.h"
#include "ClientInputCapture.h"

#include <SDL3/SDL.h>

extern "C" {
#include <libavutil/log.h>
#include <libavcodec/avcodec.h>
}

#include <algorithm>
#include <atomic>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <deque>
#include <intrin.h>
#include <map>
#include <memory>
#include <mutex>
#include <objbase.h>
#include <sstream>
#include <thread>
#include <timeapi.h>
#include <vector>

#pragma comment(lib, "winmm.lib")

using namespace osmium;

namespace
{
#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

    class ScopedHighResTimer
    {
    public:
        ScopedHighResTimer()
        {
            m_PeriodActive = (timeBeginPeriod(1) == TIMERR_NOERROR);
            m_Timer = CreateWaitableTimerExW(
                nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            if (!m_Timer)
            {
                m_Timer = CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_ALL_ACCESS);
            }
        }

        ~ScopedHighResTimer()
        {
            if (m_Timer)
            {
                CloseHandle(m_Timer);
                m_Timer = nullptr;
            }
            if (m_PeriodActive)
            {
                timeEndPeriod(1);
                m_PeriodActive = false;
            }
        }

        ScopedHighResTimer(const ScopedHighResTimer&) = delete;
        ScopedHighResTimer& operator=(const ScopedHighResTimer&) = delete;

        void WaitUntilUs(uint64_t targetUs) const
        {
            uint64_t nowUs = NowMicros();
            if (nowUs >= targetUs) return;

            uint64_t remUs = targetUs - nowUs;
            if (remUs > 1000ULL)
            {
                uint64_t sleepUs = remUs - 800ULL;
                if (m_Timer)
                {
                    LARGE_INTEGER due{};
                    due.QuadPart = -static_cast<LONGLONG>(sleepUs * 10ULL);
                    if (SetWaitableTimer(m_Timer, &due, 0, nullptr, nullptr, FALSE))
                    {
                        WaitForSingleObject(m_Timer, static_cast<DWORD>(sleepUs / 1000ULL + 5ULL));
                    }
                }
                else
                {
                    DWORD ms = static_cast<DWORD>(sleepUs / 1000ULL);
                    if (ms > 0) Sleep(ms);
                }
            }

            while (NowMicros() < targetUs)
            {
                _mm_pause();
            }
        }

    private:
        HANDLE m_Timer = nullptr;
        bool m_PeriodActive = false;
    };

    bool g_Initialized = false;
    std::atomic<bool> g_ClientStopRequested{false};
    std::atomic<bool> g_ClientOverlayToggleRequested{false};

    void FfmpegLogBridge(void*, int level, const char* fmt, va_list vl)
    {
        if (level > AV_LOG_WARNING) return;
        char buf[1024];
        vsnprintf(buf, sizeof(buf), fmt, vl);
        size_t len = strlen(buf);
        while (len > 0 && (buf[len - 1] == '\n' || buf[len - 1] == '\r'))
        {
            buf[--len] = '\0';
        }
        if (len > 0)
        {
            OSM_LOG_WARN("[FFmpeg] %s", buf);
        }
    }

    std::string EscapeJson(const char* raw)
    {
        if (!raw) return {};
        std::string out;
        out.reserve(strlen(raw) + 8);
        for (const unsigned char* p = reinterpret_cast<const unsigned char*>(raw); *p; ++p)
        {
            char c = static_cast<char>(*p);
            if (c == '\\') out += "\\\\";
            else if (c == '"') out += "\\\"";
            else if (c == '\n') out += "\\n";
            else if (c == '\r') out += "\\r";
            else if (c == '\t') out += "\\t";
            else if (*p >= 0x20) out.push_back(c);
        }
        return out;
    }

    class ActionSeqDeduplicator
    {
    public:
        bool Accept(uint16_t seq)
        {
            if (seq == 0) return true;
            if (!m_HasSeq)
            {
                m_HasSeq = true;
                m_MaxSeq = seq;
                m_Window[0] = 1ULL;
                m_Window[1] = 0ULL;
                m_Window[2] = 0ULL;
                m_Window[3] = 0ULL;
                return true;
            }

            const int16_t diff = static_cast<int16_t>(seq - m_MaxSeq);
            if (diff == 0) return false;
            if (diff > 0)
            {
                const uint32_t shift = static_cast<uint32_t>(diff);
                if (shift >= 256U)
                {
                    m_Window[0] = 1ULL;
                    m_Window[1] = 0ULL;
                    m_Window[2] = 0ULL;
                    m_Window[3] = 0ULL;
                }
                else
                {
                    const uint32_t wordShift = shift / 64U;
                    const uint32_t bitShift = shift % 64U;
                    uint64_t next[4]{};
                    for (uint32_t i = 0; i < 4U; ++i)
                    {
                        if (i + wordShift < 4U)
                        {
                            next[i + wordShift] |= (m_Window[i] << bitShift);
                        }
                        if (bitShift > 0U && i + wordShift + 1U < 4U)
                        {
                            next[i + wordShift + 1U] |= (m_Window[i] >> (64U - bitShift));
                        }
                    }
                    memcpy(m_Window, next, sizeof(m_Window));
                    m_Window[0] |= 1ULL;
                }
                m_MaxSeq = seq;
                return true;
            }

            const uint32_t age = static_cast<uint32_t>(-static_cast<int32_t>(diff));
            if (age >= 256U) return false;
            const uint32_t wordIdx = age / 64U;
            const uint64_t mask = 1ULL << (age % 64U);
            if ((m_Window[wordIdx] & mask) != 0ULL) return false;
            m_Window[wordIdx] |= mask;
            return true;
        }

    private:
        bool m_HasSeq = false;
        uint16_t m_MaxSeq = 0;
        uint64_t m_Window[4]{};
    };

    struct HostVideoSettings
    {
        int32_t width = 0;
        int32_t height = 0;
        int32_t fps = 60;
        int32_t bitrateKbps = 20000;
        bool hdr = false;
        bool yuv444 = false;

        bool operator==(const HostVideoSettings&) const = default;
    };

    HostVideoSettings ApplyHostVideoLimits(const OsmiumHostConfig& config, HostVideoSettings settings)
    {
        const auto limitDimension = [](int32_t value, int32_t limit)
        {
            if (limit <= 0) return std::max(0, value);
            return value > 0 ? std::min(value, limit) : limit;
        };
        constexpr int32_t supportedMaxFps = 240;
        constexpr int32_t supportedMaxBitrateKbps = 500000;
        const int32_t maxFps = std::clamp(config.maxFps > 0 ? config.maxFps : supportedMaxFps, 10, supportedMaxFps);
        const int32_t maxBitrateKbps = std::clamp(
            config.maxBitrateKbps > 0 ? config.maxBitrateKbps : supportedMaxBitrateKbps, 500, supportedMaxBitrateKbps);

        settings.width = limitDimension(settings.width, config.maxWidth);
        settings.height = limitDimension(settings.height, config.maxHeight);
        settings.fps = std::clamp(settings.fps > 0 ? settings.fps : HostVideoSettings{}.fps, 10, maxFps);
        settings.bitrateKbps = std::clamp(settings.bitrateKbps > 0 ? settings.bitrateKbps : HostVideoSettings{}.bitrateKbps,
                                          500, maxBitrateKbps);
        return settings;
    }

    struct HostSessionContext
    {
        OsmiumTransport* transport = nullptr;
        OsmiumHostConfig config{};
        OsmiumClientConfig clientProfile{};
        OsmiumHostCallbacks callbacks{};

        HostCapture capture;
        HostPrivacyScreen privacyScreen;
        HostVideoEncoder encoder;
        HostAudioCapture audio;
        HostInputInjector injector;
        HostTextFocus textFocus;
        UsbIpVirtualDevices virtualUsb;
        HostSystemAgent systemAgent;
        ActionSeqDeduplicator actionDedup;

        std::mutex cfgMutex;
        int32_t activeDisplayIndex = 0;
        HostVideoSettings videoSettings;
        bool lastHostHdr = false;
        int32_t mouseMode = 0;
        bool hideHostCursorInHostMode = true;
        bool smartEngaged = false;
        bool virtualMouseRequested = false;
        bool virtualSpeakerRequested = false;
        bool virtualMicRequested = false;
        std::string lastActionMsg;
        std::wstring privacyWallpaperPath;

        std::atomic<bool> allowPrivacyScreen{false};
        std::atomic<bool> captureUsesDuplication{false};
        std::atomic<bool> reconfigNeeded{false};
        std::atomic<bool> statusDirty{true};
        std::atomic<bool> idrRecoveryPending{true};
        std::atomic<bool> stopFlag{false};

        std::atomic<uint32_t> frameIdCounter{1};
        std::atomic<uint32_t> audioSeqCounter{1};
        std::atomic<uint64_t> bytesSentWindow{0};
        std::atomic<uint32_t> framesEncodedWindow{0};
        std::atomic<uint32_t> totalEncodedFrames{0};
        std::atomic<uint32_t> droppedFrames{0};

        std::mutex sysActionMutex;
        std::condition_variable sysActionCv;
        std::deque<int32_t> sysActionQueue;

        std::thread videoThread;
        std::thread recvThread;
        std::thread sysActionThread;
    };

    void PushHostStatusJson(HostSessionContext* ctx)
    {
        if (!ctx || !ctx->transport || ctx->stopFlag.load()) return;

        ctx->capture.RefreshOutputHdrState();
        auto displays = HostCapture::EnumerateDisplays();
        int32_t activeIdx = 0;
        int32_t nativeW = ctx->capture.GetWidth();
        int32_t nativeH = ctx->capture.GetHeight();
        int32_t streamW = ctx->encoder.GetOutWidth();
        int32_t streamH = ctx->encoder.GetOutHeight();
        HostVideoSettings video;
        bool hostHdrEnabled = ctx->capture.IsOutputHdr();
        bool hdr = ctx->encoder.IsPqHdrActive();
        bool yuv444 = ctx->encoder.IsYuv444Active();
        int32_t mMode = 0;
        bool smartEng = false;
        bool vMouse = ctx->virtualUsb.IsVirtualMouseActive();
        bool vSpk = ctx->virtualUsb.IsVirtualSpeakerActive();
        bool vMic = ctx->virtualUsb.IsVirtualMicActive();
        bool locked = HostSystemAgent::IsSessionLocked();
        bool hasUsb = UsbIpVirtualDevices::IsDriverAvailable();
        std::string lastAct;

        {
            std::lock_guard<std::mutex> lock(ctx->cfgMutex);
            activeIdx = ctx->activeDisplayIndex;
            video = ctx->videoSettings;
            mMode = ctx->mouseMode;
            smartEng = ctx->smartEngaged;
            lastAct = ctx->lastActionMsg;
        }

        for (const auto& d : displays)
        {
            if (d.index == activeIdx)
            {
                nativeW = d.width;
                nativeH = d.height;
                break;
            }
        }

        std::ostringstream oss;
        oss << "{\"activeDisplayIndex\":" << activeIdx
            << ",\"nativeWidth\":" << nativeW
            << ",\"nativeHeight\":" << nativeH
            << ",\"streamWidth\":" << streamW
            << ",\"streamHeight\":" << streamH
            << ",\"fps\":" << video.fps
            << ",\"bitrateKbps\":" << video.bitrateKbps
            << ",\"hostHdrEnabled\":" << (hostHdrEnabled ? "true" : "false")
            << ",\"hdr\":" << (hdr ? "true" : "false")
            << ",\"yuv444\":" << (yuv444 ? "true" : "false")
            << ",\"mouseMode\":" << mMode
            << ",\"virtualMouseEngaged\":" << ((mMode == 1 || (mMode == 2 && smartEng)) ? "true" : "false")
            << ",\"virtualMouseHardware\":" << (vMouse ? "true" : "false")
            << ",\"smartFullscreenEngaged\":" << (smartEng ? "true" : "false")
            << ",\"virtualSpeakerActive\":" << (vSpk ? "true" : "false")
            << ",\"virtualMicActive\":" << (vMic ? "true" : "false")
            << ",\"screenLocked\":" << (locked ? "true" : "false")
            << ",\"hasUsbIpDriver\":" << (hasUsb ? "true" : "false")
            << ",\"allowRemoteDriverMgmt\":" << (ctx->config.allowRemoteDriverMgmt ? "true" : "false")
            << ",\"allowPrivacyScreen\":" << (ctx->allowPrivacyScreen.load() ? "true" : "false")
            << ",\"privacyScreenActive\":" << (ctx->privacyScreen.IsActive() ? "true" : "false")
            << ",\"touchInput\":" << (HostTouchInjector::IsSupported() ? "true" : "false")
            << ",\"lastAction\":\"" << EscapeJson(lastAct.c_str()) << "\""
            << ",\"displays\":[";

        size_t maxDisp = std::min<size_t>(displays.size(), 8);
        for (size_t i = 0; i < maxDisp; ++i)
        {
            const auto& d = displays[i];
            std::ostringstream item;
            if (i > 0) item << ",";
            item << "{\"index\":" << d.index
                 << ",\"width\":" << d.width
                 << ",\"height\":" << d.height
                 << ",\"refreshRate\":" << d.refreshRate
                 << ",\"isPrimary\":" << (d.isPrimary ? "true" : "false")
                 << ",\"name\":\"" << EscapeJson(d.name) << "\""
                 << ",\"deviceName\":\"" << EscapeJson(d.deviceName) << "\"}";
            std::string itemStr = item.str();
            if (oss.str().size() + itemStr.size() + 4 > proto::kMaxVideoChunkPayload)
            {
                break;
            }
            oss << itemStr;
        }
        oss << "]}";

        std::string json = oss.str();
        std::vector<uint8_t> pkt(sizeof(proto::AppPacketHeader) + json.size());
        memset(pkt.data(), 0, sizeof(proto::AppPacketHeader));
        memcpy(pkt.data() + sizeof(proto::AppPacketHeader), json.data(), json.size());
        ctx->transport->SendEncrypted(proto::AppPacketType::HostStatusJson, pkt.data(), pkt.size());
    }

    void ApplyHostMouseModeLocked(HostSessionContext* ctx)
    {
        bool engageVirtual = (ctx->mouseMode == 1) || (ctx->mouseMode == 2 && ctx->smartEngaged);
        ctx->virtualMouseRequested = engageVirtual;
        ctx->injector.SetVirtualMousePreferred(engageVirtual);

        bool shouldCaptureCursor = engageVirtual ? true : !ctx->hideHostCursorInHostMode;
        ctx->capture.SetCaptureCursor(shouldCaptureCursor);
    }

    void HostSysActionLoop(HostSessionContext* ctx)
    {
        while (!ctx->stopFlag.load())
        {
            int32_t action = 0;
            {
                std::unique_lock<std::mutex> lock(ctx->sysActionMutex);
                ctx->sysActionCv.wait_for(lock, std::chrono::milliseconds(150), [&]()
                {
                    return ctx->stopFlag.load() || !ctx->sysActionQueue.empty();
                });
                if (ctx->stopFlag.load()) break;
                if (ctx->sysActionQueue.empty()) continue;
                action = ctx->sysActionQueue.front();
                ctx->sysActionQueue.pop_front();
            }

            if (action == static_cast<int32_t>(proto::SystemActionId::EnablePrivacyScreen) ||
                action == static_cast<int32_t>(proto::SystemActionId::DisablePrivacyScreen))
            {
                if (!ctx->allowPrivacyScreen.load()) continue;
                std::string result;
                if (action == static_cast<int32_t>(proto::SystemActionId::DisablePrivacyScreen))
                {
                    ctx->privacyScreen.Hide();
                }
                else if (!ctx->privacyScreen.IsActive())
                {
                    std::wstring path;
                    {
                        std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                        path = ctx->privacyWallpaperPath;
                    }
                    if (!ctx->captureUsesDuplication.load() || !ctx->privacyScreen.Show(std::move(path), &ctx->stopFlag))
                    {
                        result = "PRIVACY_SCREEN_UNAVAILABLE";
                    }
                }
                std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                ctx->lastActionMsg = std::move(result);
                ctx->statusDirty.store(true);
            }
            else if (action == static_cast<int32_t>(proto::SystemActionId::SendCtrlAltDel))
            {
                bool ok = ctx->systemAgent.SendCtrlAltDel();
                std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                ctx->lastActionMsg = ok ? "SAS_SENT" : "SAS_NEEDS_ADMIN";
                ctx->statusDirty.store(true);
            }
            else if (action == static_cast<int32_t>(proto::SystemActionId::OpenTaskManager))
            {
                bool ok = ctx->systemAgent.OpenTaskManager();
                std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                ctx->lastActionMsg = ok ? "TASKMGR_OPENED" : "TASKMGR_FAILED";
                ctx->statusDirty.store(true);
            }
            else if (action == static_cast<int32_t>(proto::SystemActionId::InstallVirtualDrivers) ||
                action == static_cast<int32_t>(proto::SystemActionId::UninstallVirtualDrivers))
            {
                const bool isInstall = (action == static_cast<int32_t>(proto::SystemActionId::InstallVirtualDrivers));

                if (ctx->config.allowRemoteDriverMgmt == 0)
                {
                    std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                    ctx->lastActionMsg = "DRIVER_MGMT_DISABLED";
                    ctx->statusDirty.store(true);
                    continue;
                }

                if (ctx->config.requireLocalDriverConfirm != 0 && ctx->callbacks.onConfirmDriverAction)
                {
                    int32_t allowed = ctx->callbacks.onConfirmDriverAction(isInstall ? 1 : 2);
                    if (allowed == 0)
                    {
                        std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                        ctx->lastActionMsg = "DRIVER_MGMT_DENIED";
                        ctx->statusDirty.store(true);
                        continue;
                    }
                }

                if (!isInstall)
                {
                    ctx->virtualUsb.StopAll();
                    {
                        std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                        ctx->virtualMouseRequested = false;
                        ctx->virtualSpeakerRequested = false;
                        ctx->virtualMicRequested = false;
                    }
                }

                bool ok = false;
                if (DriverSetupHelper::IsProcessElevated())
                {
                    ok = isInstall
                             ? (Osmium_InstallVirtualDrivers() != 0)
                             : (Osmium_UninstallVirtualDrivers() != 0);
                }
                else
                {
                    ok = DriverSetupHelper::RunElevatedSelfDriverOp(isInstall, 60000);
                }

                std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                if (isInstall)
                {
                    ctx->lastActionMsg = ok ? "DRIVER_INSTALLED" : "DRIVER_INSTALL_FAILED";
                }
                else
                {
                    ctx->lastActionMsg = ok ? "DRIVER_UNINSTALLED" : "DRIVER_UNINSTALL_FAILED";
                }
                ctx->statusDirty.store(true);
            }
        }
        ctx->privacyScreen.Hide();
    }

    void HostVideoLoop(HostSessionContext* ctx)
    {
        ScopedHighResTimer hiResTimer;
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
        ctx->injector.SetVirtualDevices(&ctx->virtualUsb);
        ctx->audio.SetVirtualDevices(&ctx->virtualUsb);

        int32_t activeFps = 60;
        bool lastPrivacyActivity = false;
        bool allowGpuEncoderInput = true;
        auto reconfigurePipeline = [&](bool forceInitCapture) -> bool
        {
            int dispIdx = 0;
            bool capCursor = false;
            HostVideoSettings settings;
            {
                std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                dispIdx = ctx->activeDisplayIndex;
                settings = ctx->videoSettings;
                ApplyHostMouseModeLocked(ctx);
                capCursor = ctx->virtualMouseRequested || !ctx->hideHostCursorInHostMode;
            }

            if (forceInitCapture || dispIdx != ctx->capture.GetDisplayIndex())
            {
                if (!ctx->capture.Initialize(dispIdx, capCursor))
                {
                    if (dispIdx != 0 && ctx->capture.Initialize(0, capCursor))
                    {
                        std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                        ctx->activeDisplayIndex = 0;
                    }
                    else
                    {
                        return false;
                    }
                }
                ctx->injector.SetTargetDisplayRect(ctx->capture.GetDesktopRect());
                ctx->textFocus.SetDisplayRect(ctx->capture.GetDesktopRect());
            }

            ctx->injector.SetEnabled(ctx->config.allowInputControl != 0);
            ctx->capture.RefreshOutputHdrState();
            bool hostHdr = ctx->capture.IsOutputHdr();
            bool effectiveHdr = settings.hdr && hostHdr;
            ctx->lastHostHdr = hostHdr;

            int capW = ctx->capture.GetWidth();
            int capH = ctx->capture.GetHeight();

            int outW = capW;
            int outH = capH;
            if (settings.width > 0 && settings.height > 0 && (capW > settings.width || capH > settings.height))
            {
                double scale = std::min(1.0, std::min(static_cast<double>(settings.width) / capW,
                                                      static_cast<double>(settings.height) / capH));
                outW = std::max(256, static_cast<int>(capW * scale) & ~1);
                outH = std::max(144, static_cast<int>(capH * scale) & ~1);
            }

            int codecPref = ctx->clientProfile.codecPreference != 0
                                ? ctx->clientProfile.codecPreference
                                : ctx->config.codecPreference;
            auto* captureDevice = allowGpuEncoderInput && !capCursor && !hostHdr ? ctx->capture.GetDevice() : nullptr;
            if (!ctx->encoder.Initialize(capW, capH, outW, outH, settings.fps, settings.bitrateKbps, codecPref,
                                         ctx->config.encoderPreference, effectiveHdr, settings.yuv444, captureDevice))
            {
                return false;
            }
            const bool acceptsNv12 = ctx->encoder.AcceptsNv12();
            ctx->capture.SetNv12OutputSize(acceptsNv12 ? ctx->encoder.GetOutWidth() : 0,
                                           acceptsNv12 ? ctx->encoder.GetOutHeight() : 0,
                                           ctx->encoder.GetD3dInputDevice());
            activeFps = settings.fps;
            ctx->statusDirty.store(true);
            return true;
        };

        if (!reconfigurePipeline(true))
        {
            if (ctx->callbacks.onEnded)
            {
                ctx->callbacks.onEnded(-1, "Failed to initialize screen capture or video encoder");
            }
            return;
        }

        if (ctx->config.captureAudio)
        {
            ctx->audio.Start([ctx](const uint8_t* opusData, int bytes, int sampleCount)
            {
                if (ctx->stopFlag.load() || !ctx->transport || bytes <= 0 || bytes > 1400) return;
                uint8_t pkt[sizeof(proto::AudioFrameHeader) + 1400];
                auto* hdr = reinterpret_cast<proto::AudioFrameHeader*>(pkt);
                memset(hdr, 0, sizeof(*hdr));
                hdr->sequence = ctx->audioSeqCounter.fetch_add(1);
                hdr->sampleRate = 48000;
                hdr->channels = 2;
                hdr->sampleCount = static_cast<uint16_t>(sampleCount);
                hdr->payloadBytes = static_cast<uint16_t>(bytes);
                memcpy(pkt + sizeof(proto::AudioFrameHeader), opusData, static_cast<size_t>(bytes));
                ctx->transport->SendEncrypted(proto::AppPacketType::AudioFrame, pkt,
                                              sizeof(proto::AudioFrameHeader) + static_cast<size_t>(bytes));
            });
        }

        if (ctx->virtualMouseRequested)
        {
            ctx->virtualUsb.SetVirtualMouseEnabled(true);
        }

        uint64_t lastStatsMs = NowMillis();
        uint64_t lastMonitorTickMs = 0;
        uint64_t lastStatusPushMs = 0;
        uint64_t lastEncodedPacketMs = NowMillis();
        uint64_t lastEncoderRecoverMs = 0;
        uint32_t consecutiveEncodeErrors = 0;
        uint32_t consecutiveEmptyEncodes = 0;
        double avgEncodeMs = 0.0;
        struct StageTiming
        {
            uint64_t sumUs = 0;
            uint64_t maxUs = 0;
            uint32_t samples = 0;

            void Record(uint64_t elapsedUs)
            {
                sumUs += elapsedUs;
                maxUs = std::max(maxUs, elapsedUs);
                ++samples;
            }

            double MeanMs() const { return samples ? sumUs / (1000.0 * samples) : 0.0; }
            double MaxMs() const { return maxUs / 1000.0; }
        } captureTiming, encodeTiming, sendTiming;
        uint32_t freshCaptureCount = 0;
        uint64_t lastTimingMs = lastStatsMs;
        uint32_t lastTimingEncodedFrames = 0;
        CapturedFrame capFrame;
        std::vector<EncodedPacket> encodedPackets;
        std::vector<uint8_t> chunkBuf;

        PushHostStatusJson(ctx);

        while (!ctx->stopFlag.load())
        {
            uint64_t loopStartUs = NowMicros();

            if (ctx->reconfigNeeded.exchange(false))
            {
                reconfigurePipeline(false);
                consecutiveEncodeErrors = 0;
                consecutiveEmptyEncodes = 0;
                lastEncodedPacketMs = NowMillis();
            }

            bool wantVirtMouse = false;
            {
                std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                wantVirtMouse = ctx->virtualMouseRequested;
            }
            if (wantVirtMouse && !ctx->virtualUsb.IsVirtualMouseActive())
            {
                if (ctx->virtualUsb.SetVirtualMouseEnabled(true))
                {
                    ctx->statusDirty.store(true);
                }
            }
            else if (!wantVirtMouse && ctx->virtualUsb.IsVirtualMouseActive())
            {
                ctx->virtualUsb.SetVirtualMouseEnabled(false);
                ctx->statusDirty.store(true);
            }

            uint64_t nowMs = NowMillis();
            if (nowMs - lastMonitorTickMs >= 150)
            {
                lastMonitorTickMs = nowMs;
                ctx->capture.RefreshOutputHdrState();
                bool curHostHdr = ctx->capture.IsOutputHdr();
                if (curHostHdr != ctx->lastHostHdr)
                {
                    ctx->lastHostHdr = curHostHdr;
                    ctx->reconfigNeeded.store(true);
                    ctx->statusDirty.store(true);
                }

                int32_t mMode = 0;
                bool curSmart = false;
                {
                    std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                    mMode = ctx->mouseMode;
                    curSmart = ctx->smartEngaged;
                }
                if (mMode == 2)
                {
                    bool nextSmart = HostSystemAgent::IsSmartFullscreenOrCursorLocked(ctx->capture.GetDesktopRect());
                    if (nextSmart != curSmart)
                    {
                        std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                        ctx->smartEngaged = nextSmart;
                        ApplyHostMouseModeLocked(ctx);
                        ctx->statusDirty.store(true);
                    }
                }
            }

            const bool privacyActive = ctx->privacyScreen.IsActive();
            if (ctx->statusDirty.exchange(false) || privacyActive != lastPrivacyActivity ||
                (nowMs - lastStatusPushMs >= 2000))
            {
                lastPrivacyActivity = privacyActive;
                lastStatusPushMs = nowMs;
                PushHostStatusJson(ctx);
            }

            const uint64_t frameIntervalUs = 1000000ULL / static_cast<uint64_t>(activeFps);
            const bool wantPqCapture = ctx->encoder.IsPqHdrActive();
            const int initialCaptureWaitMs = std::clamp<int>(static_cast<int>(frameIntervalUs / 1000ULL), 4, 16);

            const uint64_t captureStartUs = NowMicros();
            const bool captured = ctx->capture.CaptureNextFrame(capFrame, initialCaptureWaitMs, wantPqCapture,
                                                                &ctx->privacyScreen.ActiveFlag());
            captureTiming.Record(NowMicros() - captureStartUs);
            if (captured && capFrame.updated) ++freshCaptureCount;
            ctx->captureUsesDuplication.store(ctx->capture.HasDesktopDuplication());
            if (captured)
            {
                bool curHostHdr = ctx->capture.IsOutputHdr();
                if (curHostHdr != ctx->lastHostHdr)
                {
                    ctx->lastHostHdr = curHostHdr;
                    ctx->statusDirty.store(true);
                    reconfigurePipeline(false);
                }
                if (ctx->encoder.IsPqHdrActive() != capFrame.isHdr10Pq && capFrame.updated)
                {
                    reconfigurePipeline(false);
                }

                ctx->injector.SetTargetDisplayRect(ctx->capture.GetDesktopRect());
                ctx->textFocus.SetDisplayRect(ctx->capture.GetDesktopRect());
                const int planeRows = capFrame.height + (capFrame.isNv12 ? capFrame.height / 2 : 0);
                if (capFrame.width <= 0 || capFrame.height <= 0 || capFrame.stride <= 0 ||
                    (!capFrame.nv12Texture && capFrame.pixels.size() < static_cast<size_t>(capFrame.stride) * planeRows))
                {
                    continue;
                }
                const int captureWidth = ctx->capture.GetWidth();
                const int captureHeight = ctx->capture.GetHeight();
                if (captureWidth != ctx->encoder.GetSrcWidth() || captureHeight != ctx->encoder.GetSrcHeight())
                {
                    OSM_LOG_INFO("Host capture geometry changed (%dx%d -> %dx%d); reconfiguring encoder",
                                 ctx->encoder.GetSrcWidth(), ctx->encoder.GetSrcHeight(), captureWidth, captureHeight);
                    if (!reconfigurePipeline(false))
                    {
                        Sleep(50);
                        continue;
                    }
                }
                const int expectedWidth = capFrame.isNv12 ? ctx->encoder.GetOutWidth() : ctx->encoder.GetSrcWidth();
                const int expectedHeight = capFrame.isNv12 ? ctx->encoder.GetOutHeight() : ctx->encoder.GetSrcHeight();
                if (capFrame.width != expectedWidth || capFrame.height != expectedHeight ||
                    (ctx->encoder.IsPqHdrActive() != capFrame.isHdr10Pq) ||
                    (capFrame.isNv12 && !ctx->encoder.AcceptsNv12()) ||
                    (capFrame.nv12Texture && !ctx->encoder.GetD3dInputDevice()))
                {
                    continue;
                }

                uint64_t encStartUs = NowMicros();
                const auto inputFormat = capFrame.isNv12 ? AV_PIX_FMT_NV12 :
                    (capFrame.isHdr10Pq ? AV_PIX_FMT_X2BGR10LE : AV_PIX_FMT_BGRA);
                const bool encoded = ctx->encoder.EncodeFrame(capFrame.pixels.data(), capFrame.stride,
                    inputFormat, encodedPackets, capFrame.pixelRevision, capFrame.nv12Texture.Get());
                const uint64_t sendStartUs = NowMicros();
                encodeTiming.Record(sendStartUs - encStartUs);
                if (encoded)
                {
                    consecutiveEncodeErrors = 0;
                    double encMs = static_cast<double>(sendStartUs - encStartUs) / 1000.0;
                    avgEncodeMs = (avgEncodeMs * 0.85) + (encMs * 0.15);

                    uint8_t cFlags = 0;
                    if (ctx->encoder.IsPqHdrActive() && capFrame.isHdr10Pq) cFlags |= proto::kVideoFlagHdr;
                    if (ctx->encoder.IsYuv444Active()) cFlags |= proto::kVideoFlagYuv444;

                    const int32_t routeNow = ctx->transport->GetTransportRoute();
                    const bool isRelayRoute = (routeNow == static_cast<int32_t>(TransportRoute::CloudRelay));
                    const bool isLanRoute = (routeNow == static_cast<int32_t>(TransportRoute::LanIPv4));
                    const size_t maxChunkPayload = isRelayRoute
                                                       ? proto::kMaxRelayVideoChunkPayload
                                                       : (isLanRoute
                                                              ? proto::kMaxLanVideoChunkPayload
                                                              : proto::kMaxWanVideoChunkPayload);

                    bool sentAnyPacket = false;
                    for (const auto& ep : encodedPackets)
                    {
                        if (ep.data.empty()) continue;
                        uint32_t frameId = ctx->frameIdCounter.fetch_add(1);
                        size_t totalChunks = (ep.data.size() + maxChunkPayload - 1) / maxChunkPayload;
                        if (totalChunks > 65535) continue;

                        const bool paceUdpBurst = (!isRelayRoute && totalChunks > 12);
                        const bool sendKeyRedundantPass = (!isRelayRoute && ep.isKeyFrame && totalChunks <= 48 &&
                            ctx->idrRecoveryPending.exchange(false, std::memory_order_acq_rel));
                        const int passCount = sendKeyRedundantPass ? 2 : 1;

                        for (int pass = 0; pass < passCount; ++pass)
                        {
                            uint64_t nextPaceUs = paceUdpBurst ? NowMicros() : 0;
                            for (size_t ci = 0; ci < totalChunks; ++ci)
                            {
                                size_t offset = ci * maxChunkPayload;
                                size_t sliceLen = std::min(maxChunkPayload, ep.data.size() - offset);

                                chunkBuf.resize(sizeof(proto::VideoChunkHeader) + sliceLen);
                                auto* hdr = reinterpret_cast<proto::VideoChunkHeader*>(chunkBuf.data());
                                memset(hdr, 0, sizeof(*hdr));
                                hdr->frameId = frameId;
                                hdr->chunkIndex = static_cast<uint16_t>(ci);
                                hdr->totalChunks = static_cast<uint16_t>(totalChunks);
                                hdr->codecId = static_cast<uint8_t>(ep.codecId);
                                hdr->isKeyFrame = ep.isKeyFrame ? 1 : 0;
                                hdr->width = static_cast<uint16_t>(ctx->encoder.GetOutWidth());
                                hdr->height = static_cast<uint16_t>(ctx->encoder.GetOutHeight());
                                hdr->colorFlags = cFlags;
                                hdr->captureTimeUs = capFrame.captureTimeUs;
                                memcpy(chunkBuf.data() + sizeof(proto::VideoChunkHeader), ep.data.data() + offset,
                                       sliceLen);

                                ctx->transport->SendEncrypted(proto::AppPacketType::VideoChunk, chunkBuf.data(),
                                                              chunkBuf.size());
                                ctx->bytesSentWindow.fetch_add(chunkBuf.size());
                                if (paceUdpBurst && (ci % 6) == 5 && (ci + 1) < totalChunks)
                                {
                                    nextPaceUs = std::max(nextPaceUs + 120ULL, NowMicros() + 80ULL);
                                    while (NowMicros() < nextPaceUs)
                                    {
                                        _mm_pause();
                                    }
                                }
                            }
                            if (pass + 1 < passCount)
                            {
                                uint64_t gapUntil = NowMicros() + 300ULL;
                                while (NowMicros() < gapUntil)
                                {
                                    _mm_pause();
                                }
                            }
                        }

                        ctx->framesEncodedWindow.fetch_add(1);
                        ctx->totalEncodedFrames.fetch_add(1);
                        sentAnyPacket = true;
                    }

                    if (sentAnyPacket)
                    {
                        sendTiming.Record(NowMicros() - sendStartUs);
                        consecutiveEmptyEncodes = 0;
                        lastEncodedPacketMs = NowMillis();
                    }
                    else
                    {
                        consecutiveEmptyEncodes++;
                    }
                }
                else
                {
                    ctx->droppedFrames.fetch_add(1);
                    consecutiveEncodeErrors++;
                }

                uint64_t encNowMs = NowMillis();
                bool encoderStalled = consecutiveEncodeErrors >= 8 ||
                    (consecutiveEmptyEncodes >= 15 && (encNowMs - lastEncodedPacketMs) >= 1000);
                if (encoderStalled && (encNowMs - lastEncoderRecoverMs) >= 1500)
                {
                    lastEncoderRecoverMs = encNowMs;
                    std::string stalledEnc = ctx->encoder.GetEncoderName();
                    OSM_LOG_WARN(
                        "Host video encoder %s stalled/failed (errors=%u, empty=%u); recovering pipeline",
                        stalledEnc.c_str(), consecutiveEncodeErrors, consecutiveEmptyEncodes);
                    if (ctx->encoder.GetD3dInputDevice())
                    {
                        allowGpuEncoderInput = false;
                    }
                    else if (ctx->encoder.IsHardwareEncoder() && ctx->config.encoderPreference != 1 && !stalledEnc.empty())
                    {
                        ctx->encoder.ExcludeEncoderForSession(stalledEnc);
                    }
                    consecutiveEncodeErrors = 0;
                    consecutiveEmptyEncodes = 0;
                    lastEncodedPacketMs = encNowMs;
                    reconfigurePipeline(false);
                }
            }

            nowMs = NowMillis();
            if (nowMs - lastTimingMs >= 5000)
            {
                const double seconds = (nowMs - lastTimingMs) / 1000.0;
                const uint32_t encoded = ctx->totalEncodedFrames.load();
                OSM_LOG_INFO("Host video timing (%s): target=%d fps, fresh=%.1f fps, encoded=%.1f fps, "
                             "capture=%.2f/%.2f ms, convert+encode=%.2f/%.2f ms, send=%.2f/%.2f ms (avg/max)",
                             ctx->encoder.GetEncoderName().c_str(), activeFps, freshCaptureCount / seconds,
                             (encoded - lastTimingEncodedFrames) / seconds, captureTiming.MeanMs(),
                             captureTiming.MaxMs(), encodeTiming.MeanMs(), encodeTiming.MaxMs(),
                             sendTiming.MeanMs(), sendTiming.MaxMs());
                lastTimingMs = nowMs;
                lastTimingEncodedFrames = encoded;
                freshCaptureCount = 0;
                captureTiming = {};
                encodeTiming = {};
                sendTiming = {};
            }
            if (nowMs - lastStatsMs >= 1000)
            {
                double elapsedSec = static_cast<double>(nowMs - lastStatsMs) / 1000.0;
                lastStatsMs = nowMs;

                uint32_t fWin = ctx->framesEncodedWindow.exchange(0);
                uint64_t bWin = ctx->bytesSentWindow.exchange(0);

                if (ctx->callbacks.onStatsUpdated)
                {
                    OsmiumHostStats st{};
                    st.encodedFrames = ctx->totalEncodedFrames.load();
                    st.droppedFrames = ctx->droppedFrames.load();
                    st.fps = static_cast<double>(fWin) / elapsedSec;
                    st.bitrateMbps = (static_cast<double>(bWin) * 8.0) / (elapsedSec * 1000000.0);
                    st.encodeLatencyMs = avgEncodeMs;
                    st.rttMs = ctx->transport->GetLastRttMs();
                    st.captureWidth = ctx->encoder.GetOutWidth();
                    st.captureHeight = ctx->encoder.GetOutHeight();
                    st.transportRoute = ctx->transport->GetTransportRoute();
                    CopyStringSafe(st.codecName, sizeof(st.codecName), ctx->encoder.GetCodecName());
                    CopyStringSafe(st.encoderName, sizeof(st.encoderName), ctx->encoder.GetEncoderName());
                    CopyStringSafe(st.peerEndpoint, sizeof(st.peerEndpoint), ctx->transport->GetActiveEndpointString());
                    ctx->callbacks.onStatsUpdated(&st);
                }
            }

            hiResTimer.WaitUntilUs(loopStartUs + frameIntervalUs);
        }

        ctx->injector.ReleaseAllInputs();
        ctx->audio.Stop();
        ctx->encoder.Shutdown();
        ctx->capture.Shutdown();
        ctx->virtualUsb.StopAll();
        ctx->systemAgent.ResetOnDisconnect();
    }

    void HostRecvLoop(HostSessionContext* ctx)
    {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
        std::vector<uint8_t> plain;
        uint64_t lastPeerRxMs = NowMillis();
        constexpr uint64_t kClientKeepaliveTimeoutMs = 8000;

        while (!ctx->stopFlag.load())
        {
            int r = ctx->transport->ReceiveEncrypted(plain, 10);
            if (r < 0)
            {
                if (!ctx->stopFlag.load() && ctx->callbacks.onEnded)
                {
                    ctx->callbacks.onEnded(-1, "Transport closed");
                }
                break;
            }
            if (r == 0 || plain.size() < sizeof(proto::AppPacketHeader))
            {
                if (NowMillis() - lastPeerRxMs >= kClientKeepaliveTimeoutMs)
                {
                    OSM_LOG_WARN("Client keepalive timeout (%llu ms); ending host session", kClientKeepaliveTimeoutMs);
                    ctx->stopFlag.store(true);
                    if (ctx->callbacks.onEnded)
                    {
                        ctx->callbacks.onEnded(-2, "Client connection timed out");
                    }
                    break;
                }
                continue;
            }

            lastPeerRxMs = NowMillis();
            const auto* appHdr = reinterpret_cast<const proto::AppPacketHeader*>(plain.data());
            if ((appHdr->flags & proto::kAppFlagHasActionSeq) != 0 && appHdr->reserved != 0)
            {
                if (!ctx->actionDedup.Accept(appHdr->reserved))
                {
                    continue;
                }
            }

            auto type = static_cast<proto::AppPacketType>(plain[0]);
            if (type == proto::AppPacketType::VideoIdrRequest)
            {
                ctx->idrRecoveryPending.store(true, std::memory_order_release);
                ctx->encoder.ForceKeyFrame();
            }
            else if (type == proto::AppPacketType::Ping && plain.size() >= sizeof(proto::PingPongPayload))
            {
                auto pong = *reinterpret_cast<const proto::PingPongPayload*>(plain.data());
                ctx->transport->SendEncrypted(proto::AppPacketType::Pong, &pong, sizeof(pong));
            }
            else if (type == proto::AppPacketType::AuthRequest)
            {
                proto::AuthResponsePayload resp{};
                resp.accepted = 1;
                resp.errorCode = 0;
                CopyStringSafe(resp.message, sizeof(resp.message), "Authenticated");
                ctx->transport->SendEncrypted(proto::AppPacketType::AuthResponse, &resp, sizeof(resp));
            }
            else if (type == proto::AppPacketType::ClientMicAudioFrame && plain.size() >= sizeof(
                proto::AudioFrameHeader))
            {
                const auto* hdr = reinterpret_cast<const proto::AudioFrameHeader*>(plain.data());
                if (plain.size() >= sizeof(proto::AudioFrameHeader) + hdr->payloadBytes)
                {
                    ctx->audio.PushClientMicOpus(
                        plain.data() + sizeof(proto::AudioFrameHeader),
                        hdr->payloadBytes,
                        hdr->sampleRate,
                        hdr->channels,
                        hdr->sampleCount);
                }
            }
            else if (type == proto::AppPacketType::ControlSwitchDisplay && plain.size() >= sizeof(
                proto::ControlSwitchDisplayPayload))
            {
                const auto* p = reinterpret_cast<const proto::ControlSwitchDisplayPayload*>(plain.data());
                bool changed = false;
                {
                    std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                    if (ctx->activeDisplayIndex != p->displayIndex)
                    {
                        ctx->activeDisplayIndex = p->displayIndex;
                        changed = true;
                    }
                }
                if (changed)
                {
                    ctx->reconfigNeeded.store(true);
                }
                ctx->statusDirty.store(true);
            }
            else if (type == proto::AppPacketType::ControlVideoParams && plain.size() >= sizeof(
                proto::ControlVideoParamsPayload))
            {
                const auto* p = reinterpret_cast<const proto::ControlVideoParamsPayload*>(plain.data());
                std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                HostVideoSettings settings = ctx->videoSettings;
                if (p->targetWidth >= 0) settings.width = p->targetWidth;
                if (p->targetHeight >= 0) settings.height = p->targetHeight;
                if (p->fps > 0) settings.fps = p->fps;
                if (p->bitrateKbps > 0) settings.bitrateKbps = p->bitrateKbps;
                settings.hdr = (p->enableHdr != 0);
                settings.yuv444 = (p->enableYuv444 != 0);
                settings = ApplyHostVideoLimits(ctx->config, settings);

                if (settings != ctx->videoSettings)
                {
                    ctx->videoSettings = settings;
                    ctx->reconfigNeeded.store(true);
                }
            }
            else if (type == proto::AppPacketType::ControlMouseMode && plain.size() >= sizeof(
                proto::ControlMouseModePayload))
            {
                const auto* p = reinterpret_cast<const proto::ControlMouseModePayload*>(plain.data());
                {
                    std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                    ctx->mouseMode = std::clamp(p->mouseMode, 0, 2);
                    ctx->hideHostCursorInHostMode = (p->hideHostCursor != 0);
                    ApplyHostMouseModeLocked(ctx);
                }
                ctx->statusDirty.store(true);
            }
            else if (type == proto::AppPacketType::ControlVirtualAudio && plain.size() >= sizeof(
                proto::ControlVirtualAudioPayload))
            {
                const auto* p = reinterpret_cast<const proto::ControlVirtualAudioPayload*>(plain.data());
                bool spkOk = ctx->virtualUsb.SetVirtualSpeakerEnabled(p->virtualSpeakerEnabled != 0);
                bool micOk = ctx->virtualUsb.SetVirtualMicEnabled(p->virtualMicEnabled != 0);
                {
                    std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                    if ((p->virtualSpeakerEnabled && !spkOk) || (p->virtualMicEnabled && !micOk))
                    {
                        ctx->lastActionMsg = "AUDIO_DRIVER_UNAVAILABLE";
                    }
                    else
                    {
                        ctx->lastActionMsg = "AUDIO_UPDATED";
                    }
                }
                ctx->statusDirty.store(true);
            }
            else if (type == proto::AppPacketType::ControlSystemAction && plain.size() >= sizeof(
                proto::ControlSystemActionPayload))
            {
                const auto* p = reinterpret_cast<const proto::ControlSystemActionPayload*>(plain.data());
                {
                    std::lock_guard<std::mutex> sl(ctx->sysActionMutex);
                    ctx->sysActionQueue.push_back(p->action);
                }
                ctx->sysActionCv.notify_one();
            }
            else if (type == proto::AppPacketType::ControlTextFocus && plain.size() >= sizeof(
                proto::ControlTextFocusPayload))
            {
                const auto* p = reinterpret_cast<const proto::ControlTextFocusPayload*>(plain.data());
                if (p->enabled != 0)
                {
                    ctx->textFocus.Start([ctx](const HostTextFocus::State& s)
                    {
                        if (ctx->stopFlag.load() || !ctx->transport) return;
                        proto::HostTextFocusPayload pkt{};
                        pkt.active = s.active ? 1 : 0;
                        pkt.hasRect = s.hasRect ? 1 : 0;
                        pkt.serial = s.serial;
                        pkt.x = s.x;
                        pkt.y = s.y;
                        pkt.w = s.w;
                        pkt.h = s.h;
                        ctx->transport->SendEncrypted(proto::AppPacketType::HostTextFocus, &pkt, sizeof(pkt));
                    });
                }
                else
                {
                    ctx->textFocus.Stop();
                }
            }
            else if (type == proto::AppPacketType::AuxData && plain.size() > sizeof(proto::AppPacketHeader))
            {
                if (ctx->callbacks.onAuxData)
                {
                    ctx->callbacks.onAuxData(
                        plain.data() + sizeof(proto::AppPacketHeader),
                        static_cast<int32_t>(plain.size() - sizeof(proto::AppPacketHeader)));
                }
            }
            else if (type == proto::AppPacketType::Disconnect)
            {
                ctx->stopFlag.store(true);
                if (ctx->callbacks.onEnded)
                {
                    ctx->callbacks.onEnded(0, "Peer disconnected");
                }
                break;
            }
            else
            {
                ctx->injector.InjectPacket(plain.data(), plain.size());
            }
        }
    }

    struct AssembledVideoFrame
    {
        uint32_t frameId = 0;
        proto::VideoCodecId codecId = proto::VideoCodecId::H264;
        bool isKeyFrame = false;
        int width = 0;
        int height = 0;
        uint8_t colorFlags = 0;
        std::vector<uint8_t> payload;
    };

    struct PendingFrameChunks
    {
        proto::VideoCodecId codecId = proto::VideoCodecId::H264;
        bool isKeyFrame = false;
        int width = 0;
        int height = 0;
        uint8_t colorFlags = 0;
        uint16_t totalChunks = 0;
        uint16_t receivedCount = 0;
        uint32_t chunkStride = 0;
        uint32_t lastChunkSize = 0;
        uint64_t createdMs = 0;
        std::vector<uint8_t> receivedMask;
        std::vector<uint8_t> flatBuf;
        std::vector<uint8_t> lastChunkBuf;
    };

    using SessionVideoDecodeGate = osmium::VideoDecodeGate<AssembledVideoFrame>;

    bool TryAssembleVideoChunk(
        std::map<uint32_t, PendingFrameChunks>& pending,
        uint32_t& lastCompletedKeyFrameId,
        const proto::VideoChunkHeader* hdr,
        const uint8_t* dataPtr,
        size_t dataBytes,
        uint64_t nowMs,
        AssembledVideoFrame& outFrame)
    {
        if (hdr->totalChunks == 0 || hdr->chunkIndex >= hdr->totalChunks)
        {
            return false;
        }
        const bool isKey = hdr->isKeyFrame != 0;
        // Only keyframes are sent twice (redundant IDR pass). Suppress an
        // already-completed keyframe; keep inter frames that finish behind a
        // higher id so the decode gate can put them back in order instead of
        // turning packet reordering into a permanent gap.
        if (isKey && hdr->frameId <= lastCompletedKeyFrameId)
        {
            return false;
        }

        if (hdr->totalChunks == 1)
        {
            outFrame.frameId = hdr->frameId;
            outFrame.codecId = static_cast<proto::VideoCodecId>(hdr->codecId);
            outFrame.isKeyFrame = isKey;
            outFrame.width = hdr->width;
            outFrame.height = hdr->height;
            outFrame.colorFlags = hdr->colorFlags;
            outFrame.payload.assign(dataPtr, dataPtr + dataBytes);

            if (isKey) lastCompletedKeyFrameId = hdr->frameId;
            return true;
        }

        auto& pf = pending[hdr->frameId];
        if (pf.receivedMask.empty())
        {
            pf.codecId = static_cast<proto::VideoCodecId>(hdr->codecId);
            pf.isKeyFrame = hdr->isKeyFrame != 0;
            pf.width = hdr->width;
            pf.height = hdr->height;
            pf.colorFlags = hdr->colorFlags;
            pf.totalChunks = hdr->totalChunks;
            pf.receivedMask.assign(hdr->totalChunks, 0);
            pf.createdMs = nowMs;
        }

        if (pf.receivedMask[hdr->chunkIndex] != 0)
        {
            return false;
        }

        const bool isLastChunk = (hdr->chunkIndex + 1 == pf.totalChunks);
        if (!isLastChunk)
        {
            if (pf.chunkStride == 0)
            {
                if (dataBytes == 0 || dataBytes > 32768) return false;
                pf.chunkStride = static_cast<uint32_t>(dataBytes);
                pf.flatBuf.resize(static_cast<size_t>(pf.totalChunks) * pf.chunkStride);
                if (!pf.lastChunkBuf.empty())
                {
                    const size_t lastOff = static_cast<size_t>(pf.totalChunks - 1) * pf.chunkStride;
                    if (pf.lastChunkBuf.size() > pf.chunkStride)
                    {
                        pf.flatBuf.resize(lastOff + pf.lastChunkBuf.size());
                    }
                    memcpy(pf.flatBuf.data() + lastOff, pf.lastChunkBuf.data(), pf.lastChunkBuf.size());
                    pf.lastChunkSize = static_cast<uint32_t>(pf.lastChunkBuf.size());
                    pf.lastChunkBuf.clear();
                    pf.lastChunkBuf.shrink_to_fit();
                }
            }
            else if (dataBytes != pf.chunkStride)
            {
                return false;
            }

            const size_t offset = static_cast<size_t>(hdr->chunkIndex) * pf.chunkStride;
            memcpy(pf.flatBuf.data() + offset, dataPtr, dataBytes);
        }
        else
        {
            if (pf.chunkStride == 0)
            {
                pf.lastChunkBuf.assign(dataPtr, dataPtr + dataBytes);
                pf.lastChunkSize = static_cast<uint32_t>(dataBytes);
            }
            else
            {
                const size_t lastOff = static_cast<size_t>(pf.totalChunks - 1) * pf.chunkStride;
                if (dataBytes > pf.chunkStride)
                {
                    pf.flatBuf.resize(lastOff + dataBytes);
                }
                memcpy(pf.flatBuf.data() + lastOff, dataPtr, dataBytes);
                pf.lastChunkSize = static_cast<uint32_t>(dataBytes);
            }
        }

        pf.receivedMask[hdr->chunkIndex] = 1;
        pf.receivedCount++;

        if (pf.receivedCount != pf.totalChunks)
        {
            return false;
        }

        const size_t finalSize = static_cast<size_t>(pf.totalChunks - 1) * pf.chunkStride + pf.lastChunkSize;
        pf.flatBuf.resize(finalSize);

        outFrame.frameId = hdr->frameId;
        outFrame.codecId = pf.codecId;
        outFrame.isKeyFrame = pf.isKeyFrame;
        outFrame.width = pf.width;
        outFrame.height = pf.height;
        outFrame.colorFlags = pf.colorFlags;
        outFrame.payload = std::move(pf.flatBuf);

        if (outFrame.isKeyFrame) lastCompletedKeyFrameId = hdr->frameId;
        pending.erase(hdr->frameId);
        return true;
    }

    struct PendingUdpAction
    {
        proto::AppPacketType type = proto::AppPacketType::InputKey;
        std::vector<uint8_t> payload;
        uint64_t nextSendUs = 0;
        int remaining = 0;
    };

    struct EmbeddedClientContext
    {
        OsmiumTransport* transport = nullptr;
        OsmiumClientConfig config{};
        OsmiumClientCallbacks callbacks{};

        ClientVideoDecoder decoder;
        ClientAudioPlayer audioPlayer;
        ClientMicCapture micCapture;

        std::atomic<bool> stopFlag{false};
        std::atomic<bool> remoteEnded{false};

        std::mutex queueMutex;
        std::condition_variable queueCv;
        std::deque<AssembledVideoFrame> frameQueue;
        SessionVideoDecodeGate decodeGate;

        std::atomic<uint32_t> actionSeqCounter{1};
        std::mutex retxMutex;
        std::deque<PendingUdpAction> udpRetxQueue;
        std::atomic<bool> hasLastAbsMouse{false};
        std::atomic<float> lastAbsMouseX{0.5f};
        std::atomic<float> lastAbsMouseY{0.5f};

        std::atomic<uint32_t> receivedFrames{0};
        std::atomic<uint32_t> droppedFrames{0};
        std::atomic<uint64_t> bytesRecvWindow{0};
        std::atomic<uint32_t> micSeqCounter{1};

        std::atomic<int32_t> nativeWidth{0};
        std::atomic<int32_t> nativeHeight{0};
        std::atomic<int32_t> activeDisplayIndex{0};
        std::atomic<int32_t> mouseMode{0};
        std::atomic<int32_t> virtualMouseEngaged{0};
        std::atomic<int32_t> enableHdr{0};
        std::atomic<int32_t> enableYuv444{0};

        SDL_Gamepad* gamepads[4]{};
        SDL_JoystickID gamepadIds[4]{};

        std::thread recvThread;
        std::thread renderThread;
    };

    void ParseStatusHintFields(EmbeddedClientContext* c, const std::string& json)
    {
        auto extractInt = [&](const char* key, int32_t& outVal)
        {
            std::string pat = std::string("\"") + key + "\":";
            size_t p = json.find(pat);
            if (p == std::string::npos) return;
            p += pat.size();
            outVal = atoi(json.c_str() + p);
        };
        auto extractBool = [&](const char* key, int32_t& outVal)
        {
            std::string pat = std::string("\"") + key + "\":";
            size_t p = json.find(pat);
            if (p == std::string::npos) return;
            p += pat.size();
            outVal = (json.compare(p, 4, "true") == 0) ? 1 : 0;
        };

        int32_t nw = 0, nh = 0, ad = 0, mm = 0, vm = 0, hdr = 0, y444 = 0;
        extractInt("nativeWidth", nw);
        extractInt("nativeHeight", nh);
        extractInt("activeDisplayIndex", ad);
        extractInt("mouseMode", mm);
        extractBool("virtualMouseEngaged", vm);
        extractBool("hdr", hdr);
        extractBool("yuv444", y444);

        if (nw > 0) c->nativeWidth.store(nw);
        if (nh > 0) c->nativeHeight.store(nh);
        c->activeDisplayIndex.store(ad);
        c->mouseMode.store(mm);
        c->virtualMouseEngaged.store(vm);
        c->enableHdr.store(hdr);
        c->enableYuv444.store(y444);
    }

    void SendReliableIdrRequest(OsmiumTransport* transport)
    {
        if (!transport) return;
        proto::AppPacketHeader idrReq{};
        transport->SendEncrypted(proto::AppPacketType::VideoIdrRequest, &idrReq, sizeof(idrReq));
        if (!transport->IsKcpActive() &&
            transport->GetTransportRoute() != static_cast<int32_t>(TransportRoute::CloudRelay))
        {
            transport->SendEncrypted(proto::AppPacketType::VideoIdrRequest, &idrReq, sizeof(idrReq));
        }
    }

    void EmbeddedClientRecvLoop(EmbeddedClientContext* ctx)
    {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
        std::map<uint32_t, PendingFrameChunks> pending;
        uint32_t lastCompletedKeyFrameId = 0;
        uint64_t lastStalePurgeMs = NowMillis();
        std::vector<uint8_t> plain;

        while (!ctx->stopFlag.load())
        {
            int r = ctx->transport->ReceiveEncrypted(plain, 10);
            if (r < 0)
            {
                ctx->remoteEnded.store(true);
                ctx->queueCv.notify_all();
                break;
            }
            uint64_t nowMs = NowMillis();
            if (!pending.empty() && (nowMs - lastStalePurgeMs) >= 40)
            {
                lastStalePurgeMs = nowMs;
                const uint64_t rtt = ctx->transport->GetLastRttMs();
                const uint64_t staleTimeoutMs = std::clamp<uint64_t>(rtt * 3ULL + 80ULL, 120ULL, 450ULL);
                bool purged = false;
                for (auto it = pending.begin(); it != pending.end();)
                {
                    if (nowMs - it->second.createdMs > staleTimeoutMs)
                    {
                        it = pending.erase(it);
                        purged = true;
                    }
                    else
                    {
                        ++it;
                    }
                }
                if (purged)
                {
                    ctx->droppedFrames.fetch_add(1);
                }
            }
            if (r == 0 || plain.size() < sizeof(proto::AppPacketHeader)) continue;

            ctx->bytesRecvWindow.fetch_add(plain.size());
            auto type = static_cast<proto::AppPacketType>(plain[0]);

            if (type == proto::AppPacketType::VideoChunk && plain.size() > sizeof(proto::VideoChunkHeader))
            {
                const auto* hdr = reinterpret_cast<const proto::VideoChunkHeader*>(plain.data());
                AssembledVideoFrame frame;
                if (TryAssembleVideoChunk(
                        pending,
                        lastCompletedKeyFrameId,
                        hdr,
                        plain.data() + sizeof(proto::VideoChunkHeader),
                        plain.size() - sizeof(proto::VideoChunkHeader),
                        nowMs,
                        frame))
                {
                    ctx->receivedFrames.fetch_add(1);
                    uint32_t queueDropped;
                    {
                        std::lock_guard<std::mutex> ql(ctx->queueMutex);
                        queueDropped = ctx->decodeGate.Enqueue(ctx->frameQueue, std::move(frame));
                    }
                    if (queueDropped) ctx->droppedFrames.fetch_add(queueDropped);
                    ctx->queueCv.notify_one();
                }
            }
            else if (type == proto::AppPacketType::AudioFrame && plain.size() >= sizeof(proto::AudioFrameHeader))
            {
                const auto* hdr = reinterpret_cast<const proto::AudioFrameHeader*>(plain.data());
                if (plain.size() >= sizeof(proto::AudioFrameHeader) + hdr->payloadBytes)
                {
                    ctx->audioPlayer.PushOpusFrame(
                        plain.data() + sizeof(proto::AudioFrameHeader),
                        hdr->payloadBytes,
                        hdr->sampleRate,
                        hdr->channels,
                        hdr->sampleCount);
                }
            }
            else if (type == proto::AppPacketType::HostStatusJson && plain.size() > sizeof(proto::AppPacketHeader))
            {
                std::string json(
                    reinterpret_cast<const char*>(plain.data() + sizeof(proto::AppPacketHeader)),
                    plain.size() - sizeof(proto::AppPacketHeader));
                ParseStatusHintFields(ctx, json);
                if (ctx->callbacks.onStatusJson)
                {
                    ctx->callbacks.onStatusJson(json.c_str());
                }
            }
            else if (type == proto::AppPacketType::Pong && plain.size() >= sizeof(proto::PingPongPayload))
            {
                const auto* pong = reinterpret_cast<const proto::PingPongPayload*>(plain.data());
                uint64_t rttUs = NowMicros() - pong->timestampUs;
                ctx->transport->RecordRttMs(static_cast<uint32_t>(rttUs / 1000ULL));
            }
            else if (type == proto::AppPacketType::AuxData && plain.size() > sizeof(proto::AppPacketHeader))
            {
                if (ctx->callbacks.onAuxData)
                {
                    ctx->callbacks.onAuxData(
                        plain.data() + sizeof(proto::AppPacketHeader),
                        static_cast<int32_t>(plain.size() - sizeof(proto::AppPacketHeader)));
                }
            }
            else if (type == proto::AppPacketType::Disconnect)
            {
                ctx->remoteEnded.store(true);
                ctx->queueCv.notify_all();
                break;
            }
        }
    }

    uint16_t AllocateActionSeq(EmbeddedClientContext* ctx)
    {
        uint32_t v = ctx->actionSeqCounter.fetch_add(1, std::memory_order_relaxed);
        uint16_t s = static_cast<uint16_t>(v & 0xFFFFU);
        if (s == 0)
        {
            v = ctx->actionSeqCounter.fetch_add(1, std::memory_order_relaxed);
            s = static_cast<uint16_t>(v & 0xFFFFU);
            if (s == 0) s = 1;
        }
        return s;
    }

    void SendMustExecuteAction(EmbeddedClientContext* ctx, proto::AppPacketType type, void* payload, size_t payloadLen)
    {
        if (!ctx || !ctx->transport || !payload || payloadLen < sizeof(proto::AppPacketHeader)) return;

        auto* hdr = reinterpret_cast<proto::AppPacketHeader*>(payload);
        hdr->flags |= proto::kAppFlagHasActionSeq;
        hdr->reserved = AllocateActionSeq(ctx);

        ctx->transport->SendEncrypted(type, payload, payloadLen);

        if (!ctx->transport->IsKcpActive() &&
            ctx->transport->GetTransportRoute() != static_cast<int32_t>(TransportRoute::CloudRelay))
        {
            PendingUdpAction item;
            item.type = type;
            item.payload.assign(reinterpret_cast<const uint8_t*>(payload),
                                reinterpret_cast<const uint8_t*>(payload) + payloadLen);
            item.nextSendUs = NowMicros() + 4000ULL;
            item.remaining = 2;
            {
                std::lock_guard<std::mutex> rl(ctx->retxMutex);
                if (ctx->udpRetxQueue.size() >= 96)
                {
                    ctx->udpRetxQueue.pop_front();
                }
                ctx->udpRetxQueue.push_back(std::move(item));
            }
            ctx->queueCv.notify_one();
        }
    }

    bool FlushPendingUdpActions(EmbeddedClientContext* ctx)
    {
        if (!ctx || !ctx->transport) return false;
        const uint64_t nowUs = NowMicros();
        std::vector<PendingUdpAction> toSend;
        bool hasMore = false;
        {
            std::lock_guard<std::mutex> rl(ctx->retxMutex);
            for (auto it = ctx->udpRetxQueue.begin(); it != ctx->udpRetxQueue.end();)
            {
                if (nowUs >= it->nextSendUs)
                {
                    PendingUdpAction copy;
                    copy.type = it->type;
                    copy.payload = it->payload;
                    toSend.push_back(std::move(copy));
                    it->remaining--;
                    if (it->remaining <= 0)
                    {
                        it = ctx->udpRetxQueue.erase(it);
                        continue;
                    }
                    it->nextSendUs = nowUs + 8000ULL;
                }
                ++it;
            }
            hasMore = !ctx->udpRetxQueue.empty();
        }
        for (const auto& pkt : toSend)
        {
            ctx->transport->SendEncrypted(pkt.type, pkt.payload.data(), pkt.payload.size());
        }
        return hasMore;
    }

    void EmbeddedClientRenderLoop(EmbeddedClientContext* ctx)
    {
        ScopedHighResTimer hiResTimer;
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
        OsmiumClientStats stats{};
        stats.transportRoute = ctx->transport->GetTransportRoute();
        stats.localPort = static_cast<int32_t>(ctx->transport->GetLocalPort());
        CopyStringSafe(stats.peerEndpoint, sizeof(stats.peerEndpoint), ctx->transport->GetActiveEndpointString());
        CopyStringSafe(stats.localEndpoint, sizeof(stats.localEndpoint), ctx->transport->GetLocalEndpointString());

        uint32_t decodedTotal = 0;
        uint32_t renderedWindow = 0;
        double avgDecodeMs = 0.0;
        bool firedStarted = false;
        bool hasRenderedFirstFrame = false;
        uint64_t lastIdrReqMs = 0;
        uint64_t lastStatsMs = NowMillis();
        uint64_t lastPingMs = 0;

        SendReliableIdrRequest(ctx->transport);

        while (!ctx->stopFlag.load() && !ctx->remoteEnded.load())
        {
            bool hasPendingRetx = FlushPendingUdpActions(ctx);

            uint64_t nowMs = NowMillis();
            if (nowMs - lastPingMs >= 500)
            {
                lastPingMs = nowMs;
                proto::PingPongPayload ping{};
                ping.timestampUs = NowMicros();
                ctx->transport->SendEncrypted(proto::AppPacketType::Ping, &ping, sizeof(ping));
            }

            AssembledVideoFrame nextFrame;
            bool hasFrame = false;
            bool gateWaiting = false;
            size_t queuedAfterPop = 0;
            {
                std::unique_lock<std::mutex> ql(ctx->queueMutex);
                if (ctx->frameQueue.empty())
                {
                    ctx->queueCv.wait_for(ql, std::chrono::milliseconds(hasPendingRetx ? 3 : 8));
                }
                uint32_t gateDropped = 0;
                hasFrame = ctx->decodeGate.SelectNext(
                    ctx->frameQueue, NowMillis(), gateDropped, nextFrame);
                queuedAfterPop = ctx->frameQueue.size();
                gateWaiting = ctx->decodeGate.waitingForKeyframe;
                if (gateDropped != 0)
                {
                    ctx->droppedFrames.fetch_add(gateDropped);
                }
            }

            FlushPendingUdpActions(ctx);

            if (!hasFrame)
            {
                ctx->decoder.RepaintIfResized();
                if ((!hasRenderedFirstFrame || gateWaiting) && (nowMs - lastIdrReqMs >= 150))
                {
                    lastIdrReqMs = nowMs;
                    SendReliableIdrRequest(ctx->transport);
                }
                // Reorder grace leaves frames queued but nothing decodable;
                // yield instead of spinning the render thread.
                if (queuedAfterPop > 0) Sleep(1);
            }
            else
            {
                if (!hasRenderedFirstFrame && !nextFrame.isKeyFrame)
                {
                    if (nowMs - lastIdrReqMs >= 150)
                    {
                        lastIdrReqMs = nowMs;
                        SendReliableIdrRequest(ctx->transport);
                    }
                }
                else
                {
                    double decMs = 0.0;
                    stats.videoWidth = nextFrame.width;
                    stats.videoHeight = nextFrame.height;
                    stats.nativeWidth = ctx->nativeWidth.load() > 0 ? ctx->nativeWidth.load() : nextFrame.width;
                    stats.nativeHeight = ctx->nativeHeight.load() > 0 ? ctx->nativeHeight.load() : nextFrame.height;
                    stats.receivedFrames = ctx->receivedFrames.load();
                    stats.droppedFrames = ctx->droppedFrames.load();
                    stats.rttMs = ctx->transport->GetLastRttMs();

                    bool ok = ctx->decoder.DecodeAndRenderFrame(
                        nextFrame.codecId,
                        nextFrame.isKeyFrame,
                        nextFrame.width,
                        nextFrame.height,
                        nextFrame.colorFlags,
                        nextFrame.payload.data(),
                        nextFrame.payload.size(),
                        stats,
                        decMs,
                        queuedAfterPop > 0);

                    if (ok)
                    {
                        hasRenderedFirstFrame = true;
                        ctx->enableHdr.store((nextFrame.colorFlags & proto::kVideoFlagHdr) != 0 ? 1 : 0);
                        ctx->enableYuv444.store((nextFrame.colorFlags & proto::kVideoFlagYuv444) != 0 ? 1 : 0);
                        decodedTotal++;
                        renderedWindow++;
                        avgDecodeMs = avgDecodeMs * 0.85 + decMs * 0.15;

                        if (!firedStarted)
                        {
                            firedStarted = true;
                            if (ctx->callbacks.onStarted)
                            {
                                ctx->callbacks.onStarted(
                                    nextFrame.width,
                                    nextFrame.height,
                                    ctx->decoder.GetCodecName().c_str(),
                                    ctx->decoder.GetDecoderName().c_str());
                            }
                        }
                    }
                    else if (nowMs - lastIdrReqMs >= 200)
                    {
                        lastIdrReqMs = nowMs;
                        SendReliableIdrRequest(ctx->transport);
                    }
                }
            }

            nowMs = NowMillis();
            if (nowMs - lastStatsMs >= 500)
            {
                double sec = static_cast<double>(nowMs - lastStatsMs) / 1000.0;
                lastStatsMs = nowMs;
                uint64_t bytesWin = ctx->bytesRecvWindow.exchange(0);

                uint64_t rxPkts = 0, lostPkts = 0;
                ctx->transport->GetPacketStats(rxPkts, lostPkts);
                uint64_t totPkts = rxPkts + lostPkts;
                double lossPct = totPkts > 0
                                     ? (static_cast<double>(lostPkts) * 100.0 / static_cast<double>(totPkts))
                                     : 0.0;

                stats.receivedFrames = ctx->receivedFrames.load();
                stats.decodedFrames = decodedTotal;
                stats.renderedFrames = decodedTotal;
                stats.droppedFrames = ctx->droppedFrames.load();
                stats.fps = static_cast<double>(renderedWindow) / sec;
                stats.bitrateMbps = (static_cast<double>(bytesWin) * 8.0) / (sec * 1000000.0);
                stats.decodeLatencyMs = avgDecodeMs;
                stats.rttMs = ctx->transport->GetLastRttMs();
                stats.nativeWidth = ctx->nativeWidth.load() > 0 ? ctx->nativeWidth.load() : stats.videoWidth;
                stats.nativeHeight = ctx->nativeHeight.load() > 0 ? ctx->nativeHeight.load() : stats.videoHeight;
                stats.receivedPackets = rxPkts;
                stats.lostPackets = lostPkts;
                stats.packetLossPercent = lossPct;
                stats.activeDisplayIndex = ctx->activeDisplayIndex.load();
                stats.mouseMode = ctx->mouseMode.load();
                stats.virtualMouseEngaged = ctx->virtualMouseEngaged.load();
                stats.enableHdr = ctx->enableHdr.load();
                stats.enableYuv444 = ctx->enableYuv444.load();
                stats.localPort = static_cast<int32_t>(ctx->transport->GetLocalPort());
                stats.transportRoute = ctx->transport->GetTransportRoute();
                CopyStringSafe(stats.codecName, sizeof(stats.codecName), ctx->decoder.GetCodecName());
                CopyStringSafe(stats.decoderName, sizeof(stats.decoderName), ctx->decoder.GetDecoderName());
                CopyStringSafe(stats.peerEndpoint, sizeof(stats.peerEndpoint),
                               ctx->transport->GetRemoteEndpointString());
                CopyStringSafe(stats.localEndpoint, sizeof(stats.localEndpoint),
                               ctx->transport->GetLocalEndpointString());
                renderedWindow = 0;

                if (ctx->callbacks.onStatsUpdated)
                {
                    ctx->callbacks.onStatsUpdated(&stats);
                }
            }
        }

        if (ctx->callbacks.onEnded && ctx->remoteEnded.load())
        {
            ctx->callbacks.onEnded(0, "Host ended the session");
        }
    }
}

OSMIUM_API int32_t Osmium_Init(const char* gameControllerDbPath, OsmiumLogCallback logCallback)
{
    Logger::SetCallback(logCallback);
    if (g_Initialized) return 1;

    WSADATA wsa{};
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0)
    {
        OSM_LOG_ERROR("WSAStartup failed");
        return 0;
    }

    av_log_set_callback(FfmpegLogBridge);

    if (!SDL_Init(SDL_INIT_GAMEPAD | SDL_INIT_JOYSTICK | SDL_INIT_EVENTS))
    {
        OSM_LOG_WARN("SDL_Init partial failure: %s", SDL_GetError());
    }

    if (gameControllerDbPath && *gameControllerDbPath)
    {
        int added = SDL_AddGamepadMappingsFromFile(gameControllerDbPath);
        OSM_LOG_INFO("Loaded %d gamepad mappings from %s", added, gameControllerDbPath);
    }

    g_Initialized = true;
    OSM_LOG_INFO("Osmium native engine initialized (FFmpeg %s, SDL %d.%d.%d)",
                 av_version_info(), SDL_MAJOR_VERSION, SDL_MINOR_VERSION, SDL_MICRO_VERSION);
    return 1;
}

OSMIUM_API void Osmium_Shutdown(void)
{
    if (!g_Initialized) return;
    SDL_Quit();
    WSACleanup();
    g_Initialized = false;
}

static bool SafeProbeHardwareEncoder(const char* name)
{
    const AVCodec* c = avcodec_find_encoder_by_name(name);
    if (!c) return false;

    AVCodecContext* ctx = avcodec_alloc_context3(c);
    if (!ctx) return false;

    ctx->width = 1280;
    ctx->height = 720;
    ctx->time_base = {1, 60};
    ctx->framerate = {60, 1};
    ctx->pix_fmt = AV_PIX_FMT_NV12;

    bool ok = false;
    __try
    {
        ok = (avcodec_open2(ctx, c, nullptr) >= 0);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        ok = false;
    }

    avcodec_free_context(&ctx);
    return ok;
}

static bool SafeProbeD3D11VideoDevice()
{
    bool supported = false;
    __try
    {
        ID3D11Device* d3dRaw = nullptr;
        D3D_FEATURE_LEVEL fl{};
        if (SUCCEEDED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_VIDEO_SUPPORT, nullptr, 0,
            D3D11_SDK_VERSION, &d3dRaw, &fl, nullptr)) && d3dRaw)
        {
            ID3D11VideoDevice* vdevRaw = nullptr;
            if (SUCCEEDED(d3dRaw->QueryInterface(__uuidof(ID3D11VideoDevice), reinterpret_cast<void**>(&vdevRaw))) &&
                vdevRaw)
            {
                supported = true;
                vdevRaw->Release();
            }
            d3dRaw->Release();
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        supported = false;
    }
    return supported;
}

OSMIUM_API int32_t Osmium_ProbeCapabilities(OsmiumCapabilities* outCaps)
{
    if (!outCaps) return 0;
    memset(outCaps, 0, sizeof(*outCaps));
    outCaps->maxWidth = 7680;
    outCaps->maxHeight = 4320;

    std::string chosenEnc;
    std::string chosenDec;
    std::thread probeThread([&]()
    {
        HRESULT hrCom = CoInitializeEx(nullptr, COINIT_MULTITHREADED);

        if (SafeProbeD3D11VideoDevice())
        {
            outCaps->hasHardwareDecoder = 1;
            chosenDec = "D3D11VA Hardware Decoder";
        }
        else
        {
            chosenDec = "FFmpeg Software Decoder";
        }

        outCaps->decoderSupportsH264 = avcodec_find_decoder(AV_CODEC_ID_H264) ? 1 : 0;
        outCaps->decoderSupportsHevc = avcodec_find_decoder(AV_CODEC_ID_HEVC) ? 1 : 0;
        outCaps->decoderSupportsAv1 = avcodec_find_decoder(AV_CODEC_ID_AV1) ? 1 : 0;

        const char* hwH264[] = {"h264_nvenc", "h264_amf", "h264_qsv", "h264_mf"};
        const char* hwHevc[] = {"hevc_nvenc", "hevc_amf", "hevc_qsv", "hevc_mf"};
        const char* hwAv1[] = {"av1_nvenc", "av1_amf", "av1_qsv", "av1_mf"};

        for (const char* n : hwHevc)
        {
            if (SafeProbeHardwareEncoder(n))
            {
                outCaps->encoderSupportsHevc = 1;
                outCaps->hasHardwareEncoder = 1;
                if (chosenEnc.empty()) chosenEnc = n;
                break;
            }
        }
        for (const char* n : hwH264)
        {
            if (SafeProbeHardwareEncoder(n))
            {
                outCaps->encoderSupportsH264 = 1;
                outCaps->hasHardwareEncoder = 1;
                if (chosenEnc.empty()) chosenEnc = n;
                break;
            }
        }
        for (const char* n : hwAv1)
        {
            if (SafeProbeHardwareEncoder(n))
            {
                outCaps->encoderSupportsAv1 = 1;
                outCaps->hasHardwareEncoder = 1;
                break;
            }
        }

        if (!outCaps->encoderSupportsH264 && avcodec_find_encoder_by_name("libopenh264"))
        {
            outCaps->encoderSupportsH264 = 1;
            if (chosenEnc.empty()) chosenEnc = "libopenh264";
        }
        if (!outCaps->encoderSupportsAv1 && avcodec_find_encoder_by_name("libsvtav1"))
        {
            outCaps->encoderSupportsAv1 = 1;
        }
        outCaps->supportsHdr = (outCaps->encoderSupportsHevc || outCaps->encoderSupportsAv1) ? 1 : 0;

        if (SUCCEEDED(hrCom))
        {
            CoUninitialize();
        }
    });
    probeThread.join();

    CopyStringSafe(outCaps->preferredDecoderName, sizeof(outCaps->preferredDecoderName), chosenDec);
    CopyStringSafe(outCaps->preferredEncoderName, sizeof(outCaps->preferredEncoderName),
                   chosenEnc.empty() ? "libopenh264" : chosenEnc);

    return 1;
}

OSMIUM_API int32_t Osmium_EnumDisplays(OsmiumDisplayInfo* outDisplays, int32_t maxCount)
{
    if (!outDisplays || maxCount <= 0) return 0;
    auto list = HostCapture::EnumerateDisplays();
    int32_t count = static_cast<int32_t>(std::min<size_t>(list.size(), static_cast<size_t>(maxCount)));
    for (int32_t i = 0; i < count; ++i)
    {
        outDisplays[i] = list[static_cast<size_t>(i)];
    }
    return count;
}

OSMIUM_API int32_t Osmium_EnumMicDevices(OsmiumAudioDeviceInfo* outDevices, int32_t maxCount)
{
    if (!outDevices || maxCount <= 0) return 0;
    auto list = ClientMicCapture::EnumerateMicDevices();
    int32_t count = static_cast<int32_t>(std::min<size_t>(list.size(), static_cast<size_t>(maxCount)));
    for (int32_t i = 0; i < count; ++i)
    {
        outDevices[i] = list[static_cast<size_t>(i)];
    }
    return count;
}

OSMIUM_API int32_t Osmium_GetDriverStatus(int32_t* outHasUsbIp, int32_t* outIsElevated, int32_t* outIsSystem)
{
    bool usbRun = UsbIpVirtualDevices::IsDriverAvailable();

    if (outHasUsbIp) *outHasUsbIp = usbRun ? 1 : 0;
    if (outIsElevated) *outIsElevated = DriverSetupHelper::IsProcessElevated() ? 1 : 0;
    if (outIsSystem) *outIsSystem = DriverSetupHelper::IsRunningAsSystem() ? 1 : 0;
    return 1;
}

OSMIUM_API int32_t Osmium_InstallVirtualDrivers(void)
{
    return UsbIpVirtualDevices::EnsureDriverInstalled() ? 1 : 0;
}

OSMIUM_API int32_t Osmium_UninstallVirtualDrivers(void)
{
    return UsbIpVirtualDevices::UninstallDriver() ? 1 : 0;
}

OSMIUM_API int32_t Osmium_RunSystemService(void)
{
    return HostSystemAgent::RunSystemServiceMain();
}

OSMIUM_API int32_t Osmium_RunOneShotSasService(void)
{
    return HostSystemAgent::RunOneShotSasServiceMain();
}

OSMIUM_API int32_t Osmium_RunInputAgent(void)
{
    return HostSystemAgent::RunInputAgentMain();
}

OSMIUM_API int32_t Osmium_InstallSystemService(void)
{
    return HostSystemAgent::InstallSystemService() ? 1 : 0;
}

OSMIUM_API int32_t Osmium_UninstallSystemService(void)
{
    return HostSystemAgent::UninstallSystemService() ? 1 : 0;
}

OSMIUM_API int32_t Osmium_IsSystemServiceRunning(void)
{
    return HostSystemAgent::IsSystemServiceRunning() ? 1 : 0;
}

OSMIUM_API int32_t Osmium_GetConnectedGamepads(OsmiumGamepadInfo* outGamepads, int32_t maxCount)
{
    if (!outGamepads || maxCount <= 0) return 0;
    SDL_PumpEvents();

    int count = 0;
    SDL_JoystickID* ids = SDL_GetJoysticks(&count);
    if (!ids) return 0;

    int written = 0;
    for (int i = 0; i < count && written < maxCount; ++i)
    {
        SDL_JoystickID jid = ids[i];
        OsmiumGamepadInfo info{};
        info.joystickIndex = i;
        info.instanceId = static_cast<int32_t>(jid);
        info.isGameController = SDL_IsGamepad(jid) ? 1 : 0;

        const char* name = SDL_GetJoystickNameForID(jid);
        CopyStringSafe(info.name, sizeof(info.name), name ? name : "Unknown Controller");

        SDL_GUID guid = SDL_GetJoystickGUIDForID(jid);
        SDL_GUIDToString(guid, info.guid, sizeof(info.guid));

        SDL_Joystick* joy = SDL_OpenJoystick(jid);
        if (joy)
        {
            info.buttonCount = SDL_GetNumJoystickButtons(joy);
            info.axisCount = SDL_GetNumJoystickAxes(joy);
            info.hatCount = SDL_GetNumJoystickHats(joy);
            SDL_CloseJoystick(joy);
        }

        outGamepads[written++] = info;
    }
    SDL_free(ids);
    return written;
}

OSMIUM_API int32_t Osmium_PollGamepadState(int32_t joystickIndex, OsmiumGamepadLiveState* outState)
{
    if (!outState) return 0;
    memset(outState, 0, sizeof(*outState));
    outState->lastPressedButton = -1;
    outState->lastMovedAxis = -1;

    SDL_PumpEvents();
    int count = 0;
    SDL_JoystickID* ids = SDL_GetJoysticks(&count);
    if (!ids) return 0;

    if (joystickIndex < 0 || joystickIndex >= count)
    {
        SDL_free(ids);
        return 0;
    }

    SDL_Joystick* joy = SDL_OpenJoystick(ids[joystickIndex]);
    SDL_free(ids);
    if (!joy) return 0;

    outState->connected = 1;
    int nb = std::min(32, SDL_GetNumJoystickButtons(joy));
    for (int b = 0; b < nb; ++b)
    {
        if (SDL_GetJoystickButton(joy, b))
        {
            outState->buttonsMask |= (1 << b);
            outState->lastPressedButton = b;
        }
    }

    int na = std::min(8, SDL_GetNumJoystickAxes(joy));
    for (int a = 0; a < na; ++a)
    {
        int16_t val = SDL_GetJoystickAxis(joy, a);
        outState->axes[a] = val;
        if (abs(val) > 16000 && outState->lastMovedAxis == -1)
        {
            outState->lastMovedAxis = a;
            outState->lastMovedAxisDirection = val > 0 ? 1 : -1;
        }
    }

    int nh = std::min(4, SDL_GetNumJoystickHats(joy));
    for (int h = 0; h < nh; ++h)
    {
        outState->hats[h] = SDL_GetJoystickHat(joy, h);
    }

    SDL_CloseJoystick(joy);
    return 1;
}

OSMIUM_API int32_t Osmium_AddGamepadMapping(const char* mappingString)
{
    if (!mappingString || !*mappingString) return 0;
    return SDL_AddGamepadMapping(mappingString) >= 0 ? 1 : 0;
}

OSMIUM_API int32_t Osmium_CreateTransport(
    const char* stunHost,
    uint16_t stunPort,
    void** outTransport,
    char* outLocalCandidatesJson,
    int32_t jsonBufLen)
{
    if (!outTransport) return 0;
    auto* t = new OsmiumTransport();
    std::string candidates;
    if (!t->InitializeAndProbeStun(stunHost, stunPort, candidates))
    {
        delete t;
        return 0;
    }
    CopyStringSafe(outLocalCandidatesJson, static_cast<size_t>(jsonBufLen), candidates);
    *outTransport = t;
    return 1;
}

OSMIUM_API void Osmium_DestroyTransport(void* transport)
{
    if (!transport) return;
    auto* t = static_cast<OsmiumTransport*>(transport);
    t->Close();
    delete t;
}

OSMIUM_API int32_t Osmium_ConnectPeerP2P(
    void* transport,
    const uint8_t* sessionToken16,
    int32_t localRole,
    const char* peerCandidatesJson,
    int32_t punchTimeoutMs,
    int32_t* outTransportRoute,
    char* outActiveEndpoint,
    int32_t endpointBufLen)
{
    if (!transport || !sessionToken16) return 0;
    auto* t = static_cast<OsmiumTransport*>(transport);
    int32_t route = 0;
    std::string ep;
    auto role = (localRole == 2) ? proto::PeerRole::Host : proto::PeerRole::Client;
    if (!t->ConnectPeer(sessionToken16, role, peerCandidatesJson, punchTimeoutMs, route, ep))
    {
        return 0;
    }
    if (outTransportRoute) *outTransportRoute = route;
    CopyStringSafe(outActiveEndpoint, static_cast<size_t>(endpointBufLen), ep);
    return 1;
}

OSMIUM_API int32_t Osmium_EnableTransportRelay(
    void* transport,
    const uint8_t* sessionToken16,
    int32_t localRole,
    OsmiumRelaySendCallback onSendRelay,
    const char* relayEndpointLabel)
{
    if (!transport || !sessionToken16 || !onSendRelay) return 0;
    auto* t = static_cast<OsmiumTransport*>(transport);
    auto role = (localRole == 2) ? proto::PeerRole::Host : proto::PeerRole::Client;
    return t->EnableRelay(
               sessionToken16,
               role,
               [onSendRelay](const uint8_t* data, size_t len, int32_t dropGroupId) -> bool
               {
                   return onSendRelay(data, static_cast<int32_t>(len), dropGroupId) != 0;
               },
               relayEndpointLabel)
               ? 1
               : 0;
}

OSMIUM_API int32_t Osmium_PushTransportRelayPacket(
    void* transport,
    const uint8_t* data,
    int32_t length)
{
    if (!transport || !data || length <= 0) return 0;
    return static_cast<OsmiumTransport*>(transport)->PushRelayPacket(data, static_cast<size_t>(length)) ? 1 : 0;
}

OSMIUM_API int32_t Osmium_PushTransportRelayBatch(
    void* transport,
    const uint8_t* batchData,
    int32_t batchLength)
{
    if (!transport || !batchData || batchLength <= 0) return 0;
    return static_cast<OsmiumTransport*>(transport)->PushRelayBatch(batchData, static_cast<size_t>(batchLength)) ? 1 : 0;
}

OSMIUM_API int32_t Osmium_SetTransportKey(void* transport, const uint8_t* aesKey32)
{
    if (!transport || !aesKey32) return 0;
    return static_cast<OsmiumTransport*>(transport)->SetSessionKey(aesKey32) ? 1 : 0;
}

OSMIUM_API int32_t Osmium_SetTransportEncryption(void* transport, int32_t enableEncryption)
{
    if (!transport) return 0;
    static_cast<OsmiumTransport*>(transport)->SetEncryptionEnabled(enableEncryption != 0);
    return 1;
}

OSMIUM_API int32_t Osmium_SetTransportKcpEnabled(void* transport, int32_t enableKcp)
{
    if (!transport) return 0;
    static_cast<OsmiumTransport*>(transport)->SetKcpEnabled(enableKcp != 0);
    return 1;
}

OSMIUM_API int32_t Osmium_AuthenticateClient(
    void* transport,
    const uint8_t* authTag32,
    const OsmiumClientConfig* requestedProfile,
    int32_t timeoutMs)
{
    if (!transport || !authTag32) return 0;
    auto* t = static_cast<OsmiumTransport*>(transport);

    proto::AuthRequestPayload req{};
    memcpy(req.authTag, authTag32, 32);
    if (requestedProfile)
    {
        req.desiredWidth = requestedProfile->targetWidth;
        req.desiredHeight = requestedProfile->targetHeight;
        req.desiredFps = requestedProfile->fps;
        req.desiredBitrateKbps = requestedProfile->bitrateKbps;
        req.codecPreference = requestedProfile->codecPreference;
        req.enableHdr = requestedProfile->enableHdr;
    }

    const uint64_t deadline = NowMillis() + static_cast<uint64_t>(std::max(1000, timeoutMs));
    uint64_t lastSend = 0;
    std::vector<uint8_t> plain;

    while (NowMillis() < deadline)
    {
        uint64_t now = NowMillis();
        if (now - lastSend >= 150)
        {
            lastSend = now;
            t->SendEncrypted(proto::AppPacketType::AuthRequest, &req, sizeof(req));
        }

        int r = t->ReceiveEncrypted(plain, 100);
        if (r > 0 && plain.size() >= sizeof(proto::AuthResponsePayload) &&
            static_cast<proto::AppPacketType>(plain[0]) == proto::AppPacketType::AuthResponse)
        {
            const auto* resp = reinterpret_cast<const proto::AuthResponsePayload*>(plain.data());
            if (resp->accepted)
            {
                return 1;
            }
            OSM_LOG_WARN("Host rejected access code: %s", resp->message);
            return -1;
        }
    }

    return 0;
}

OSMIUM_API int32_t Osmium_WaitClientAuthHost(
    void* transport,
    const uint8_t* expectedTempTag32,
    const uint8_t* expectedFixedTag32,
    int32_t hasFixedCode,
    int32_t timeoutMs,
    OsmiumClientConfig* outNegotiatedProfile)
{
    if (!transport || !expectedTempTag32) return 0;
    auto* t = static_cast<OsmiumTransport*>(transport);

    const uint64_t deadline = NowMillis() + static_cast<uint64_t>(std::max(1000, timeoutMs));
    std::vector<uint8_t> plain;

    auto constantTimeEq = [](const uint8_t* a, const uint8_t* b)
    {
        uint8_t diff = 0;
        for (int i = 0; i < 32; ++i) diff |= (a[i] ^ b[i]);
        return diff == 0;
    };

    while (NowMillis() < deadline)
    {
        int r = t->ReceiveEncrypted(plain, 100);
        if (r < 0) return 0;
        if (r > 0 && plain.size() >= sizeof(proto::AuthRequestPayload) &&
            static_cast<proto::AppPacketType>(plain[0]) == proto::AppPacketType::AuthRequest)
        {
            const auto* req = reinterpret_cast<const proto::AuthRequestPayload*>(plain.data());

            bool matched = constantTimeEq(req->authTag, expectedTempTag32) ||
                (hasFixedCode && expectedFixedTag32 && constantTimeEq(req->authTag, expectedFixedTag32));

            proto::AuthResponsePayload resp{};
            if (matched)
            {
                resp.accepted = 1;
                resp.errorCode = 0;
                CopyStringSafe(resp.message, sizeof(resp.message), "Authenticated");
                for (int i = 0; i < 4; ++i)
                {
                    if (i > 0) Sleep(15);
                    t->SendEncrypted(proto::AppPacketType::AuthResponse, &resp, sizeof(resp));
                }
                if (outNegotiatedProfile)
                {
                    memset(outNegotiatedProfile, 0, sizeof(*outNegotiatedProfile));
                    outNegotiatedProfile->targetWidth = req->desiredWidth;
                    outNegotiatedProfile->targetHeight = req->desiredHeight;
                    outNegotiatedProfile->fps = req->desiredFps;
                    outNegotiatedProfile->bitrateKbps = req->desiredBitrateKbps;
                    outNegotiatedProfile->codecPreference = req->codecPreference;
                    outNegotiatedProfile->enableHdr = req->enableHdr;
                }
                return 1;
            }
            else
            {
                resp.accepted = 0;
                resp.errorCode = 403;
                CopyStringSafe(resp.message, sizeof(resp.message), "Invalid remote access code");
                t->SendEncrypted(proto::AppPacketType::AuthResponse, &resp, sizeof(resp));
                return -1;
            }
        }
    }

    return 0;
}

OSMIUM_API int32_t Osmium_StartHostSession(
    void* transport,
    const OsmiumHostConfig* config,
    const OsmiumClientConfig* negotiatedClientProfile,
    const OsmiumHostCallbacks* callbacks,
    void** outHostHandle)
{
    if (!transport || !config || !outHostHandle) return 0;

    auto* ctx = new HostSessionContext();
    ctx->transport = static_cast<OsmiumTransport*>(transport);
    ctx->config = *config;
    if (negotiatedClientProfile) ctx->clientProfile = *negotiatedClientProfile;
    if (callbacks) ctx->callbacks = *callbacks;

    const auto& profile = ctx->clientProfile;
    ctx->activeDisplayIndex = ctx->config.displayIndex;
    ctx->videoSettings = ApplyHostVideoLimits(ctx->config, {
        profile.targetWidth, profile.targetHeight, profile.fps, profile.bitrateKbps,
        profile.enableHdr != 0, profile.enableYuv444 != 0
    });
    ctx->mouseMode = profile.mouseMode;
    ctx->hideHostCursorInHostMode = (ctx->mouseMode == 0);

    ctx->sysActionThread = std::thread(HostSysActionLoop, ctx);
    ctx->videoThread = std::thread(HostVideoLoop, ctx);
    ctx->recvThread = std::thread(HostRecvLoop, ctx);

    *outHostHandle = ctx;
    return 1;
}

OSMIUM_API void Osmium_StopHostSession(void* hostHandle)
{
    if (!hostHandle) return;
    auto* ctx = static_cast<HostSessionContext*>(hostHandle);
    ctx->stopFlag.store(true);
    ctx->sysActionCv.notify_all();
    ctx->textFocus.Stop();

    if (ctx->transport)
    {
        proto::AppPacketHeader bye{};
        for (int i = 0; i < 4; ++i)
        {
            ctx->transport->SendEncrypted(proto::AppPacketType::Disconnect, &bye, sizeof(bye));
            if (i < 3) Sleep(3);
        }
        ctx->transport->Close();
    }

    auto curTid = std::this_thread::get_id();
    if (ctx->videoThread.joinable())
    {
        if (ctx->videoThread.get_id() == curTid) ctx->videoThread.detach();
        else ctx->videoThread.join();
    }
    if (ctx->recvThread.joinable())
    {
        if (ctx->recvThread.get_id() == curTid) ctx->recvThread.detach();
        else ctx->recvThread.join();
    }
    if (ctx->sysActionThread.joinable())
    {
        if (ctx->sysActionThread.get_id() == curTid) ctx->sysActionThread.detach();
        else ctx->sysActionThread.join();
    }
    delete ctx;
}

OSMIUM_API void Osmium_HostAllowPrivacyScreen(void* hostHandle, const wchar_t* wallpaperPath)
{
    if (!hostHandle) return;
    auto* ctx = static_cast<HostSessionContext*>(hostHandle);
    if (!ctx->config.allowInputControl) return;
    {
        std::lock_guard<std::mutex> lock(ctx->cfgMutex);
        ctx->privacyWallpaperPath = wallpaperPath ? wallpaperPath : L"";
    }
    ctx->allowPrivacyScreen.store(true);
    ctx->statusDirty.store(true);
}

OSMIUM_API int32_t Osmium_HostSendAuxData(void* hostHandle, const uint8_t* data, int32_t length)
{
    if (!hostHandle || !data || length <= 0 ||
        static_cast<size_t>(length) > proto::kMaxAuxDataPayload)
    {
        return 0;
    }

    auto* ctx = static_cast<HostSessionContext*>(hostHandle);
    if (!ctx->transport || ctx->stopFlag.load(std::memory_order_acquire))
    {
        return 0;
    }

    uint8_t pkt[sizeof(proto::AppPacketHeader) + proto::kMaxAuxDataPayload];
    memset(pkt, 0, sizeof(proto::AppPacketHeader));
    memcpy(pkt + sizeof(proto::AppPacketHeader), data, static_cast<size_t>(length));
    return ctx->transport->SendEncrypted(
               proto::AppPacketType::AuxData,
               pkt,
               sizeof(proto::AppPacketHeader) + static_cast<size_t>(length))
               ? 1
               : 0;
}

OSMIUM_API int32_t Osmium_StartClientEmbedded(
    void* transport,
    void* swapChainPanelIUnknown,
    int32_t initialWidth,
    int32_t initialHeight,
    float dpiScale,
    const OsmiumClientConfig* config,
    const OsmiumClientCallbacks* callbacks,
    void** outClientHandle)
{
    if (!transport || !swapChainPanelIUnknown || !config || !outClientHandle) return 0;

    auto* ctx = new EmbeddedClientContext();
    ctx->transport = static_cast<OsmiumTransport*>(transport);
    ctx->config = *config;
    if (callbacks) ctx->callbacks = *callbacks;
    ctx->mouseMode.store(config->mouseMode);
    ctx->enableHdr.store(0);
    ctx->enableYuv444.store(config->enableYuv444);

    if (!ctx->decoder.InitializeCompositionRenderer(
        static_cast<IUnknown*>(swapChainPanelIUnknown),
        initialWidth, initialHeight, dpiScale,
        config->enableVsync != 0,
        config->decoderPreference,
        config->enableHdr != 0))
    {
        delete ctx;
        return 0;
    }

    ctx->decoder.SetOverlayVisible(config->showPerformanceOverlay != 0);
    ctx->audioPlayer.Initialize();

    int count = 0;
    SDL_JoystickID* ids = SDL_GetJoysticks(&count);
    if (ids)
    {
        int slot = 0;
        for (int i = 0; i < count && slot < 4; ++i)
        {
            if (SDL_IsGamepad(ids[i]))
            {
                ctx->gamepads[slot] = SDL_OpenGamepad(ids[i]);
                ctx->gamepadIds[slot] = ids[i];
                if (ctx->gamepads[slot]) slot++;
                if (!config->multiController) break;
            }
        }
        SDL_free(ids);
    }

    ctx->recvThread = std::thread(EmbeddedClientRecvLoop, ctx);
    ctx->renderThread = std::thread(EmbeddedClientRenderLoop, ctx);

    {
        proto::ControlVideoParamsPayload vp{};
        vp.targetWidth = config->targetWidth;
        vp.targetHeight = config->targetHeight;
        vp.fps = config->fps;
        vp.bitrateKbps = config->bitrateKbps;
        vp.enableHdr = config->enableHdr;
        vp.enableYuv444 = config->enableYuv444;
        SendMustExecuteAction(ctx, proto::AppPacketType::ControlVideoParams, &vp, sizeof(vp));

        proto::ControlMouseModePayload mp{};
        mp.mouseMode = config->mouseMode;
        mp.hideHostCursor = (config->mouseMode == 0) ? 1 : 0;
        SendMustExecuteAction(ctx, proto::AppPacketType::ControlMouseMode, &mp, sizeof(mp));
    }

    *outClientHandle = ctx;
    return 1;
}

OSMIUM_API void Osmium_StopClientEmbedded(void* clientHandle)
{
    if (!clientHandle) return;
    auto* ctx = static_cast<EmbeddedClientContext*>(clientHandle);
    ctx->stopFlag.store(true);
    ctx->queueCv.notify_all();

    ctx->micCapture.Stop();

    if (ctx->transport)
    {
        proto::AppPacketHeader bye{};
        for (int i = 0; i < 4; ++i)
        {
            ctx->transport->SendEncrypted(proto::AppPacketType::Disconnect, &bye, sizeof(bye));
            if (i < 3) Sleep(3);
        }
        ctx->transport->Close();
    }

    auto curTid = std::this_thread::get_id();
    if (ctx->recvThread.joinable())
    {
        if (ctx->recvThread.get_id() == curTid) ctx->recvThread.detach();
        else ctx->recvThread.join();
    }
    if (ctx->renderThread.joinable())
    {
        if (ctx->renderThread.get_id() == curTid) ctx->renderThread.detach();
        else ctx->renderThread.join();
    }

    for (int i = 0; i < 4; ++i)
    {
        if (ctx->gamepads[i])
        {
            SDL_CloseGamepad(ctx->gamepads[i]);
            ctx->gamepads[i] = nullptr;
            ctx->gamepadIds[i] = 0;
        }
    }

    ctx->audioPlayer.Shutdown();
    ctx->decoder.Shutdown();
    delete ctx;
}

OSMIUM_API void Osmium_ClientResize(void* clientHandle, int32_t width, int32_t height, float dpiScale)
{
    if (!clientHandle) return;
    auto* ctx = static_cast<EmbeddedClientContext*>(clientHandle);
    ctx->decoder.OnWindowResized(width, height, dpiScale);
    ctx->queueCv.notify_all();
}

OSMIUM_API void Osmium_ClientSwitchDisplay(void* clientHandle, int32_t displayIndex)
{
    if (!clientHandle) return;
    auto* ctx = static_cast<EmbeddedClientContext*>(clientHandle);
    if (!ctx->transport) return;
    proto::ControlSwitchDisplayPayload pkt{};
    pkt.displayIndex = displayIndex;
    SendMustExecuteAction(ctx, proto::AppPacketType::ControlSwitchDisplay, &pkt, sizeof(pkt));
}

OSMIUM_API void Osmium_ClientUpdateVideoParams(
    void* clientHandle,
    int32_t targetWidth,
    int32_t targetHeight,
    int32_t fps,
    int32_t bitrateKbps,
    int32_t enableHdr,
    int32_t enableYuv444)
{
    if (!clientHandle) return;
    auto* ctx = static_cast<EmbeddedClientContext*>(clientHandle);
    ctx->enableYuv444.store(enableYuv444);
    ctx->decoder.SetHdrRequested(enableHdr != 0);
    if (!ctx->transport) return;

    proto::ControlVideoParamsPayload pkt{};
    pkt.targetWidth = targetWidth;
    pkt.targetHeight = targetHeight;
    pkt.fps = fps;
    pkt.bitrateKbps = bitrateKbps;
    pkt.enableHdr = enableHdr;
    pkt.enableYuv444 = enableYuv444;
    SendMustExecuteAction(ctx, proto::AppPacketType::ControlVideoParams, &pkt, sizeof(pkt));
}

OSMIUM_API void Osmium_ClientSetMouseMode(void* clientHandle, int32_t mouseMode, int32_t hideHostCursor)
{
    if (!clientHandle) return;
    auto* ctx = static_cast<EmbeddedClientContext*>(clientHandle);
    ctx->mouseMode.store(mouseMode);
    if (!ctx->transport) return;
    proto::ControlMouseModePayload pkt{};
    pkt.mouseMode = mouseMode;
    pkt.hideHostCursor = hideHostCursor;
    SendMustExecuteAction(ctx, proto::AppPacketType::ControlMouseMode, &pkt, sizeof(pkt));
}

OSMIUM_API void Osmium_ClientSetVirtualAudio(
    void* clientHandle,
    int32_t virtualSpeaker,
    int32_t virtualMic,
    const char* localMicDeviceId)
{
    if (!clientHandle) return;
    auto* ctx = static_cast<EmbeddedClientContext*>(clientHandle);
    if (!ctx->transport) return;

    proto::ControlVirtualAudioPayload pkt{};
    pkt.virtualSpeakerEnabled = virtualSpeaker ? 1 : 0;
    pkt.virtualMicEnabled = virtualMic ? 1 : 0;
    SendMustExecuteAction(ctx, proto::AppPacketType::ControlVirtualAudio, &pkt, sizeof(pkt));

    if (virtualMic)
    {
        std::string micId = localMicDeviceId ? localMicDeviceId : "";
        ctx->micCapture.Start(micId, [ctx](const uint8_t* opusData, int bytes, int sampleCount)
        {
            if (ctx->stopFlag.load() || !ctx->transport) return;
            std::vector<uint8_t> mpkt(sizeof(proto::AudioFrameHeader) + static_cast<size_t>(bytes));
            auto* hdr = reinterpret_cast<proto::AudioFrameHeader*>(mpkt.data());
            memset(hdr, 0, sizeof(*hdr));
            hdr->sequence = ctx->micSeqCounter.fetch_add(1);
            hdr->sampleRate = 48000;
            hdr->channels = 2;
            hdr->sampleCount = static_cast<uint16_t>(sampleCount);
            hdr->payloadBytes = static_cast<uint16_t>(bytes);
            memcpy(mpkt.data() + sizeof(proto::AudioFrameHeader), opusData, static_cast<size_t>(bytes));
            ctx->transport->SendEncrypted(proto::AppPacketType::ClientMicAudioFrame, mpkt.data(), mpkt.size());
        });
    }
    else
    {
        ctx->micCapture.Stop();
    }
}

OSMIUM_API void Osmium_ClientSendSystemAction(void* clientHandle, int32_t action)
{
    if (!clientHandle) return;
    auto* ctx = static_cast<EmbeddedClientContext*>(clientHandle);
    if (!ctx->transport) return;
    proto::ControlSystemActionPayload pkt{};
    pkt.action = action;
    SendMustExecuteAction(ctx, proto::AppPacketType::ControlSystemAction, &pkt, sizeof(pkt));
}

OSMIUM_API void Osmium_ClientSendKey(void* clientHandle, uint16_t vkCode, uint16_t scanCode, int32_t isDown,
                                     int32_t isExtended)
{
    if (!clientHandle) return;
    auto* ctx = static_cast<EmbeddedClientContext*>(clientHandle);
    if (!ctx->transport) return;
    proto::InputKeyPayload pkt{};
    pkt.vkCode = vkCode;
    pkt.scanCode = scanCode;
    pkt.isDown = isDown ? 1 : 0;
    pkt.isExtended = isExtended ? 1 : 0;
    SendMustExecuteAction(ctx, proto::AppPacketType::InputKey, &pkt, sizeof(pkt));
}

OSMIUM_API void Osmium_ClientSendMouseMove(void* clientHandle, int32_t isAbsolute, float xOrDeltaX, float yOrDeltaY)
{
    if (!clientHandle) return;
    auto* ctx = static_cast<EmbeddedClientContext*>(clientHandle);
    if (!ctx->transport) return;
    if (isAbsolute)
    {
        ctx->lastAbsMouseX.store(xOrDeltaX, std::memory_order_relaxed);
        ctx->lastAbsMouseY.store(yOrDeltaY, std::memory_order_relaxed);
        ctx->hasLastAbsMouse.store(true, std::memory_order_release);
    }
    proto::InputMouseMovePayload pkt{};
    pkt.isAbsolute = isAbsolute ? 1 : 0;
    pkt.xOrDeltaX = xOrDeltaX;
    pkt.yOrDeltaY = yOrDeltaY;
    ctx->transport->SendEncrypted(proto::AppPacketType::InputMouseMove, &pkt, sizeof(pkt));
}

OSMIUM_API void Osmium_ClientSendMouseButton(void* clientHandle, uint8_t button, int32_t isDown)
{
    if (!clientHandle) return;
    auto* ctx = static_cast<EmbeddedClientContext*>(clientHandle);
    if (!ctx->transport) return;
    if (ctx->transport->GetTransportRoute() != static_cast<int32_t>(TransportRoute::CloudRelay) &&
        ctx->hasLastAbsMouse.load(std::memory_order_acquire))
    {
        proto::InputMouseMovePayload mv{};
        mv.isAbsolute = 1;
        mv.xOrDeltaX = ctx->lastAbsMouseX.load(std::memory_order_relaxed);
        mv.yOrDeltaY = ctx->lastAbsMouseY.load(std::memory_order_relaxed);
        ctx->transport->SendEncrypted(proto::AppPacketType::InputMouseMove, &mv, sizeof(mv));
    }
    proto::InputMouseButtonPayload pkt{};
    pkt.button = button;
    pkt.isDown = isDown ? 1 : 0;
    SendMustExecuteAction(ctx, proto::AppPacketType::InputMouseButton, &pkt, sizeof(pkt));
}

OSMIUM_API void Osmium_ClientSendMouseWheel(void* clientHandle, int16_t deltaX, int16_t deltaY)
{
    if (!clientHandle) return;
    auto* ctx = static_cast<EmbeddedClientContext*>(clientHandle);
    if (!ctx->transport) return;
    proto::InputMouseWheelPayload pkt{};
    pkt.deltaX = deltaX;
    pkt.deltaY = deltaY;
    SendMustExecuteAction(ctx, proto::AppPacketType::InputMouseWheel, &pkt, sizeof(pkt));
}

OSMIUM_API void Osmium_ClientPollInput(void* clientHandle, int32_t windowFocused)
{
    if (!clientHandle) return;
    auto* ctx = static_cast<EmbeddedClientContext*>(clientHandle);
    if (!ctx->transport) return;

    SDL_PumpEvents();
    if (!windowFocused && !ctx->config.backgroundGamepad) return;

    for (int i = 0; i < 4; ++i)
    {
        SDL_Gamepad* gp = ctx->gamepads[i];
        if (!gp) continue;

        uint16_t mask = 0;
        bool swapFace = ctx->config.swapFaceButtons != 0;
        if (SDL_GetGamepadButton(gp, swapFace ? SDL_GAMEPAD_BUTTON_EAST : SDL_GAMEPAD_BUTTON_SOUTH)) mask |= 0x1000;
        if (SDL_GetGamepadButton(gp, swapFace ? SDL_GAMEPAD_BUTTON_SOUTH : SDL_GAMEPAD_BUTTON_EAST)) mask |= 0x2000;
        if (SDL_GetGamepadButton(gp, swapFace ? SDL_GAMEPAD_BUTTON_NORTH : SDL_GAMEPAD_BUTTON_WEST)) mask |= 0x4000;
        if (SDL_GetGamepadButton(gp, swapFace ? SDL_GAMEPAD_BUTTON_WEST : SDL_GAMEPAD_BUTTON_NORTH)) mask |= 0x8000;
        if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_DPAD_UP)) mask |= 0x0001;
        if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_DPAD_DOWN)) mask |= 0x0002;
        if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_DPAD_LEFT)) mask |= 0x0004;
        if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_DPAD_RIGHT)) mask |= 0x0008;
        if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_START)) mask |= 0x0010;
        if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_BACK)) mask |= 0x0020;
        if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_LEFT_STICK)) mask |= 0x0040;
        if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_RIGHT_STICK))mask |= 0x0080;
        if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_LEFT_SHOULDER)) mask |= 0x0100;
        if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER)) mask |= 0x0200;
        if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_GUIDE)) mask |= 0x0400;

        proto::InputGamepadPayload pkt{};
        pkt.controllerIndex = static_cast<uint8_t>(i);
        pkt.connected = 1;
        pkt.buttonsMask = mask;
        pkt.leftTrigger = static_cast<uint8_t>(SDL_GetGamepadAxis(gp, SDL_GAMEPAD_AXIS_LEFT_TRIGGER) >> 7);
        pkt.rightTrigger = static_cast<uint8_t>(SDL_GetGamepadAxis(gp, SDL_GAMEPAD_AXIS_RIGHT_TRIGGER) >> 7);
        pkt.leftThumbX = SDL_GetGamepadAxis(gp, SDL_GAMEPAD_AXIS_LEFTX);
        pkt.leftThumbY = SDL_GetGamepadAxis(gp, SDL_GAMEPAD_AXIS_LEFTY);
        pkt.rightThumbX = SDL_GetGamepadAxis(gp, SDL_GAMEPAD_AXIS_RIGHTX);
        pkt.rightThumbY = SDL_GetGamepadAxis(gp, SDL_GAMEPAD_AXIS_RIGHTY);
        ctx->transport->SendEncrypted(proto::AppPacketType::InputGamepad, &pkt, sizeof(pkt));
    }
}

OSMIUM_API void Osmium_ClientSetOverlayVisible(void* clientHandle, int32_t visible)
{
    if (!clientHandle) return;
    static_cast<EmbeddedClientContext*>(clientHandle)->decoder.SetOverlayVisible(visible != 0);
}

OSMIUM_API void Osmium_ClientSetVsync(void* clientHandle, int32_t enableVsync)
{
    if (!clientHandle) return;
    static_cast<EmbeddedClientContext*>(clientHandle)->decoder.SetVsync(enableVsync != 0);
}

OSMIUM_API void Osmium_ClientSetMute(void* clientHandle, int32_t muted)
{
    if (!clientHandle) return;
    static_cast<EmbeddedClientContext*>(clientHandle)->audioPlayer.SetMuted(muted != 0);
}

OSMIUM_API int32_t Osmium_ClientSendAuxData(void* clientHandle, const uint8_t* data, int32_t length)
{
    if (!clientHandle || !data || length <= 0 ||
        static_cast<size_t>(length) > proto::kMaxAuxDataPayload)
    {
        return 0;
    }

    auto* ctx = static_cast<EmbeddedClientContext*>(clientHandle);
    if (!ctx->transport || ctx->stopFlag.load(std::memory_order_acquire) ||
        ctx->remoteEnded.load(std::memory_order_acquire))
    {
        return 0;
    }

    uint8_t pkt[sizeof(proto::AppPacketHeader) + proto::kMaxAuxDataPayload];
    memset(pkt, 0, sizeof(proto::AppPacketHeader));
    memcpy(pkt + sizeof(proto::AppPacketHeader), data, static_cast<size_t>(length));
    return ctx->transport->SendEncrypted(
               proto::AppPacketType::AuxData,
               pkt,
               sizeof(proto::AppPacketHeader) + static_cast<size_t>(length))
               ? 1
               : 0;
}

OSMIUM_API int32_t Osmium_RunClientSession(
    void* transport,
    const OsmiumClientConfig* config,
    const OsmiumClientCallbacks* callbacks)
{
    if (!transport || !config) return -1;
    auto* t = static_cast<OsmiumTransport*>(transport);

    g_ClientStopRequested.store(false);
    g_ClientOverlayToggleRequested.store(false);

    if (!SDL_WasInit(SDL_INIT_VIDEO))
    {
        if (!SDL_InitSubSystem(SDL_INIT_VIDEO))
        {
            OSM_LOG_ERROR("SDL_InitSubSystem(VIDEO) failed: %s", SDL_GetError());
            return -2;
        }
    }

    int initW = config->targetWidth > 0 ? std::min(config->targetWidth, 1600) : 1280;
    int initH = config->targetHeight > 0 ? std::min(config->targetHeight, 900) : 720;

    SDL_WindowFlags winFlags = SDL_WINDOW_RESIZABLE | SDL_WINDOW_HIGH_PIXEL_DENSITY;
    if (config->windowMode == 0) winFlags |= SDL_WINDOW_FULLSCREEN;
    else if (config->windowMode == 1) winFlags |= SDL_WINDOW_BORDERLESS | SDL_WINDOW_MAXIMIZED;

    const char* title = (config->windowTitle && *config->windowTitle) ? config->windowTitle : "Osmium Remote Session";
    SDL_Window* window = SDL_CreateWindow(title, initW, initH, winFlags);
    if (!window) return -3;

    HWND hwnd = static_cast<HWND>(SDL_GetPointerProperty(SDL_GetWindowProperties(window),
                                                         SDL_PROP_WINDOW_WIN32_HWND_POINTER, nullptr));
    if (!hwnd)
    {
        SDL_DestroyWindow(window);
        return -4;
    }

    if (config->keepAwake) SDL_DisableScreenSaver();

    ClientVideoDecoder decoder;
    if (!decoder.InitializeRenderer(hwnd, config->enableVsync != 0, config->decoderPreference))
    {
        SDL_DestroyWindow(window);
        return -5;
    }
    decoder.SetOverlayVisible(config->showPerformanceOverlay != 0);

    ClientAudioPlayer audioPlayer;
    audioPlayer.Initialize();

    bool isFullscreen = (config->windowMode == 0);
    ClientInputCapture inputCapture;
    inputCapture.Initialize(window, t, *config, {
                                []() { g_ClientStopRequested.store(true); },
                                []() { g_ClientOverlayToggleRequested.store(true); },
                                [window, &isFullscreen]()
                                {
                                    isFullscreen = !isFullscreen;
                                    SDL_SetWindowFullscreen(window, isFullscreen);
                                }
                            });

    std::atomic<bool> recvStop{false};
    std::mutex queueMutex;
    std::condition_variable queueCv;
    std::deque<AssembledVideoFrame> frameQueue;
    SessionVideoDecodeGate decodeGate;
    std::atomic<uint32_t> receivedFrames{0};
    std::atomic<uint32_t> droppedFrames{0};
    std::atomic<uint64_t> bytesRecvWindow{0};
    std::atomic<bool> remoteEnded{false};

    std::thread recvThread([&]()
    {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
        std::map<uint32_t, PendingFrameChunks> pending;
        uint32_t lastCompletedKeyFrameId = 0;
        uint64_t lastStalePurgeMs = NowMillis();
        std::vector<uint8_t> plain;

        while (!recvStop.load() && !g_ClientStopRequested.load())
        {
            int r = t->ReceiveEncrypted(plain, 10);
            if (r < 0)
            {
                remoteEnded.store(true);
                break;
            }
            uint64_t nowMs = NowMillis();
            if (!pending.empty() && (nowMs - lastStalePurgeMs) >= 40)
            {
                lastStalePurgeMs = nowMs;
                const uint64_t rtt = t->GetLastRttMs();
                const uint64_t staleTimeoutMs = std::clamp<uint64_t>(rtt * 3ULL + 80ULL, 120ULL, 450ULL);
                bool purged = false;
                for (auto it = pending.begin(); it != pending.end();)
                {
                    if (nowMs - it->second.createdMs > staleTimeoutMs)
                    {
                        it = pending.erase(it);
                        purged = true;
                    }
                    else
                    {
                        ++it;
                    }
                }
                if (purged)
                {
                    droppedFrames.fetch_add(1);
                }
            }
            if (r == 0 || plain.size() < sizeof(proto::AppPacketHeader)) continue;

            bytesRecvWindow.fetch_add(plain.size());
            auto type = static_cast<proto::AppPacketType>(plain[0]);

            if (type == proto::AppPacketType::VideoChunk && plain.size() > sizeof(proto::VideoChunkHeader))
            {
                const auto* hdr = reinterpret_cast<const proto::VideoChunkHeader*>(plain.data());
                AssembledVideoFrame frame;
                if (TryAssembleVideoChunk(
                        pending,
                        lastCompletedKeyFrameId,
                        hdr,
                        plain.data() + sizeof(proto::VideoChunkHeader),
                        plain.size() - sizeof(proto::VideoChunkHeader),
                        nowMs,
                        frame))
                {
                    receivedFrames.fetch_add(1);
                    uint32_t queueDropped;
                    {
                        std::lock_guard<std::mutex> ql(queueMutex);
                        queueDropped = decodeGate.Enqueue(frameQueue, std::move(frame));
                    }
                    if (queueDropped) droppedFrames.fetch_add(queueDropped);
                    queueCv.notify_one();
                }
            }
            else if (type == proto::AppPacketType::AudioFrame && plain.size() >= sizeof(proto::AudioFrameHeader))
            {
                const auto* hdr = reinterpret_cast<const proto::AudioFrameHeader*>(plain.data());
                if (plain.size() >= sizeof(proto::AudioFrameHeader) + hdr->payloadBytes)
                {
                    audioPlayer.PushOpusFrame(
                        plain.data() + sizeof(proto::AudioFrameHeader),
                        hdr->payloadBytes, hdr->sampleRate, hdr->channels, hdr->sampleCount);
                }
            }
            else if (type == proto::AppPacketType::Pong && plain.size() >= sizeof(proto::PingPongPayload))
            {
                const auto* pong = reinterpret_cast<const proto::PingPongPayload*>(plain.data());
                t->RecordRttMs(static_cast<uint32_t>((NowMicros() - pong->timestampUs) / 1000ULL));
            }
            else if (type == proto::AppPacketType::Disconnect)
            {
                remoteEnded.store(true);
                break;
            }
        }
    });

    SendReliableIdrRequest(t);

    OsmiumClientStats stats{};
    stats.transportRoute = t->GetTransportRoute();
    CopyStringSafe(stats.peerEndpoint, sizeof(stats.peerEndpoint), t->GetActiveEndpointString());

    uint32_t decodedTotal = 0, renderedWindow = 0;
    double avgDecodeMs = 0.0;
    bool firedStarted = false, hasRenderedFirstFrame = false;
    uint64_t lastIdrReqMs = 0, lastStatsMs = NowMillis(), lastPingMs = 0, lastGamepadPollMs = 0;

    while (!g_ClientStopRequested.load() && !remoteEnded.load())
    {
        SDL_Event ev;
        while (SDL_PollEvent(&ev))
        {
            if (ev.type == SDL_EVENT_QUIT || ev.type == SDL_EVENT_WINDOW_CLOSE_REQUESTED)
            {
                g_ClientStopRequested.store(true);
                break;
            }
            if (ev.type == SDL_EVENT_WINDOW_RESIZED) decoder.OnWindowResized(ev.window.data1, ev.window.data2);
            else if (ev.type == SDL_EVENT_WINDOW_FOCUS_GAINED)
            {
                inputCapture.OnFocusChanged(true, isFullscreen);
                if (config->muteOnFocusLoss) audioPlayer.SetMuted(false);
            }
            else if (ev.type == SDL_EVENT_WINDOW_FOCUS_LOST)
            {
                inputCapture.OnFocusChanged(false, isFullscreen);
                if (config->muteOnFocusLoss) audioPlayer.SetMuted(true);
            }
            inputCapture.ProcessSdlEvent(ev);
        }

        if (g_ClientOverlayToggleRequested.exchange(false)) decoder.ToggleOverlay();

        uint64_t nowMs = NowMillis();
        if (nowMs - lastGamepadPollMs >= 8)
        {
            lastGamepadPollMs = nowMs;
            inputCapture.PollGamepads();
        }
        if (nowMs - lastPingMs >= 500)
        {
            lastPingMs = nowMs;
            proto::PingPongPayload ping{};
            ping.timestampUs = NowMicros();
            t->SendEncrypted(proto::AppPacketType::Ping, &ping, sizeof(ping));
        }

        AssembledVideoFrame nextFrame;
        bool hasFrame = false;
        size_t queuedAfterPop = 0;
        {
            std::unique_lock<std::mutex> ql(queueMutex);
            if (frameQueue.empty()) queueCv.wait_for(ql, std::chrono::milliseconds(4));
            uint32_t gateDropped = 0;
            hasFrame = decodeGate.SelectNext(
                frameQueue, NowMillis(), gateDropped, nextFrame);
            queuedAfterPop = frameQueue.size();
            if (gateDropped != 0) droppedFrames.fetch_add(gateDropped);
        }

        if (!hasFrame)
        {
            decoder.RepaintIfResized();
            if ((!hasRenderedFirstFrame || decodeGate.waitingForKeyframe) && (nowMs - lastIdrReqMs >= 150))
            {
                lastIdrReqMs = nowMs;
                SendReliableIdrRequest(t);
            }
            // Reorder grace leaves frames queued but nothing decodable; yield
            // instead of spinning the render loop.
            if (queuedAfterPop > 0) Sleep(1);
        }
        else
        {
            if (!hasRenderedFirstFrame && !nextFrame.isKeyFrame)
            {
                if (nowMs - lastIdrReqMs >= 150)
                {
                    lastIdrReqMs = nowMs;
                    SendReliableIdrRequest(t);
                }
            }
            else
            {
                double decMs = 0.0;
                stats.videoWidth = nextFrame.width;
                stats.videoHeight = nextFrame.height;
                stats.receivedFrames = receivedFrames.load();
                stats.droppedFrames = droppedFrames.load();
                stats.rttMs = t->GetLastRttMs();

                bool ok = decoder.DecodeAndRenderFrame(
                    nextFrame.codecId, nextFrame.isKeyFrame, nextFrame.width, nextFrame.height, nextFrame.colorFlags,
                    nextFrame.payload.data(), nextFrame.payload.size(), stats, decMs, queuedAfterPop > 0);

                if (ok)
                {
                    hasRenderedFirstFrame = true;
                    decodedTotal++;
                    renderedWindow++;
                    avgDecodeMs = avgDecodeMs * 0.85 + decMs * 0.15;
                    if (!firedStarted)
                    {
                        firedStarted = true;
                        if (callbacks && callbacks->onStarted)
                        {
                            callbacks->onStarted(nextFrame.width, nextFrame.height,
                                                 decoder.GetCodecName().c_str(), decoder.GetDecoderName().c_str());
                        }
                    }
                }
                else if (nowMs - lastIdrReqMs >= 200)
                {
                    lastIdrReqMs = nowMs;
                    SendReliableIdrRequest(t);
                }
            }
        }

        if (nowMs - lastStatsMs >= 500)
        {
            double sec = static_cast<double>(nowMs - lastStatsMs) / 1000.0;
            lastStatsMs = nowMs;
            uint64_t bytesWin = bytesRecvWindow.exchange(0);

            stats.receivedFrames = receivedFrames.load();
            stats.decodedFrames = decodedTotal;
            stats.renderedFrames = decodedTotal;
            stats.droppedFrames = droppedFrames.load();
            stats.fps = static_cast<double>(renderedWindow) / sec;
            stats.bitrateMbps = (static_cast<double>(bytesWin) * 8.0) / (sec * 1000000.0);
            stats.decodeLatencyMs = avgDecodeMs;
            stats.rttMs = t->GetLastRttMs();
            CopyStringSafe(stats.codecName, sizeof(stats.codecName), decoder.GetCodecName());
            CopyStringSafe(stats.decoderName, sizeof(stats.decoderName), decoder.GetDecoderName());
            renderedWindow = 0;

            if (callbacks && callbacks->onStatsUpdated) callbacks->onStatsUpdated(&stats);
        }
    }

    recvStop.store(true);
    proto::AppPacketHeader bye{};
    for (int i = 0; i < 4; ++i)
    {
        t->SendEncrypted(proto::AppPacketType::Disconnect, &bye, sizeof(bye));
        if (i < 3) Sleep(3);
    }
    t->Close();
    if (recvThread.joinable()) recvThread.join();

    inputCapture.Shutdown();
    audioPlayer.Shutdown();
    decoder.Shutdown();
    if (config->keepAwake) SDL_EnableScreenSaver();
    SDL_DestroyWindow(window);

    if (callbacks && callbacks->onEnded)
    {
        callbacks->onEnded(0, remoteEnded.load() ? "Host ended the session" : "Session closed by user");
    }
    return 0;
}

OSMIUM_API void Osmium_StopClientSession(void)
{
    g_ClientStopRequested.store(true);
}

OSMIUM_API void Osmium_ToggleClientOverlay(void)
{
    g_ClientOverlayToggleRequested.store(true);
}
