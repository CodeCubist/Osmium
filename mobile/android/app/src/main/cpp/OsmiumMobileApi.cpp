#include "OsmiumMobileApi.h"
#include "AndroidAudioEngine.h"
#include "AndroidVideoDecoder.h"
#include "MobileCrypto.h"
#include "MobileTransport.h"
#include "OsmiumProtocol.h"
#include "VideoReassembler.h"

#include <android/log.h>
#include <android/native_window.h>
#include <android/native_window_jni.h>
#include <jni.h>

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstring>
#include <deque>
#include <map>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, "OsmiumMobileApi", __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, "OsmiumMobileApi", __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, "OsmiumMobileApi", __VA_ARGS__)

namespace
{
    using namespace osmium::mobile;

    // Surface handle registry managed via JNI from Kotlin VideoSurfaceBridge
    std::mutex g_SurfaceMutex;
    int64_t g_NextSurfaceId = 1;
    std::map<int64_t, ANativeWindow*> g_Surfaces;

    ANativeWindow* AcquireSurfaceById(int64_t id)
    {
        if (id <= 0) return nullptr;
        std::lock_guard<std::mutex> lock(g_SurfaceMutex);
        auto it = g_Surfaces.find(id);
        if (it == g_Surfaces.end() || !it->second) return nullptr;
        ANativeWindow_acquire(it->second);
        return it->second;
    }

    struct QueuedEvent
    {
        int32_t type = 0;
        int32_t flags = 0;
        std::vector<uint8_t> payload;
    };

    struct MobileSessionContext
    {
        MobileTransport* transport = nullptr;
        OsmiumMobileClientConfig config{};
        AndroidVideoDecoder decoder;
        AndroidAudioEngine audio;
        VideoReassembler reassembler;

        std::atomic<bool> stopFlag{false};
        std::atomic<bool> firstFrameRendered{false};
        std::atomic<bool> sessionActive{true};

        std::mutex frameQueueMutex;
        std::condition_variable frameQueueCv;
        std::deque<AssembledVideoFrame> frameQueue;

        std::mutex eventMutex;
        std::deque<QueuedEvent> eventQueue;

        std::atomic<uint32_t> receivedFrames{0};
        std::atomic<uint32_t> droppedFrames{0};
        std::atomic<uint32_t> fps{0};
        std::atomic<uint32_t> bitrateKbps{0};
        std::atomic<uint32_t> decodeTimeUs{0};
        std::atomic<uint32_t> colorFlags{0};
        std::atomic<uint64_t> bytesRecvWindow{0};
        std::atomic<uint32_t> renderedFramesWindow{0};
        std::atomic<uint32_t> micSeqCounter{1};
        std::atomic<uint64_t> lastIdrRequestMs{0};

        std::thread recvThread;
        std::thread decodeThread;

        // Every request forces a host keyframe. Windows spaces them 150 ms apart
        // so a burst of loss does not turn into a burst of oversized frames.
        void RequestIdr(uint64_t minGapMs)
        {
            uint64_t nowMs = MobileTransport::NowMillis();
            uint64_t last = lastIdrRequestMs.load(std::memory_order_relaxed);
            if (last != 0 && nowMs - last < minGapMs) return;
            if (!lastIdrRequestMs.compare_exchange_strong(last, nowMs, std::memory_order_relaxed)) return;
            osmium::proto::AppPacketHeader idrReq{};
            transport->SendEncrypted(osmium::proto::AppPacketType::VideoIdrRequest, &idrReq, sizeof(idrReq));
        }

        void PushEvent(int32_t type, int32_t flags, const uint8_t* data, size_t len)
        {
            std::lock_guard<std::mutex> lock(eventMutex);
            if (eventQueue.size() > 256)
            {
                eventQueue.pop_front();
            }
            QueuedEvent ev;
            ev.type = type;
            ev.flags = flags;
            if (data && len > 0) ev.payload.assign(data, data + len);
            eventQueue.push_back(std::move(ev));
        }

        void PushStringEvent(int32_t type, const std::string& s)
        {
            PushEvent(type, 0, reinterpret_cast<const uint8_t*>(s.data()), s.size());
        }
    };

    void RecvLoop(MobileSessionContext* ctx)
    {
        uint64_t lastPingMs = 0;
        uint64_t lastStatsMs = MobileTransport::NowMillis();
        uint64_t lastRxMs = lastStatsMs;
        std::vector<uint8_t> plain;

        // Immediately request an IDR keyframe so first frame arrives fast
        {
            osmium::proto::AppPacketHeader idrReq{};
            ctx->transport->SendEncrypted(osmium::proto::AppPacketType::VideoIdrRequest, &idrReq, sizeof(idrReq));
        }

        while (!ctx->stopFlag.load(std::memory_order_acquire))
        {
            uint64_t nowMs = MobileTransport::NowMillis();

            // Send Ping every 500ms
            if (nowMs - lastPingMs >= 500ULL)
            {
                osmium::proto::PingPongPayload ping{};
                ping.hdr.type = static_cast<uint8_t>(osmium::proto::AppPacketType::Ping);
                ping.timestampUs = MobileTransport::NowMicros();
                ctx->transport->SendEncrypted(osmium::proto::AppPacketType::Ping, &ping, sizeof(ping));
                lastPingMs = nowMs;
            }

            // Update 1-second stats window
            if (nowMs - lastStatsMs >= 1000ULL)
            {
                uint64_t dtMs = std::max<uint64_t>(1, nowMs - lastStatsMs);
                uint64_t bytes = ctx->bytesRecvWindow.exchange(0, std::memory_order_relaxed);
                uint32_t rendered = ctx->renderedFramesWindow.exchange(0, std::memory_order_relaxed);
                ctx->bitrateKbps.store(static_cast<uint32_t>((bytes * 8ULL) / dtMs), std::memory_order_relaxed);
                ctx->fps.store(static_cast<uint32_t>((rendered * 1000ULL) / dtMs), std::memory_order_relaxed);
                lastStatsMs = nowMs;
            }

            // Detect peer timeout (10s silence)
            if (nowMs - lastRxMs > 10000ULL)
            {
                ctx->sessionActive.store(false, std::memory_order_release);
                ctx->PushStringEvent(4, "Connection timed out");
                break;
            }

            int r = ctx->transport->ReceiveEncrypted(plain, 15);
            if (r < 0)
            {
                ctx->sessionActive.store(false, std::memory_order_release);
                ctx->PushStringEvent(4, "Transport closed");
                break;
            }

            nowMs = MobileTransport::NowMillis();
            if (ctx->reassembler.PurgeStale(nowMs, ctx->transport->GetRttMs()))
            {
                ctx->droppedFrames.fetch_add(1, std::memory_order_relaxed);
                ctx->RequestIdr(150);
            }

            if (r == 0 || plain.size() < sizeof(osmium::proto::AppPacketHeader)) continue;
            lastRxMs = nowMs;
            ctx->bytesRecvWindow.fetch_add(plain.size(), std::memory_order_relaxed);

            auto type = static_cast<osmium::proto::AppPacketType>(plain[0]);

            if (type == osmium::proto::AppPacketType::VideoChunk &&
                plain.size() > sizeof(osmium::proto::VideoChunkHeader))
            {
                const auto* hdr = reinterpret_cast<const osmium::proto::VideoChunkHeader*>(plain.data());
                AssembledVideoFrame frame;
                bool hadDrop = false;
                if (ctx->reassembler.PushChunk(
                        hdr,
                        plain.data() + sizeof(osmium::proto::VideoChunkHeader),
                        plain.size() - sizeof(osmium::proto::VideoChunkHeader),
                        nowMs,
                        frame,
                        hadDrop))
                {
                    ctx->receivedFrames.fetch_add(1, std::memory_order_relaxed);
                    ctx->colorFlags.store(frame.colorFlags, std::memory_order_relaxed);
                    if (hadDrop)
                    {
                        ctx->droppedFrames.fetch_add(1, std::memory_order_relaxed);
                        ctx->RequestIdr(150);
                    }
                    bool overflowed = false;
                    {
                        std::lock_guard<std::mutex> ql(ctx->frameQueueMutex);
                        if (frame.isKeyFrame)
                        {
                            ctx->frameQueue.clear();
                        }
                        else if (ctx->frameQueue.size() > 6)
                        {
                            // Skipping delta frames breaks every reference after
                            // them, so resync on a keyframe instead of smearing.
                            ctx->frameQueue.clear();
                            ctx->decoder.ResyncAtNextKeyFrame();
                            overflowed = true;
                        }
                        ctx->frameQueue.push_back(std::move(frame));
                    }
                    if (overflowed)
                    {
                        ctx->droppedFrames.fetch_add(1, std::memory_order_relaxed);
                        ctx->RequestIdr(150);
                    }
                    ctx->frameQueueCv.notify_one();
                }
            }
            else if (type == osmium::proto::AppPacketType::AudioFrame &&
                     plain.size() >= sizeof(osmium::proto::AudioFrameHeader))
            {
                if (ctx->config.enableAudio != 0)
                {
                    const auto* hdr = reinterpret_cast<const osmium::proto::AudioFrameHeader*>(plain.data());
                    if (plain.size() >= sizeof(osmium::proto::AudioFrameHeader) + hdr->payloadBytes)
                    {
                        ctx->audio.PushOpusFrame(
                            plain.data() + sizeof(osmium::proto::AudioFrameHeader),
                            hdr->payloadBytes,
                            hdr->sampleRate,
                            hdr->channels,
                            hdr->sampleCount);
                    }
                }
            }
            else if (type == osmium::proto::AppPacketType::HostStatusJson &&
                     plain.size() > sizeof(osmium::proto::AppPacketHeader))
            {
                ctx->PushEvent(
                    2,
                    0,
                    plain.data() + sizeof(osmium::proto::AppPacketHeader),
                    plain.size() - sizeof(osmium::proto::AppPacketHeader));
            }
            else if (type == osmium::proto::AppPacketType::HostTextFocus &&
                     plain.size() >= sizeof(osmium::proto::HostTextFocusPayload))
            {
                ctx->PushEvent(
                    6,
                    0,
                    plain.data() + sizeof(osmium::proto::AppPacketHeader),
                    sizeof(osmium::proto::HostTextFocusPayload) - sizeof(osmium::proto::AppPacketHeader));
            }
            else if (type == osmium::proto::AppPacketType::Pong &&
                     plain.size() >= sizeof(osmium::proto::PingPongPayload))
            {
                const auto* pong = reinterpret_cast<const osmium::proto::PingPongPayload*>(plain.data());
                uint64_t nowUs = MobileTransport::NowMicros();
                if (nowUs >= pong->timestampUs)
                {
                    ctx->transport->RecordRttMs(static_cast<uint32_t>((nowUs - pong->timestampUs) / 1000ULL));
                }
            }
            else if (type == osmium::proto::AppPacketType::AuxData &&
                     plain.size() > sizeof(osmium::proto::AppPacketHeader))
            {
                ctx->PushEvent(
                    3,
                    0,
                    plain.data() + sizeof(osmium::proto::AppPacketHeader),
                    plain.size() - sizeof(osmium::proto::AppPacketHeader));
            }
            else if (type == osmium::proto::AppPacketType::Disconnect)
            {
                ctx->sessionActive.store(false, std::memory_order_release);
                ctx->PushStringEvent(4, "Host disconnected");
                break;
            }
        }
    }

    void DecodeLoop(MobileSessionContext* ctx)
    {
        while (!ctx->stopFlag.load(std::memory_order_acquire))
        {
            AssembledVideoFrame frame;
            {
                std::unique_lock<std::mutex> lock(ctx->frameQueueMutex);
                ctx->frameQueueCv.wait_for(
                    lock,
                    std::chrono::milliseconds(50),
                    [ctx] { return !ctx->frameQueue.empty() || ctx->stopFlag.load(std::memory_order_acquire); });
                if (ctx->stopFlag.load(std::memory_order_acquire)) break;
                if (ctx->frameQueue.empty()) continue;
                frame = std::move(ctx->frameQueue.front());
                ctx->frameQueue.pop_front();
            }

            uint32_t decUs = 0;
            if (ctx->decoder.DecodeAndRender(frame, decUs))
            {
                ctx->decodeTimeUs.store(decUs, std::memory_order_relaxed);
                ctx->renderedFramesWindow.fetch_add(1, std::memory_order_relaxed);
                if (!ctx->firstFrameRendered.exchange(true, std::memory_order_acq_rel))
                {
                    ctx->PushEvent(1, 0, nullptr, 0);
                }
            }
            else if (ctx->decoder.IsWaitingForKeyFrame())
            {
                // A fresh codec drops delta frames until an IDR arrives; the host
                // encodes with a long GOP, so ask instead of waiting it out.
                ctx->RequestIdr(500);
            }
        }
    }
}

// ---------------------------------------------------------------------------
// JNI Surface Bridge (called from Kotlin VideoSurfaceBridge.kt)
// ---------------------------------------------------------------------------
extern "C" JNIEXPORT jlong JNICALL
Java_com_osmium_osmium_1mobile_VideoSurfaceBridge_nativeRegisterSurface(
    JNIEnv* env,
    jobject /*thiz*/,
    jobject surface)
{
    if (!surface) return 0;
    ANativeWindow* window = ANativeWindow_fromSurface(env, surface);
    if (!window) return 0;
    std::lock_guard<std::mutex> lock(g_SurfaceMutex);
    int64_t id = g_NextSurfaceId++;
    g_Surfaces[id] = window;
    LOGI("Registered ANativeWindow id=%lld (%dx%d)",
         static_cast<long long>(id),
         ANativeWindow_getWidth(window),
         ANativeWindow_getHeight(window));
    return id;
}

extern "C" JNIEXPORT void JNICALL
Java_com_osmium_osmium_1mobile_VideoSurfaceBridge_nativeUnregisterSurface(
    JNIEnv* /*env*/,
    jobject /*thiz*/,
    jlong handleId)
{
    ANativeWindow* window = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_SurfaceMutex);
        auto it = g_Surfaces.find(handleId);
        if (it != g_Surfaces.end())
        {
            window = it->second;
            g_Surfaces.erase(it);
        }
    }
    if (window)
    {
        ANativeWindow_release(window);
        LOGI("Unregistered ANativeWindow id=%lld", static_cast<long long>(handleId));
    }
}

// ---------------------------------------------------------------------------
// Pure C ABI for Dart FFI
// ---------------------------------------------------------------------------
extern "C"
{
    int32_t OsmiumMobile_GenerateKeyPair(
        uint8_t* outPubSpkiDer,
        int32_t* ioPubLen,
        void** outKeyHandle)
    {
        if (!outPubSpkiDer || !ioPubLen || !outKeyHandle) return 0;
        auto* pair = new EcdhKeyPair();
        if (!GenerateEcdhP256KeyPair(*pair))
        {
            delete pair;
            return 0;
        }
        int32_t needed = static_cast<int32_t>(pair->pubSpkiDer.size());
        if (*ioPubLen < needed)
        {
            *ioPubLen = needed;
            delete pair;
            return 0;
        }
        std::memcpy(outPubSpkiDer, pair->pubSpkiDer.data(), pair->pubSpkiDer.size());
        *ioPubLen = needed;
        *outKeyHandle = pair;
        return 1;
    }

    int32_t OsmiumMobile_DeriveSessionMaterial(
        void* keyHandle,
        const uint8_t* peerPubSpkiDer,
        int32_t peerPubLen,
        const uint8_t* sessionToken16,
        const uint8_t* challengeNonce,
        int32_t nonceLen,
        const char* accessCodeUtf8,
        uint8_t* outAesKey32,
        uint8_t* outAuthTag32)
    {
        if (!keyHandle || peerPubLen <= 0 || nonceLen < 0) return 0;
        auto* pair = static_cast<EcdhKeyPair*>(keyHandle);
        return DeriveSessionMaterial(
                   *pair,
                   peerPubSpkiDer,
                   static_cast<size_t>(peerPubLen),
                   sessionToken16,
                   challengeNonce,
                   static_cast<size_t>(nonceLen),
                   accessCodeUtf8,
                   outAesKey32,
                   outAuthTag32)
                   ? 1
                   : 0;
    }

    void OsmiumMobile_DestroyKeyPair(void* keyHandle)
    {
        delete static_cast<EcdhKeyPair*>(keyHandle);
    }

    int32_t OsmiumMobile_CreateTransport(
        const char* stunHost,
        uint16_t stunPort,
        void** outTransport,
        char* outCandidatesJson,
        int32_t jsonCap)
    {
        if (!outTransport) return 0;
        auto* t = new MobileTransport();
        std::string json;
        if (!t->InitializeAndProbeStun(stunHost, stunPort, json))
        {
            delete t;
            return 0;
        }
        if (outCandidatesJson && jsonCap > 0)
        {
            std::strncpy(outCandidatesJson, json.c_str(), static_cast<size_t>(jsonCap - 1));
            outCandidatesJson[jsonCap - 1] = '\0';
        }
        *outTransport = t;
        return 1;
    }

    int32_t OsmiumMobile_ConnectPeerP2P(
        void* transport,
        const uint8_t* sessionToken16,
        const char* peerCandidatesJson,
        int32_t timeoutMs,
        int32_t* outRoute,
        char* outEndpoint,
        int32_t epCap)
    {
        if (!transport || !sessionToken16) return 0;
        auto* t = static_cast<MobileTransport*>(transport);
        int32_t route = 0;
        std::string ep;
        if (!t->ConnectPeerP2P(sessionToken16, peerCandidatesJson, timeoutMs, route, ep))
        {
            return 0;
        }
        if (outRoute) *outRoute = route;
        if (outEndpoint && epCap > 0)
        {
            std::strncpy(outEndpoint, ep.c_str(), static_cast<size_t>(epCap - 1));
            outEndpoint[epCap - 1] = '\0';
        }
        return 1;
    }

    int32_t OsmiumMobile_EnableRelay(
        void* transport,
        const uint8_t* sessionToken16,
        const char* label)
    {
        if (!transport || !sessionToken16) return 0;
        return static_cast<MobileTransport*>(transport)->EnableRelay(sessionToken16, label) ? 1 : 0;
    }

    int32_t OsmiumMobile_PushRelayBatch(
        void* transport,
        const uint8_t* batch,
        int32_t len)
    {
        if (!transport || !batch || len <= 0) return 0;
        static_cast<MobileTransport*>(transport)->PushRelayBatch(batch, static_cast<size_t>(len));
        return 1;
    }

    int32_t OsmiumMobile_PollRelayOutgoing(
        void* transport,
        uint8_t* outBuf,
        int32_t bufCap,
        int32_t* outIsDroppable)
    {
        if (!transport || !outBuf || bufCap <= 0) return 0;
        return static_cast<MobileTransport*>(transport)->PollRelayOutgoing(outBuf, bufCap, outIsDroppable);
    }

    int32_t OsmiumMobile_SetTransportKey(void* transport, const uint8_t* aesKey32)
    {
        if (!transport || !aesKey32) return 0;
        return static_cast<MobileTransport*>(transport)->SetSessionKey(aesKey32) ? 1 : 0;
    }

    int32_t OsmiumMobile_SetTransportEncryption(void* transport, int32_t enabled)
    {
        if (!transport) return 0;
        static_cast<MobileTransport*>(transport)->SetEncryptionEnabled(enabled != 0);
        return 1;
    }

    int32_t OsmiumMobile_AuthenticateClient(
        void* transport,
        const uint8_t* authTag32,
        const OsmiumMobileClientConfig* profile,
        int32_t timeoutMs,
        char* outErrorMsg,
        int32_t errorCap)
    {
        if (!transport || !profile) return 0;
        std::string err;
        int32_t status = static_cast<MobileTransport*>(transport)->AuthenticateClient(
            authTag32,
            profile->desiredWidth,
            profile->desiredHeight,
            profile->desiredFps,
            profile->desiredBitrateKbps,
            profile->codecPreference,
            profile->enableHdr,
            timeoutMs,
            err);
        if (status <= 0 && outErrorMsg && errorCap > 0)
        {
            std::strncpy(outErrorMsg, err.c_str(), static_cast<size_t>(errorCap - 1));
            outErrorMsg[errorCap - 1] = '\0';
        }
        return status;
    }

    void OsmiumMobile_DestroyTransport(void* transport)
    {
        if (!transport) return;
        auto* t = static_cast<MobileTransport*>(transport);
        t->Close();
        delete t;
    }

    int32_t OsmiumMobile_StartSession(
        void* transport,
        int64_t surfaceHandleId,
        const OsmiumMobileClientConfig* cfg,
        void** outSession)
    {
        if (!transport || !cfg || !outSession) return 0;
        auto* ctx = new MobileSessionContext();
        ctx->transport = static_cast<MobileTransport*>(transport);
        ctx->config = *cfg;

        ANativeWindow* win = AcquireSurfaceById(surfaceHandleId);
        if (win)
        {
            ctx->decoder.SetSurface(win);
            ANativeWindow_release(win);
        }

        ctx->recvThread = std::thread(RecvLoop, ctx);
        ctx->decodeThread = std::thread(DecodeLoop, ctx);
        *outSession = ctx;
        LOGI("Mobile session started (surface=%lld)", static_cast<long long>(surfaceHandleId));
        return 1;
    }

    void OsmiumMobile_StopSession(void* session)
    {
        if (!session) return;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        ctx->stopFlag.store(true, std::memory_order_release);
        ctx->sessionActive.store(false, std::memory_order_release);
        ctx->frameQueueCv.notify_all();

        if (ctx->transport)
        {
            osmium::proto::AppPacketHeader disc{};
            ctx->transport->SendEncrypted(osmium::proto::AppPacketType::Disconnect, &disc, sizeof(disc));
        }

        if (ctx->recvThread.joinable()) ctx->recvThread.join();
        if (ctx->decodeThread.joinable()) ctx->decodeThread.join();

        ctx->audio.StopAll();
        ctx->decoder.Release();
        delete ctx;
        LOGI("Mobile session stopped");
    }

    void OsmiumMobile_UpdateSurface(void* session, int64_t surfaceHandleId)
    {
        if (!session) return;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        ANativeWindow* win = AcquireSurfaceById(surfaceHandleId);
        ctx->decoder.SetSurface(win);
        if (win)
        {
            ANativeWindow_release(win);
            osmium::proto::AppPacketHeader idrReq{};
            ctx->transport->SendEncrypted(osmium::proto::AppPacketType::VideoIdrRequest, &idrReq, sizeof(idrReq));
        }
    }

    int32_t OsmiumMobile_GetStats(void* session, OsmiumMobileStats* outStats)
    {
        if (!session || !outStats) return 0;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        std::memset(outStats, 0, sizeof(*outStats));
        outStats->fps = ctx->fps.load(std::memory_order_relaxed);
        outStats->rttMs = ctx->transport ? ctx->transport->GetRttMs() : 0;
        outStats->bitrateKbps = ctx->bitrateKbps.load(std::memory_order_relaxed);
        outStats->packetLossPercentX100 = ctx->transport ? ctx->transport->GetPacketLossPercentX100() : 0;
        outStats->decodeTimeUs = ctx->decodeTimeUs.load(std::memory_order_relaxed);
        outStats->videoWidth = ctx->decoder.GetWidth();
        outStats->videoHeight = ctx->decoder.GetHeight();
        outStats->codecId = ctx->decoder.GetCodecId();
        outStats->colorFlags = ctx->colorFlags.load(std::memory_order_relaxed);
        outStats->transportRoute = ctx->transport ? ctx->transport->GetRoute() : 0;
        outStats->receivedFrames = ctx->receivedFrames.load(std::memory_order_relaxed);
        outStats->droppedFrames = ctx->droppedFrames.load(std::memory_order_relaxed);
        outStats->firstFrameRendered = ctx->firstFrameRendered.load(std::memory_order_relaxed) ? 1 : 0;
        outStats->sessionActive = ctx->sessionActive.load(std::memory_order_relaxed) ? 1 : 0;
        return 1;
    }

    int32_t OsmiumMobile_PollEvent(
        void* session,
        OsmiumMobileEvent* outEvent,
        uint8_t* outPayloadBuf,
        int32_t payloadCap)
    {
        if (!session || !outEvent) return 0;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        std::lock_guard<std::mutex> lock(ctx->eventMutex);
        if (ctx->eventQueue.empty()) return 0;

        const auto& front = ctx->eventQueue.front();
        int32_t len = static_cast<int32_t>(front.payload.size());
        outEvent->type = front.type;
        outEvent->flags = front.flags;
        outEvent->payloadLen = len;
        if (len > payloadCap)
        {
            return -1;
        }
        if (len > 0 && outPayloadBuf)
        {
            std::memcpy(outPayloadBuf, front.payload.data(), static_cast<size_t>(len));
        }
        ctx->eventQueue.pop_front();
        return 1;
    }

    void OsmiumMobile_SendMouseMove(void* session, int32_t isAbsolute, float xOrDx, float yOrDy)
    {
        if (!session) return;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        osmium::proto::InputMouseMovePayload pkt{};
        pkt.isAbsolute = isAbsolute ? 1 : 0;
        pkt.xOrDeltaX = xOrDx;
        pkt.yOrDeltaY = yOrDy;
        ctx->transport->SendEncrypted(osmium::proto::AppPacketType::InputMouseMove, &pkt, sizeof(pkt));
    }

    void OsmiumMobile_SendMouseButton(void* session, uint8_t button, int32_t isDown)
    {
        if (!session) return;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        osmium::proto::InputMouseButtonPayload pkt{};
        pkt.button = button;
        pkt.isDown = isDown ? 1 : 0;
        ctx->transport->SendEncrypted(osmium::proto::AppPacketType::InputMouseButton, &pkt, sizeof(pkt));
    }

    void OsmiumMobile_SendMouseWheel(void* session, int16_t dx, int16_t dy)
    {
        if (!session) return;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        osmium::proto::InputMouseWheelPayload pkt{};
        pkt.deltaX = dx;
        pkt.deltaY = dy;
        ctx->transport->SendEncrypted(osmium::proto::AppPacketType::InputMouseWheel, &pkt, sizeof(pkt));
    }

    void OsmiumMobile_SendKey(void* session, uint16_t vk, uint16_t scan, int32_t isDown, int32_t isExtended)
    {
        if (!session) return;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        osmium::proto::InputKeyPayload pkt{};
        pkt.vkCode = vk;
        pkt.scanCode = scan;
        pkt.isDown = isDown ? 1 : 0;
        pkt.isExtended = isExtended ? 1 : 0;
        ctx->transport->SendEncrypted(osmium::proto::AppPacketType::InputKey, &pkt, sizeof(pkt));
    }

    void OsmiumMobile_SendUnicodeText(void* session, const char* utf8Text, int32_t len)
    {
        if (!session || !utf8Text || len <= 0) return;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        std::vector<uint8_t> buf(sizeof(osmium::proto::AppPacketHeader) + static_cast<size_t>(len), 0);
        std::memcpy(buf.data() + sizeof(osmium::proto::AppPacketHeader), utf8Text, static_cast<size_t>(len));
        ctx->transport->SendEncrypted(osmium::proto::AppPacketType::InputUnicodeText, buf.data(), buf.size());
    }

    void OsmiumMobile_SendGamepad(void* session, const OsmiumMobileGamepadState* state)
    {
        if (!session || !state) return;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        osmium::proto::InputGamepadPayload pkt{};
        pkt.controllerIndex = state->controllerIndex;
        pkt.connected = state->connected;
        pkt.buttonsMask = state->buttonsMask;
        pkt.leftTrigger = state->leftTrigger;
        pkt.rightTrigger = state->rightTrigger;
        pkt.leftThumbX = state->leftThumbX;
        pkt.leftThumbY = state->leftThumbY;
        pkt.rightThumbX = state->rightThumbX;
        pkt.rightThumbY = state->rightThumbY;
        ctx->transport->SendEncrypted(osmium::proto::AppPacketType::InputGamepad, &pkt, sizeof(pkt));
    }

    void OsmiumMobile_SendTouch(void* session, uint16_t seq, const uint8_t* contacts, int32_t count)
    {
        if (!session || count < 0 || (count > 0 && !contacts)) return;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        osmium::proto::InputTouchPayload pkt{};
        pkt.count = static_cast<uint8_t>(std::min<int32_t>(count, osmium::proto::kMaxTouchContacts));
        pkt.seq = seq;
        if (pkt.count > 0)
        {
            std::memcpy(pkt.contacts, contacts, pkt.count * sizeof(osmium::proto::InputTouchContact));
        }
        const size_t len = osmium::proto::kInputTouchHeaderSize + pkt.count * sizeof(osmium::proto::InputTouchContact);
        ctx->transport->SendEncrypted(osmium::proto::AppPacketType::InputTouch, &pkt, len);

        // A lost press or lift is a wrong click; send those frames twice on UDP.
        // The host drops the repeat by its sequence number.
        bool edge = pkt.count == 0;
        for (uint8_t i = 0; i < pkt.count && !edge; ++i)
        {
            edge = pkt.contacts[i].phase != static_cast<uint8_t>(osmium::proto::TouchPhase::Move);
        }
        if (edge && ctx->transport->GetRoute() != static_cast<int32_t>(TransportRoute::CloudRelay))
        {
            ctx->transport->SendEncrypted(osmium::proto::AppPacketType::InputTouch, &pkt, len);
        }
    }

    void OsmiumMobile_SwitchDisplay(void* session, int32_t displayIndex)
    {
        if (!session) return;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        osmium::proto::ControlSwitchDisplayPayload pkt{};
        pkt.displayIndex = displayIndex;
        ctx->transport->SendEncrypted(osmium::proto::AppPacketType::ControlSwitchDisplay, &pkt, sizeof(pkt));
        // Redundant send on UDP to protect against single-packet loss
        if (ctx->transport->GetRoute() != static_cast<int32_t>(TransportRoute::CloudRelay))
        {
            ctx->transport->SendEncrypted(osmium::proto::AppPacketType::ControlSwitchDisplay, &pkt, sizeof(pkt));
        }
    }

    void OsmiumMobile_UpdateVideoParams(
        void* session,
        int32_t w,
        int32_t h,
        int32_t fps,
        int32_t bitrateKbps,
        int32_t hdr,
        int32_t yuv444)
    {
        if (!session) return;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        osmium::proto::ControlVideoParamsPayload pkt{};
        pkt.targetWidth = w;
        pkt.targetHeight = h;
        pkt.fps = fps;
        pkt.bitrateKbps = bitrateKbps;
        pkt.enableHdr = hdr;
        pkt.enableYuv444 = yuv444;
        ctx->transport->SendEncrypted(osmium::proto::AppPacketType::ControlVideoParams, &pkt, sizeof(pkt));
    }

    void OsmiumMobile_SetMouseMode(void* session, int32_t mouseMode, int32_t hideHostCursor)
    {
        if (!session) return;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        osmium::proto::ControlMouseModePayload pkt{};
        pkt.mouseMode = mouseMode;
        pkt.hideHostCursor = hideHostCursor;
        ctx->transport->SendEncrypted(osmium::proto::AppPacketType::ControlMouseMode, &pkt, sizeof(pkt));
    }

    void OsmiumMobile_SetTextFocusTracking(void* session, int32_t enabled)
    {
        if (!session) return;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        osmium::proto::ControlTextFocusPayload pkt{};
        pkt.enabled = enabled;
        ctx->transport->SendEncrypted(osmium::proto::AppPacketType::ControlTextFocus, &pkt, sizeof(pkt));
    }

    void OsmiumMobile_SetAudioMute(void* session, int32_t muted)
    {
        if (!session) return;
        static_cast<MobileSessionContext*>(session)->audio.SetMuted(muted != 0);
    }

    void OsmiumMobile_SetVirtualAudio(void* session, int32_t virtualSpeaker, int32_t virtualMic)
    {
        if (!session) return;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        osmium::proto::ControlVirtualAudioPayload pkt{};
        pkt.virtualSpeakerEnabled = virtualSpeaker;
        pkt.virtualMicEnabled = virtualMic;
        ctx->transport->SendEncrypted(osmium::proto::AppPacketType::ControlVirtualAudio, &pkt, sizeof(pkt));
    }

    void OsmiumMobile_SetMicCaptureEnabled(void* session, int32_t enabled)
    {
        if (!session) return;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        if (enabled)
        {
            ctx->audio.StartMicCapture(
                [ctx](const uint8_t* opusData, uint16_t payloadBytes,
                      uint16_t sampleRate, uint8_t channels, uint16_t sampleCount)
                {
                    if (ctx->stopFlag.load(std::memory_order_acquire) || !ctx->transport) return;
                    std::vector<uint8_t> buf(sizeof(osmium::proto::AudioFrameHeader) + payloadBytes);
                    auto* hdr = reinterpret_cast<osmium::proto::AudioFrameHeader*>(buf.data());
                    std::memset(hdr, 0, sizeof(*hdr));
                    hdr->sequence = ctx->micSeqCounter.fetch_add(1, std::memory_order_relaxed);
                    hdr->sampleRate = sampleRate;
                    hdr->channels = channels;
                    hdr->sampleCount = sampleCount;
                    hdr->payloadBytes = payloadBytes;
                    std::memcpy(buf.data() + sizeof(osmium::proto::AudioFrameHeader), opusData, payloadBytes);
                    ctx->transport->SendEncrypted(
                        osmium::proto::AppPacketType::ClientMicAudioFrame, buf.data(), buf.size());
                });
        }
        else
        {
            ctx->audio.StopMicCapture();
        }
    }

    void OsmiumMobile_SendSystemAction(void* session, int32_t action)
    {
        if (!session) return;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        osmium::proto::ControlSystemActionPayload pkt{};
        pkt.action = action;
        ctx->transport->SendEncrypted(osmium::proto::AppPacketType::ControlSystemAction, &pkt, sizeof(pkt));
    }

    void OsmiumMobile_RequestIdr(void* session)
    {
        if (!session) return;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        osmium::proto::AppPacketHeader idrReq{};
        ctx->transport->SendEncrypted(osmium::proto::AppPacketType::VideoIdrRequest, &idrReq, sizeof(idrReq));
    }

    int32_t OsmiumMobile_SendAuxData(void* session, const uint8_t* data, int32_t len)
    {
        if (!session || !data || len <= 0 || static_cast<size_t>(len) > osmium::proto::kMaxAuxDataPayload)
            return 0;
        auto* ctx = static_cast<MobileSessionContext*>(session);
        std::vector<uint8_t> buf(sizeof(osmium::proto::AppPacketHeader) + static_cast<size_t>(len), 0);
        std::memcpy(buf.data() + sizeof(osmium::proto::AppPacketHeader), data, static_cast<size_t>(len));
        return ctx->transport->SendEncrypted(osmium::proto::AppPacketType::AuxData, buf.data(), buf.size()) ? 1 : 0;
    }
}
