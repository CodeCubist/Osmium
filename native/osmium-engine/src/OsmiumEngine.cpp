#include "OsmiumNative.h"
#include "OsmiumCommon.h"
#include "OsmiumProtocol.h"
#include "OsmiumTransport.h"
#include "DriverSetupHelper.h"
#include "UsbIpVirtualDevices.h"
#include "HostSystemAgent.h"
#include "HostCapture.h"
#include "HostVideoEncoder.h"
#include "HostAudioCapture.h"
#include "HostInputInjector.h"
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
#include <map>
#include <memory>
#include <mutex>
#include <objbase.h>
#include <sstream>
#include <thread>
#include <vector>

using namespace osmium;

namespace
{
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

    struct HostSessionContext
    {
        OsmiumTransport* transport = nullptr;
        OsmiumHostConfig config{};
        OsmiumClientConfig clientProfile{};
        OsmiumHostCallbacks callbacks{};

        HostCapture capture;
        HostVideoEncoder encoder;
        HostAudioCapture audio;
        HostInputInjector injector;
        UsbIpVirtualDevices virtualUsb;
        HostSystemAgent systemAgent;

        std::mutex cfgMutex;
        int32_t activeDisplayIndex = 0;
        int32_t reqWidth = 0;
        int32_t reqHeight = 0;
        int32_t reqFps = 60;
        int32_t reqBitrateKbps = 20000;
        bool reqHdr = false;
        bool reqYuv444 = false;
        bool lastHostHdr = false;
        int32_t mouseMode = 0;
        bool hideHostCursorInHostMode = true;
        bool smartEngaged = false;
        bool virtualMouseRequested = false;
        bool virtualSpeakerRequested = false;
        bool virtualMicRequested = false;
        std::string lastActionMsg;

        std::atomic<bool> reconfigNeeded{false};
        std::atomic<bool> statusDirty{true};
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
        int32_t fps = 60;
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
            fps = ctx->reqFps;
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
            << ",\"fps\":" << fps
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
            << ",\"lastAction\":\"" << EscapeJson(lastAct.c_str()) << "\""
            << ",\"displays\":[";

        size_t maxDisp = std::min<size_t>(displays.size(), 6);
        for (size_t i = 0; i < maxDisp; ++i)
        {
            const auto& d = displays[i];
            if (i > 0) oss << ",";
            oss << "{\"index\":" << d.index
                << ",\"width\":" << d.width
                << ",\"height\":" << d.height
                << ",\"refreshRate\":" << d.refreshRate
                << ",\"isPrimary\":" << (d.isPrimary ? "true" : "false")
                << ",\"name\":\"" << EscapeJson(d.name) << "\""
                << ",\"deviceName\":\"" << EscapeJson(d.deviceName) << "\"}";
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

            if (action == static_cast<int32_t>(proto::SystemActionId::SendCtrlAltDel))
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
            else if (action == static_cast<int32_t>(proto::SystemActionId::TriggerUnlock))
            {
                bool ok = ctx->systemAgent.TriggerAutoUnlockNow();
                std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                ctx->lastActionMsg = ok ? "UNLOCKED" : "UNLOCK_TRIGGERED";
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
    }

    void HostVideoLoop(HostSessionContext* ctx)
    {
        ctx->injector.SetVirtualDevices(&ctx->virtualUsb);
        ctx->audio.SetVirtualDevices(&ctx->virtualUsb);

        {
            std::lock_guard<std::mutex> lock(ctx->cfgMutex);
            ctx->activeDisplayIndex = ctx->config.displayIndex;
            ctx->reqWidth = ctx->config.maxWidth > 0 ? ctx->config.maxWidth : ctx->clientProfile.targetWidth;
            ctx->reqHeight = ctx->config.maxHeight > 0 ? ctx->config.maxHeight : ctx->clientProfile.targetHeight;
            ctx->reqFps = ctx->config.maxFps > 0 ? ctx->config.maxFps : 60;
            if (ctx->clientProfile.fps > 0 && ctx->clientProfile.fps < ctx->reqFps)
            {
                ctx->reqFps = ctx->clientProfile.fps;
            }
            ctx->reqFps = std::clamp(ctx->reqFps, 10, 240);
            ctx->reqBitrateKbps = ctx->config.maxBitrateKbps > 0 ? ctx->config.maxBitrateKbps : 20000;
            if (ctx->clientProfile.bitrateKbps > 0 && ctx->clientProfile.bitrateKbps < ctx->reqBitrateKbps)
            {
                ctx->reqBitrateKbps = ctx->clientProfile.bitrateKbps;
            }
            ctx->reqBitrateKbps = std::clamp(ctx->reqBitrateKbps, 500, 500000);
            ctx->reqHdr = ctx->clientProfile.enableHdr != 0;
            ctx->reqYuv444 = ctx->clientProfile.enableYuv444 != 0;
            ctx->mouseMode = ctx->clientProfile.mouseMode;
            ctx->hideHostCursorInHostMode = (ctx->mouseMode == 0);
        }

        auto reconfigurePipeline = [&](bool forceInitCapture) -> bool
        {
            int dispIdx = 0, reqW = 0, reqH = 0, fps = 60, bitrate = 20000;
            bool hdr = false, yuv444 = false;
            {
                std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                dispIdx = ctx->activeDisplayIndex;
                reqW = ctx->reqWidth;
                reqH = ctx->reqHeight;
                fps = ctx->reqFps;
                bitrate = ctx->reqBitrateKbps;
                hdr = ctx->reqHdr;
                yuv444 = ctx->reqYuv444;
                ApplyHostMouseModeLocked(ctx);
            }

            if (forceInitCapture || dispIdx != ctx->capture.GetDisplayIndex())
            {
                bool capCursor = ctx->virtualMouseRequested || !ctx->hideHostCursorInHostMode;
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
            }

            ctx->injector.SetEnabled(ctx->config.allowInputControl != 0);
            ctx->capture.RefreshOutputHdrState();
            bool hostHdr = ctx->capture.IsOutputHdr();
            bool effectiveHdr = hdr && hostHdr;
            ctx->lastHostHdr = hostHdr;

            int capW = ctx->capture.GetWidth();
            int capH = ctx->capture.GetHeight();
            int outW = capW;
            int outH = capH;

            if (reqW > 0 && reqH > 0 && (capW > reqW || capH > reqH))
            {
                double scale = std::min(1.0, std::min(static_cast<double>(reqW) / capW,
                                                      static_cast<double>(reqH) / capH));
                outW = std::max(256, static_cast<int>(capW * scale) & ~1);
                outH = std::max(144, static_cast<int>(capH * scale) & ~1);
            }

            int codecPref = ctx->clientProfile.codecPreference != 0
                                ? ctx->clientProfile.codecPreference
                                : ctx->config.codecPreference;
            if (!ctx->encoder.Initialize(capW, capH, outW, outH, fps, bitrate, codecPref, ctx->config.encoderPreference,
                                         effectiveHdr, yuv444))
            {
                return false;
            }
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
        double avgEncodeMs = 0.0;
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
                ctx->systemAgent.TickAutoUnlock();
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

            if (ctx->statusDirty.exchange(false) || (nowMs - lastStatusPushMs >= 2000))
            {
                lastStatusPushMs = nowMs;
                PushHostStatusJson(ctx);
            }

            int curFps = 60;
            {
                std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                curFps = std::clamp(ctx->reqFps, 10, 240);
            }
            const uint64_t frameIntervalUs = 1000000ULL / static_cast<uint64_t>(curFps);
            const bool wantPqCapture = ctx->encoder.IsPqHdrActive();

            if (ctx->capture.CaptureNextFrame(capFrame, 16, wantPqCapture))
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
                if (capFrame.width <= 0 || capFrame.height <= 0 || capFrame.stride <= 0 ||
                    capFrame.bgra.size() < static_cast<size_t>(capFrame.stride) * static_cast<size_t>(capFrame.height))
                {
                    continue;
                }
                if (capFrame.width != ctx->encoder.GetSrcWidth() || capFrame.height != ctx->encoder.GetSrcHeight())
                {
                    OSM_LOG_INFO("Host capture geometry changed (%dx%d -> %dx%d); reconfiguring encoder",
                                 ctx->encoder.GetSrcWidth(), ctx->encoder.GetSrcHeight(), capFrame.width,
                                 capFrame.height);
                    if (!reconfigurePipeline(false))
                    {
                        Sleep(50);
                        continue;
                    }
                }
                if (capFrame.width != ctx->encoder.GetSrcWidth() || capFrame.height != ctx->encoder.GetSrcHeight() ||
                    (ctx->encoder.IsPqHdrActive() != capFrame.isHdr10Pq))
                {
                    continue;
                }

                uint64_t encStartUs = NowMicros();
                if (ctx->encoder.EncodeFrame(capFrame.bgra.data(), capFrame.stride, capFrame.isHdr10Pq, encodedPackets))
                {
                    double encMs = static_cast<double>(NowMicros() - encStartUs) / 1000.0;
                    avgEncodeMs = (avgEncodeMs * 0.85) + (encMs * 0.15);

                    uint8_t cFlags = 0;
                    if (ctx->encoder.IsPqHdrActive() && capFrame.isHdr10Pq) cFlags |= proto::kVideoFlagHdr;
                    if (ctx->encoder.IsYuv444Active()) cFlags |= proto::kVideoFlagYuv444;

                    for (const auto& ep : encodedPackets)
                    {
                        if (ep.data.empty()) continue;
                        uint32_t frameId = ctx->frameIdCounter.fetch_add(1);
                        size_t totalChunks = (ep.data.size() + proto::kMaxVideoChunkPayload - 1) /
                            proto::kMaxVideoChunkPayload;
                        if (totalChunks > 65535) continue;

                        for (size_t ci = 0; ci < totalChunks; ++ci)
                        {
                            size_t offset = ci * proto::kMaxVideoChunkPayload;
                            size_t sliceLen = std::min(proto::kMaxVideoChunkPayload, ep.data.size() - offset);

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
                            if ((ci & 0x0F) == 0x0F)
                            {
                                Sleep(0);
                            }
                        }

                        ctx->framesEncodedWindow.fetch_add(1);
                        ctx->totalEncodedFrames.fetch_add(1);
                    }
                }
                else
                {
                    ctx->droppedFrames.fetch_add(1);
                }
            }

            nowMs = NowMillis();
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

            uint64_t elapsedUs = NowMicros() - loopStartUs;
            if (elapsedUs < frameIntervalUs)
            {
                uint64_t sleepMs = (frameIntervalUs - elapsedUs) / 1000ULL;
                if (sleepMs > 0) Sleep(static_cast<DWORD>(sleepMs));
            }
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
        std::vector<uint8_t> plain;
        uint64_t lastPeerRxMs = NowMillis();
        constexpr uint64_t kClientKeepaliveTimeoutMs = 6000;

        while (!ctx->stopFlag.load())
        {
            int r = ctx->transport->ReceiveEncrypted(plain, 50);
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
            auto type = static_cast<proto::AppPacketType>(plain[0]);
            if (type == proto::AppPacketType::VideoIdrRequest)
            {
                ctx->encoder.ForceKeyFrame();
            }
            else if (type == proto::AppPacketType::Ping && plain.size() >= sizeof(proto::PingPongPayload))
            {
                auto pong = *reinterpret_cast<const proto::PingPongPayload*>(plain.data());
                ctx->transport->SendEncrypted(proto::AppPacketType::Pong, &pong, sizeof(pong));
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
                {
                    std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                    ctx->activeDisplayIndex = p->displayIndex;
                }
                ctx->reconfigNeeded.store(true);
            }
            else if (type == proto::AppPacketType::ControlVideoParams && plain.size() >= sizeof(
                proto::ControlVideoParamsPayload))
            {
                const auto* p = reinterpret_cast<const proto::ControlVideoParamsPayload*>(plain.data());
                {
                    std::lock_guard<std::mutex> lock(ctx->cfgMutex);
                    if (p->targetWidth >= 0) ctx->reqWidth = p->targetWidth;
                    if (p->targetHeight >= 0) ctx->reqHeight = p->targetHeight;
                    if (p->fps > 0) ctx->reqFps = std::clamp(p->fps, 10, 240);
                    if (p->bitrateKbps > 0) ctx->reqBitrateKbps = std::clamp(p->bitrateKbps, 500, 500000);
                    ctx->reqHdr = (p->enableHdr != 0);
                    ctx->reqYuv444 = (p->enableYuv444 != 0);
                }
                ctx->reconfigNeeded.store(true);
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
            else if (type == proto::AppPacketType::ControlAutoUnlock && plain.size() >= sizeof(
                proto::ControlAutoUnlockPayload))
            {
                const auto* p = reinterpret_cast<const proto::ControlAutoUnlockPayload*>(plain.data());
                size_t secBytes = std::min<size_t>(p->secretBytes,
                                                   plain.size() - sizeof(proto::ControlAutoUnlockPayload));
                std::string sec(reinterpret_cast<const char*>(plain.data() + sizeof(proto::ControlAutoUnlockPayload)),
                                secBytes);
                ctx->systemAgent.SetAutoUnlockCredential(p->enabled != 0, sec.c_str());
                if (!sec.empty())
                    SecureZeroMemory(sec.data(), sec.size());
                ctx->systemAgent.TickAutoUnlock();
                ctx->statusDirty.store(true);
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
        uint64_t createdMs = 0;
        std::vector<std::vector<uint8_t>> chunks;
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

    void EmbeddedClientRecvLoop(EmbeddedClientContext* ctx)
    {
        std::map<uint32_t, PendingFrameChunks> pending;
        uint32_t lastCompletedFrameId = 0;
        uint64_t lastStalePurgeMs = NowMillis();
        std::vector<uint8_t> plain;

        while (!ctx->stopFlag.load())
        {
            int r = ctx->transport->ReceiveEncrypted(plain, 25);
            if (r < 0)
            {
                ctx->remoteEnded.store(true);
                ctx->queueCv.notify_all();
                break;
            }
            uint64_t nowMs = NowMillis();
            if (!pending.empty() && (nowMs - lastStalePurgeMs) >= 150)
            {
                lastStalePurgeMs = nowMs;
                bool purged = false;
                for (auto it = pending.begin(); it != pending.end();)
                {
                    if (nowMs - it->second.createdMs > 500)
                    {
                        if (it->first > lastCompletedFrameId)
                        {
                            lastCompletedFrameId = it->first;
                        }
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
                    proto::AppPacketHeader idrReq{};
                    ctx->transport->SendEncrypted(proto::AppPacketType::VideoIdrRequest, &idrReq, sizeof(idrReq));
                }
            }
            if (r == 0 || plain.size() < sizeof(proto::AppPacketHeader)) continue;

            ctx->bytesRecvWindow.fetch_add(plain.size());
            auto type = static_cast<proto::AppPacketType>(plain[0]);

            if (type == proto::AppPacketType::VideoChunk && plain.size() > sizeof(proto::VideoChunkHeader))
            {
                const auto* hdr = reinterpret_cast<const proto::VideoChunkHeader*>(plain.data());
                if (hdr->frameId <= lastCompletedFrameId || hdr->totalChunks == 0 || hdr->chunkIndex >= hdr->
                    totalChunks)
                {
                    continue;
                }

                auto& pf = pending[hdr->frameId];
                if (pf.chunks.empty())
                {
                    pf.codecId = static_cast<proto::VideoCodecId>(hdr->codecId);
                    pf.isKeyFrame = hdr->isKeyFrame != 0;
                    pf.width = hdr->width;
                    pf.height = hdr->height;
                    pf.colorFlags = hdr->colorFlags;
                    pf.totalChunks = hdr->totalChunks;
                    pf.chunks.resize(hdr->totalChunks);
                    pf.createdMs = NowMillis();
                }

                if (pf.chunks[hdr->chunkIndex].empty())
                {
                    size_t dataBytes = plain.size() - sizeof(proto::VideoChunkHeader);
                    const uint8_t* ptr = plain.data() + sizeof(proto::VideoChunkHeader);
                    pf.chunks[hdr->chunkIndex].assign(ptr, ptr + dataBytes);
                    pf.receivedCount++;
                }

                if (pf.receivedCount == pf.totalChunks)
                {
                    AssembledVideoFrame frame;
                    frame.frameId = hdr->frameId;
                    frame.codecId = pf.codecId;
                    frame.isKeyFrame = pf.isKeyFrame;
                    frame.width = pf.width;
                    frame.height = pf.height;
                    frame.colorFlags = pf.colorFlags;

                    size_t totalSz = 0;
                    for (const auto& c : pf.chunks) totalSz += c.size();
                    frame.payload.reserve(totalSz);
                    for (const auto& c : pf.chunks)
                    {
                        frame.payload.insert(frame.payload.end(), c.begin(), c.end());
                    }

                    lastCompletedFrameId = hdr->frameId;
                    pending.erase(hdr->frameId);
                    ctx->receivedFrames.fetch_add(1);

                    bool hadDrop = false;
                    for (auto it = pending.begin(); it != pending.end();)
                    {
                        if (it->first < lastCompletedFrameId)
                        {
                            it = pending.erase(it);
                            hadDrop = true;
                        }
                        else
                        {
                            ++it;
                        }
                    }
                    if (hadDrop)
                    {
                        ctx->droppedFrames.fetch_add(1);
                        proto::AppPacketHeader idrReq{};
                        ctx->transport->SendEncrypted(proto::AppPacketType::VideoIdrRequest, &idrReq, sizeof(idrReq));
                    }

                    {
                        std::lock_guard<std::mutex> ql(ctx->queueMutex);
                        if (frame.isKeyFrame)
                        {
                            ctx->frameQueue.clear();
                        }
                        ctx->frameQueue.push_back(std::move(frame));
                    }
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
            else if (type == proto::AppPacketType::Disconnect)
            {
                ctx->remoteEnded.store(true);
                ctx->queueCv.notify_all();
                break;
            }
        }
    }

    void EmbeddedClientRenderLoop(EmbeddedClientContext* ctx)
    {
        OsmiumClientStats stats{};
        stats.transportRoute = ctx->transport->GetTransportRoute();
        stats.localPort = static_cast<int32_t>(ctx->transport->GetLocalPort());
        CopyStringSafe(stats.peerEndpoint, sizeof(stats.peerEndpoint), ctx->transport->GetActiveEndpointString());
        CopyStringSafe(stats.localEndpoint, sizeof(stats.localEndpoint), ctx->transport->GetLocalEndpointString());

        uint32_t decodedTotal = 0;
        uint32_t renderedWindow = 0;
        double avgDecodeMs = 0.0;
        bool firedStarted = false;
        bool waitingForKeyframe = true;
        uint64_t lastIdrReqMs = 0;
        uint64_t lastStatsMs = NowMillis();
        uint64_t lastPingMs = 0;

        {
            proto::AppPacketHeader idrReq{};
            ctx->transport->SendEncrypted(proto::AppPacketType::VideoIdrRequest, &idrReq, sizeof(idrReq));
        }

        while (!ctx->stopFlag.load() && !ctx->remoteEnded.load())
        {
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
            {
                std::unique_lock<std::mutex> ql(ctx->queueMutex);
                if (ctx->frameQueue.empty())
                {
                    ctx->queueCv.wait_for(ql, std::chrono::milliseconds(8));
                }
                if (!ctx->frameQueue.empty())
                {
                    nextFrame = std::move(ctx->frameQueue.front());
                    ctx->frameQueue.pop_front();
                    hasFrame = true;
                }
            }

            if (!hasFrame)
            {
                ctx->decoder.RepaintIfResized();
                if (waitingForKeyframe && (nowMs - lastIdrReqMs >= 350))
                {
                    lastIdrReqMs = nowMs;
                    proto::AppPacketHeader idrReq{};
                    ctx->transport->SendEncrypted(proto::AppPacketType::VideoIdrRequest, &idrReq, sizeof(idrReq));
                }
            }
            else
            {
                if (waitingForKeyframe && !nextFrame.isKeyFrame)
                {
                    if (nowMs - lastIdrReqMs >= 250)
                    {
                        lastIdrReqMs = nowMs;
                        proto::AppPacketHeader idrReq{};
                        ctx->transport->SendEncrypted(proto::AppPacketType::VideoIdrRequest, &idrReq, sizeof(idrReq));
                    }
                }
                else
                {
                    waitingForKeyframe = false;
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
                        decMs);

                    if (ok)
                    {
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
                    else
                    {
                        waitingForKeyframe = true;
                        if (nowMs - lastIdrReqMs >= 250)
                        {
                            lastIdrReqMs = nowMs;
                            proto::AppPacketHeader idrReq{};
                            ctx->transport->SendEncrypted(proto::AppPacketType::VideoIdrRequest, &idrReq,
                                                          sizeof(idrReq));
                        }
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

OSMIUM_API int32_t Osmium_RunOneShotUnlockService(uint32_t parentPid)
{
    return HostSystemAgent::RunOneShotUnlockServiceMain(parentPid);
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
        if (now - lastSend >= 250)
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
                for (int i = 0; i < 3; ++i)
                {
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
        ctx->transport->SendEncrypted(proto::AppPacketType::ControlVideoParams, &vp, sizeof(vp));

        proto::ControlMouseModePayload mp{};
        mp.mouseMode = config->mouseMode;
        mp.hideHostCursor = (config->mouseMode == 0) ? 1 : 0;
        ctx->transport->SendEncrypted(proto::AppPacketType::ControlMouseMode, &mp, sizeof(mp));
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
    ctx->transport->SendEncrypted(proto::AppPacketType::ControlSwitchDisplay, &pkt, sizeof(pkt));
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
    ctx->transport->SendEncrypted(proto::AppPacketType::ControlVideoParams, &pkt, sizeof(pkt));
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
    ctx->transport->SendEncrypted(proto::AppPacketType::ControlMouseMode, &pkt, sizeof(pkt));
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
    ctx->transport->SendEncrypted(proto::AppPacketType::ControlVirtualAudio, &pkt, sizeof(pkt));

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
    ctx->transport->SendEncrypted(proto::AppPacketType::ControlSystemAction, &pkt, sizeof(pkt));
}

OSMIUM_API void Osmium_ClientSetAutoUnlock(void* clientHandle, int32_t enabled, const char* secretUtf8)
{
    if (!clientHandle) return;
    auto* ctx = static_cast<EmbeddedClientContext*>(clientHandle);
    if (!ctx->transport) return;
    std::string sec = (secretUtf8 && *secretUtf8) ? std::string(secretUtf8) : std::string();
    if (sec.size() > 256) sec.resize(256);

    std::vector<uint8_t> pkt(sizeof(proto::ControlAutoUnlockPayload) + sec.size());
    auto* hdr = reinterpret_cast<proto::ControlAutoUnlockPayload*>(pkt.data());
    memset(hdr, 0, sizeof(*hdr));
    hdr->enabled = enabled ? 1 : 0;
    hdr->secretBytes = static_cast<uint16_t>(sec.size());
    if (!sec.empty())
    {
        memcpy(pkt.data() + sizeof(proto::ControlAutoUnlockPayload), sec.data(), sec.size());
        SecureZeroMemory(sec.data(), sec.size());
    }
    ctx->transport->SendEncrypted(proto::AppPacketType::ControlAutoUnlock, pkt.data(), pkt.size());
    if (!pkt.empty())
        SecureZeroMemory(pkt.data(), pkt.size());
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
    ctx->transport->SendEncrypted(proto::AppPacketType::InputKey, &pkt, sizeof(pkt));
}

OSMIUM_API void Osmium_ClientSendMouseMove(void* clientHandle, int32_t isAbsolute, float xOrDeltaX, float yOrDeltaY)
{
    if (!clientHandle) return;
    auto* ctx = static_cast<EmbeddedClientContext*>(clientHandle);
    if (!ctx->transport) return;
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
    proto::InputMouseButtonPayload pkt{};
    pkt.button = button;
    pkt.isDown = isDown ? 1 : 0;
    ctx->transport->SendEncrypted(proto::AppPacketType::InputMouseButton, &pkt, sizeof(pkt));
}

OSMIUM_API void Osmium_ClientSendMouseWheel(void* clientHandle, int16_t deltaX, int16_t deltaY)
{
    if (!clientHandle) return;
    auto* ctx = static_cast<EmbeddedClientContext*>(clientHandle);
    if (!ctx->transport) return;
    proto::InputMouseWheelPayload pkt{};
    pkt.deltaX = deltaX;
    pkt.deltaY = deltaY;
    ctx->transport->SendEncrypted(proto::AppPacketType::InputMouseWheel, &pkt, sizeof(pkt));
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
    std::atomic<uint32_t> receivedFrames{0};
    std::atomic<uint32_t> droppedFrames{0};
    std::atomic<uint64_t> bytesRecvWindow{0};
    std::atomic<bool> remoteEnded{false};

    std::thread recvThread([&]()
    {
        std::map<uint32_t, PendingFrameChunks> pending;
        uint32_t lastCompletedFrameId = 0;
        uint64_t lastStalePurgeMs = NowMillis();
        std::vector<uint8_t> plain;

        while (!recvStop.load() && !g_ClientStopRequested.load())
        {
            int r = t->ReceiveEncrypted(plain, 25);
            if (r < 0)
            {
                remoteEnded.store(true);
                break;
            }
            uint64_t nowMs = NowMillis();
            if (!pending.empty() && (nowMs - lastStalePurgeMs) >= 150)
            {
                lastStalePurgeMs = nowMs;
                bool purged = false;
                for (auto it = pending.begin(); it != pending.end();)
                {
                    if (nowMs - it->second.createdMs > 500)
                    {
                        if (it->first > lastCompletedFrameId) lastCompletedFrameId = it->first;
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
                    proto::AppPacketHeader idrReq{};
                    t->SendEncrypted(proto::AppPacketType::VideoIdrRequest, &idrReq, sizeof(idrReq));
                }
            }
            if (r == 0 || plain.size() < sizeof(proto::AppPacketHeader)) continue;

            bytesRecvWindow.fetch_add(plain.size());
            auto type = static_cast<proto::AppPacketType>(plain[0]);

            if (type == proto::AppPacketType::VideoChunk && plain.size() > sizeof(proto::VideoChunkHeader))
            {
                const auto* hdr = reinterpret_cast<const proto::VideoChunkHeader*>(plain.data());
                if (hdr->frameId <= lastCompletedFrameId || hdr->totalChunks == 0 || hdr->chunkIndex >= hdr->
                    totalChunks) continue;

                auto& pf = pending[hdr->frameId];
                if (pf.chunks.empty())
                {
                    pf.codecId = static_cast<proto::VideoCodecId>(hdr->codecId);
                    pf.isKeyFrame = hdr->isKeyFrame != 0;
                    pf.width = hdr->width;
                    pf.height = hdr->height;
                    pf.colorFlags = hdr->colorFlags;
                    pf.totalChunks = hdr->totalChunks;
                    pf.chunks.resize(hdr->totalChunks);
                    pf.createdMs = NowMillis();
                }

                if (pf.chunks[hdr->chunkIndex].empty())
                {
                    size_t dataBytes = plain.size() - sizeof(proto::VideoChunkHeader);
                    const uint8_t* ptr = plain.data() + sizeof(proto::VideoChunkHeader);
                    pf.chunks[hdr->chunkIndex].assign(ptr, ptr + dataBytes);
                    pf.receivedCount++;
                }

                if (pf.receivedCount == pf.totalChunks)
                {
                    AssembledVideoFrame frame;
                    frame.frameId = hdr->frameId;
                    frame.codecId = pf.codecId;
                    frame.isKeyFrame = pf.isKeyFrame;
                    frame.width = pf.width;
                    frame.height = pf.height;
                    frame.colorFlags = pf.colorFlags;

                    size_t totalSz = 0;
                    for (const auto& c : pf.chunks) totalSz += c.size();
                    frame.payload.reserve(totalSz);
                    for (const auto& c : pf.chunks) frame.payload.insert(frame.payload.end(), c.begin(), c.end());

                    lastCompletedFrameId = hdr->frameId;
                    pending.erase(hdr->frameId);
                    receivedFrames.fetch_add(1);

                    bool hadDrop = false;
                    for (auto it = pending.begin(); it != pending.end();)
                    {
                        if (it->first < lastCompletedFrameId)
                        {
                            it = pending.erase(it);
                            hadDrop = true;
                        }
                        else ++it;
                    }
                    if (hadDrop)
                    {
                        droppedFrames.fetch_add(1);
                        proto::AppPacketHeader idrReq{};
                        t->SendEncrypted(proto::AppPacketType::VideoIdrRequest, &idrReq, sizeof(idrReq));
                    }

                    {
                        std::lock_guard<std::mutex> ql(queueMutex);
                        if (frame.isKeyFrame) frameQueue.clear();
                        frameQueue.push_back(std::move(frame));
                    }
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

    {
        proto::AppPacketHeader idrReq{};
        t->SendEncrypted(proto::AppPacketType::VideoIdrRequest, &idrReq, sizeof(idrReq));
    }

    OsmiumClientStats stats{};
    stats.transportRoute = t->GetTransportRoute();
    CopyStringSafe(stats.peerEndpoint, sizeof(stats.peerEndpoint), t->GetActiveEndpointString());

    uint32_t decodedTotal = 0, renderedWindow = 0;
    double avgDecodeMs = 0.0;
    bool firedStarted = false, waitingForKeyframe = true;
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
        {
            std::unique_lock<std::mutex> ql(queueMutex);
            if (frameQueue.empty()) queueCv.wait_for(ql, std::chrono::milliseconds(4));
            if (!frameQueue.empty())
            {
                nextFrame = std::move(frameQueue.front());
                frameQueue.pop_front();
                hasFrame = true;
            }
        }

        if (!hasFrame)
        {
            decoder.RepaintIfResized();
            if (waitingForKeyframe && (nowMs - lastIdrReqMs >= 350))
            {
                lastIdrReqMs = nowMs;
                proto::AppPacketHeader idrReq{};
                t->SendEncrypted(proto::AppPacketType::VideoIdrRequest, &idrReq, sizeof(idrReq));
            }
        }
        else
        {
            if (waitingForKeyframe && !nextFrame.isKeyFrame)
            {
                if (nowMs - lastIdrReqMs >= 250)
                {
                    lastIdrReqMs = nowMs;
                    proto::AppPacketHeader idrReq{};
                    t->SendEncrypted(proto::AppPacketType::VideoIdrRequest, &idrReq, sizeof(idrReq));
                }
            }
            else
            {
                waitingForKeyframe = false;
                double decMs = 0.0;
                stats.videoWidth = nextFrame.width;
                stats.videoHeight = nextFrame.height;
                stats.receivedFrames = receivedFrames.load();
                stats.droppedFrames = droppedFrames.load();
                stats.rttMs = t->GetLastRttMs();

                bool ok = decoder.DecodeAndRenderFrame(
                    nextFrame.codecId, nextFrame.isKeyFrame, nextFrame.width, nextFrame.height, nextFrame.colorFlags,
                    nextFrame.payload.data(), nextFrame.payload.size(), stats, decMs);

                if (ok)
                {
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
                else
                {
                    waitingForKeyframe = true;
                    if (nowMs - lastIdrReqMs >= 250)
                    {
                        lastIdrReqMs = nowMs;
                        proto::AppPacketHeader idrReq{};
                        t->SendEncrypted(proto::AppPacketType::VideoIdrRequest, &idrReq, sizeof(idrReq));
                    }
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
