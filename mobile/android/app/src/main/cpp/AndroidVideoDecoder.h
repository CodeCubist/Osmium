#pragma once

#include "VideoReassembler.h"

#include <android/native_window.h>
#include <media/NdkMediaCodec.h>

#include <atomic>
#include <cstdint>
#include <mutex>

namespace osmium::mobile
{
    class AndroidVideoDecoder
    {
    public:
        AndroidVideoDecoder() = default;
        ~AndroidVideoDecoder();

        AndroidVideoDecoder(const AndroidVideoDecoder&) = delete;
        AndroidVideoDecoder& operator=(const AndroidVideoDecoder&) = delete;

        void SetSurface(ANativeWindow* window);
        void Release();

        // Returns the number of decoded frames submitted to the output surface.
        uint32_t DecodeAndRender(const AssembledVideoFrame& frame, uint32_t& outDecodeTimeUs,
                                 bool hasBacklog = false, uint64_t* outRenderedCaptureUs = nullptr);

        // Non-blocking drain of hardware-pipelined output frames when no new input is ready.
        uint32_t DrainPendingOutput(uint32_t& outDecodeTimeUs, uint64_t* outRenderedCaptureUs = nullptr);

        // Recovers a stalled/errored AMediaCodec (soft flush or full destroy+recreate).
        void RecoverCodec(bool hardRebuild);

        uint32_t GetWidth() const { return m_Width.load(std::memory_order_relaxed); }
        uint32_t GetHeight() const { return m_Height.load(std::memory_order_relaxed); }
        uint8_t GetCodecId() const { return m_CodecId.load(std::memory_order_relaxed); }
        bool IsWaitingForKeyFrame() const { return m_WaitForKeyFrame.load(std::memory_order_relaxed); }
        bool HasSurface() const { return m_HasSurface.load(std::memory_order_acquire); }
        bool HasInFlightFrames() const { return m_InFlightFrames.load(std::memory_order_relaxed) > 0; }
        bool ConsumeSurfaceStale() { return m_SurfaceStale.exchange(false, std::memory_order_acq_rel); }
        void ResyncAtNextKeyFrame() { m_WaitForKeyFrame.store(true, std::memory_order_relaxed); }

    private:
        bool EnsureConfiguredLocked(uint8_t codecId, uint16_t width, uint16_t height, size_t minInputBytes);
        void DestroyCodecLocked();
        uint32_t DrainOutputsLocked(int64_t firstTimeoutUs, bool& outFatalError, uint64_t& outLastPtsUs);
        static const char* CodecMimeFromId(uint8_t codecId);

        std::mutex m_Mutex;
        ANativeWindow* m_Window = nullptr;
        AMediaCodec* m_Codec = nullptr;
        std::atomic<bool> m_WaitForKeyFrame{true};
        std::atomic<bool> m_HasSurface{false};
        std::atomic<bool> m_SurfaceStale{false};
        std::atomic<int32_t> m_InFlightFrames{0};

        std::atomic<uint8_t> m_CodecId{0};
        std::atomic<uint32_t> m_Width{0};
        std::atomic<uint32_t> m_Height{0};
        size_t m_ConfiguredMaxInput = 0;
        uint32_t m_ConsecutiveNoOutput = 0;
        uint32_t m_FlushAttempts = 0;
        bool m_ForceStandardMode = false;
    };
}
