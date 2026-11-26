#include "VideoReassembler.h"

#include <algorithm>
#include <cstring>

namespace osmium::mobile
{
    void VideoReassembler::Reset()
    {
        m_Pending.clear();
        m_LastCompletedKeyFrameId = 0;
    }

    bool VideoReassembler::PushChunk(
        const proto::VideoChunkHeader* hdr,
        const uint8_t* dataPtr,
        size_t dataBytes,
        uint64_t nowMs,
        AssembledVideoFrame& outFrame)
    {
        if (!hdr || hdr->totalChunks == 0 || hdr->chunkIndex >= hdr->totalChunks || dataBytes == 0)
            return false;
        const bool isKey = hdr->isKeyFrame != 0;
        // Only keyframes are sent twice (redundant IDR pass). Suppress an
        // already-completed keyframe; keep inter frames that finish behind a
        // higher id so the decode gate can put them back in order instead of
        // turning packet reordering into a permanent gap.
        if (isKey && hdr->frameId <= m_LastCompletedKeyFrameId)
        {
            return false;
        }

        if (hdr->totalChunks == 1)
        {
            outFrame.frameId = hdr->frameId;
            outFrame.codecId = hdr->codecId;
            outFrame.isKeyFrame = isKey;
            outFrame.width = hdr->width;
            outFrame.height = hdr->height;
            outFrame.colorFlags = hdr->colorFlags;
            outFrame.captureTimeUs = hdr->captureTimeUs;
            outFrame.payload.assign(dataPtr, dataPtr + dataBytes);

            if (isKey)
            {
                m_LastCompletedKeyFrameId = hdr->frameId;
                for (auto it = m_Pending.begin(); it != m_Pending.end();)
                {
                    if (it->first < hdr->frameId) it = m_Pending.erase(it);
                    else ++it;
                }
            }
            return true;
        }

        if (m_Pending.find(hdr->frameId) == m_Pending.end())
        {
            // Keep pending reassembly table tightly bounded so bursts of incomplete
            // frames never bloat memory or slow down stale purging.
            while (m_Pending.size() >= 16)
            {
                m_Pending.erase(m_Pending.begin());
            }
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
                if (dataBytes > 32768) return false;
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
            else if (dataBytes != pf.chunkStride)
            {
                return false;
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

        if (outFrame.isKeyFrame)
        {
            m_LastCompletedKeyFrameId = hdr->frameId;
            for (auto it = m_Pending.begin(); it != m_Pending.end();)
            {
                if (it->first < hdr->frameId) it = m_Pending.erase(it);
                else ++it;
            }
        }
        m_Pending.erase(hdr->frameId);
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
