#include "VideoReassembler.h"

#include <algorithm>
#include <cstring>

namespace osmium::mobile
{
    void VideoReassembler::Reset()
    {
        m_Pending.clear();
        m_LastCompletedFrameId = 0;
    }

    bool VideoReassembler::PushChunk(
        const proto::VideoChunkHeader* hdr,
        const uint8_t* dataPtr,
        size_t dataBytes,
        uint64_t nowMs,
        AssembledVideoFrame& outFrame,
        bool& outHadDrop)
    {
        outHadDrop = false;
        if (!hdr || hdr->totalChunks == 0 || hdr->chunkIndex >= hdr->totalChunks || dataBytes == 0)
            return false;
        if (hdr->frameId <= m_LastCompletedFrameId && m_LastCompletedFrameId != 0)
            return false;

        if (hdr->totalChunks == 1)
        {
            outFrame.frameId = hdr->frameId;
            outFrame.codecId = hdr->codecId;
            outFrame.isKeyFrame = hdr->isKeyFrame != 0;
            outFrame.width = hdr->width;
            outFrame.height = hdr->height;
            outFrame.colorFlags = hdr->colorFlags;
            outFrame.captureTimeUs = hdr->captureTimeUs;
            outFrame.payload.assign(dataPtr, dataPtr + dataBytes);

            m_LastCompletedFrameId = hdr->frameId;
            for (auto it = m_Pending.begin(); it != m_Pending.end();)
            {
                if (it->first < m_LastCompletedFrameId)
                {
                    it = m_Pending.erase(it);
                    outHadDrop = true;
                }
                else
                {
                    ++it;
                }
            }
            return true;
        }

        auto& pf = m_Pending[hdr->frameId];
        if (pf.totalChunks == 0)
        {
            pf.frameId = hdr->frameId;
            pf.totalChunks = hdr->totalChunks;
            pf.codecId = hdr->codecId;
            pf.isKeyFrame = hdr->isKeyFrame != 0;
            pf.width = hdr->width;
            pf.height = hdr->height;
            pf.colorFlags = hdr->colorFlags;
            pf.captureTimeUs = hdr->captureTimeUs;
            pf.firstSeenMs = nowMs;
            pf.receivedMask.assign(hdr->totalChunks, 0);
        }

        if (pf.totalChunks != hdr->totalChunks) return false;
        if (pf.receivedMask[hdr->chunkIndex]) return false;

        const bool isLastChunk = (hdr->chunkIndex == pf.totalChunks - 1);
        if (!isLastChunk)
        {
            if (pf.chunkStride == 0)
            {
                pf.chunkStride = static_cast<uint32_t>(dataBytes);
                pf.flatBuf.resize(static_cast<size_t>(pf.totalChunks) * pf.chunkStride);
                if (!pf.lastChunkBuf.empty())
                {
                    const size_t lastOff = static_cast<size_t>(pf.totalChunks - 1) * pf.chunkStride;
                    if (pf.lastChunkBuf.size() > pf.chunkStride)
                    {
                        pf.flatBuf.resize(lastOff + pf.lastChunkBuf.size());
                    }
                    std::memcpy(pf.flatBuf.data() + lastOff, pf.lastChunkBuf.data(), pf.lastChunkBuf.size());
                    pf.lastChunkBuf.clear();
                }
            }
            const size_t offset = static_cast<size_t>(hdr->chunkIndex) * pf.chunkStride;
            if (offset + dataBytes > pf.flatBuf.size())
            {
                pf.flatBuf.resize(offset + dataBytes);
            }
            std::memcpy(pf.flatBuf.data() + offset, dataPtr, dataBytes);
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
                std::memcpy(pf.flatBuf.data() + lastOff, dataPtr, dataBytes);
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
        outFrame.captureTimeUs = pf.captureTimeUs;
        outFrame.payload = std::move(pf.flatBuf);

        m_LastCompletedFrameId = hdr->frameId;
        m_Pending.erase(hdr->frameId);

        for (auto it = m_Pending.begin(); it != m_Pending.end();)
        {
            if (it->first < m_LastCompletedFrameId)
            {
                it = m_Pending.erase(it);
                outHadDrop = true;
            }
            else
            {
                ++it;
            }
        }
        return true;
    }

    bool VideoReassembler::PurgeStale(uint64_t nowMs, uint32_t rttMs)
    {
        uint64_t timeoutMs = std::clamp<uint64_t>(static_cast<uint64_t>(rttMs) * 3ULL + 80ULL, 120ULL, 450ULL);
        bool purged = false;
        for (auto it = m_Pending.begin(); it != m_Pending.end();)
        {
            if (nowMs > it->second.firstSeenMs && (nowMs - it->second.firstSeenMs) > timeoutMs)
            {
                if (it->first > m_LastCompletedFrameId)
                {
                    m_LastCompletedFrameId = it->first;
                }
                it = m_Pending.erase(it);
                purged = true;
            }
            else
            {
                ++it;
            }
        }
        return purged;
    }
}
