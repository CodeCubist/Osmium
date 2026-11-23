#pragma once

#include "OsmiumProtocol.h"

#include <cstdint>
#include <map>
#include <vector>

namespace osmium::mobile
{
    struct AssembledVideoFrame
    {
        uint32_t frameId = 0;
        uint8_t codecId = 0;
        bool isKeyFrame = false;
        uint16_t width = 0;
        uint16_t height = 0;
        uint8_t colorFlags = 0;
        uint64_t captureTimeUs = 0;
        std::vector<uint8_t> payload;
    };

    class VideoReassembler
    {
    public:
        void Reset();

        bool PushChunk(
            const proto::VideoChunkHeader* hdr,
            const uint8_t* dataPtr,
            size_t dataBytes,
            uint64_t nowMs,
            AssembledVideoFrame& outFrame,
            bool& outHadDrop);

        bool PurgeStale(uint64_t nowMs, uint32_t rttMs);

    private:
        struct PendingFrame
        {
            uint32_t frameId = 0;
            uint16_t totalChunks = 0;
            uint16_t receivedCount = 0;
            uint8_t codecId = 0;
            bool isKeyFrame = false;
            uint16_t width = 0;
            uint16_t height = 0;
            uint8_t colorFlags = 0;
            uint64_t captureTimeUs = 0;
            uint64_t firstSeenMs = 0;
            uint32_t chunkStride = 0;
            uint32_t lastChunkSize = 0;
            std::vector<uint8_t> receivedMask;
            std::vector<uint8_t> flatBuf;
            std::vector<uint8_t> lastChunkBuf;
        };

        std::map<uint32_t, PendingFrame> m_Pending;
        uint32_t m_LastCompletedFrameId = 0;
    };
}
