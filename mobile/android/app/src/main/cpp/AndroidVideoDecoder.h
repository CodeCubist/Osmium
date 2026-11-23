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

        // Returns true if a frame was rendered to the output surface
        bool DecodeAndRender(const AssembledVideoFrame& frame, uint32_t& outDecodeTimeUs);

        uint32_t GetWidth() const { return m_Width.load(std::memory_order_relaxed); }
        uint32_t GetHeight() const { return m_Height.load(std::memory_order_relaxed); }
        uint8_t GetCodecId() const { return m_CodecId.load(std::memory_order_relaxed); }
        bool IsWaitingForKeyFrame() const { return m_WaitForKeyFrame.load(std::memory_order_relaxed); }
        void ResyncAtNextKeyFrame() { m_WaitForKeyFrame.store(true, std::memory_order_relaxed); }

    private:
        bool EnsureConfiguredLocked(uint8_t codecId, uint16_t width, uint16_t height);
        void DestroyCodecLocked();
        static const char* CodecMimeFromId(uint8_t codecId);

        std::mutex m_Mutex;
        ANativeWindow* m_Window = nullptr;
        AMediaCodec* m_Codec = nullptr;
        std::atomic<bool> m_WaitForKeyFrame{true};

        std::atomic<uint8_t> m_CodecId{0};
        std::atomic<uint32_t> m_Width{0};
        std::atomic<uint32_t> m_Height{0};
    };
}
