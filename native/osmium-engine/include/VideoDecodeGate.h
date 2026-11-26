#pragma once

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <iterator>
#include <utility>

namespace osmium
{
    inline constexpr std::uint64_t kVideoReorderGraceMs = 30;
    inline constexpr std::size_t kVideoMaxOrderedBacklog = 4;
    inline constexpr std::size_t kVideoMaxQueuedFrames = 12;

    // The host emits no B-frames. Feed contiguous frame IDs to the decoder
    // and skip a missing reference only when an IDR can restart the stream.
    template <typename Frame>
    struct VideoDecodeGate
    {
        bool waitingForKeyframe = true;

        void Reset()
        {
            waitingForKeyframe = true;
            m_HasFedFrame = false;
            m_LastFedFrameId = 0;
            m_GapSinceMs = 0;
        }

        // Called under the frame queue mutex, like SelectNext. Prefer to keep
        // the next dependent frames and any keyframes when the queue fills.
        std::uint32_t Enqueue(std::deque<Frame>& queue, Frame&& frame)
        {
            if (m_HasFedFrame && frame.frameId <= m_LastFedFrameId) return 1;

            const auto byId = [](const Frame& left, const Frame& right) { return left.frameId < right.frameId; };
            auto position = std::lower_bound(queue.begin(), queue.end(), frame, byId);
            if (position != queue.end() && position->frameId == frame.frameId) return 1;

            std::uint32_t dropped = 0;
            if (queue.size() == kVideoMaxQueuedFrames)
            {
                auto dependent = std::find_if(queue.rbegin(), queue.rend(),
                    [](const Frame& queued) { return !queued.isKeyFrame; });
                if (dependent == queue.rend())
                {
                    if (!frame.isKeyFrame || position == queue.begin()) return 1;
                    queue.pop_front();
                }
                else
                {
                    if (!frame.isKeyFrame && frame.frameId > dependent->frameId) return 1;
                    queue.erase(std::prev(dependent.base()));
                }
                dropped = 1;
                position = std::lower_bound(queue.begin(), queue.end(), frame, byId);
            }
            queue.insert(position, std::move(frame));
            return dropped;
        }

        bool SelectNext(std::deque<Frame>& queue, std::uint64_t nowMs, std::uint32_t& dropped, Frame& outFrame,
                        std::uint64_t reorderGraceMs = kVideoReorderGraceMs)
        {
            if (queue.empty()) return false;

            if (!waitingForKeyframe && queue.front().frameId != m_LastFedFrameId + 1)
            {
                if (m_GapSinceMs == 0) m_GapSinceMs = nowMs;
                // A complete keyframe at the head already replaces the missing reference.
                if (!queue.front().isKeyFrame && nowMs - m_GapSinceMs < reorderGraceMs) return false;
                waitingForKeyframe = true;
            }

            if (waitingForKeyframe || queue.size() > kVideoMaxOrderedBacklog)
            {
                auto key = std::find_if(queue.rbegin(), queue.rend(),
                    [](const Frame& queued) { return queued.isKeyFrame; });
                if (key != queue.rend())
                {
                    auto next = std::prev(key.base());
                    dropped += static_cast<std::uint32_t>(next - queue.begin());
                    queue.erase(queue.begin(), next);
                }
                else if (waitingForKeyframe)
                {
                    return false;
                }
            }

            outFrame = std::move(queue.front());
            queue.pop_front();
            m_HasFedFrame = true;
            m_LastFedFrameId = outFrame.frameId;
            m_GapSinceMs = 0;
            waitingForKeyframe = false;
            return true;
        }

    private:
        bool m_HasFedFrame = false;
        std::uint32_t m_LastFedFrameId = 0;
        std::uint64_t m_GapSinceMs = 0;
    };
}
