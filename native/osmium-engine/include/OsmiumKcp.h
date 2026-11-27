#pragma once

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <deque>
#include <iterator>
#include <list>
#include <utility>
#include <vector>

namespace osmium::kcp
{
    constexpr uint32_t kOverhead = 24;
    constexpr uint8_t kCmdPush = 81;
    constexpr uint8_t kCmdAck = 82;
    constexpr uint8_t kCmdWask = 83;
    constexpr uint8_t kCmdWins = 84;
    constexpr uint32_t kAskSend = 1;
    constexpr uint32_t kAskTell = 2;
    constexpr uint32_t kRtoNdl = 20;
    constexpr uint32_t kRtoMin = 40;
    constexpr uint32_t kRtoDef = 120;
    constexpr uint32_t kRtoMax = 10000;
    constexpr uint32_t kProbeInit = 2000;
    constexpr uint32_t kProbeLimit = 30000;
    constexpr uint32_t kThreshInit = 32;
    constexpr uint32_t kThreshMin = 2;

    inline uint32_t ReadConvLE(const uint8_t* data, size_t len)
    {
        if (!data || len < kOverhead) return 0;
        return static_cast<uint32_t>(data[0]) |
            (static_cast<uint32_t>(data[1]) << 8) |
            (static_cast<uint32_t>(data[2]) << 16) |
            (static_cast<uint32_t>(data[3]) << 24);
    }

    class KcpSession
    {
    public:
        explicit KcpSession(uint32_t conv)
            : m_Conv(conv)
        {
            SetMtu(1180);
        }

        void SetMtu(uint32_t mtu)
        {
            if (mtu < 64) mtu = 64;
            m_Mtu = mtu;
            m_Mss = m_Mtu - kOverhead;
            m_FlushBuf.reserve(m_Mtu);
        }

        void SetNoDelay(int nodelay, int interval, int resend, int nocwnd)
        {
            if (nodelay >= 0)
            {
                m_NoDelay = static_cast<uint32_t>(nodelay);
                m_RxMinRto = m_NoDelay ? kRtoNdl : kRtoMin;
            }
            if (interval >= 0)
            {
                if (interval > 5000) interval = 5000;
                if (interval < 5) interval = 5;
                m_Interval = static_cast<uint32_t>(interval);
            }
            if (resend >= 0)
            {
                m_FastResend = static_cast<uint32_t>(resend);
            }
            if (nocwnd >= 0)
            {
                m_NoCwnd = static_cast<uint32_t>(nocwnd);
            }
        }

        void SetMinRto(uint32_t minRtoMs)
        {
            m_RxMinRto = std::clamp<uint32_t>(minRtoMs, 10U, 500U);
        }

        void SetWndSize(uint32_t sndWnd, uint32_t rcvWnd)
        {
            if (sndWnd > 0) m_SndWnd = sndWnd;
            if (rcvWnd > 0) m_RcvWnd = std::max<uint32_t>(rcvWnd, 128U);
        }

        uint32_t Conv() const { return m_Conv; }

        size_t WaitSnd() const { return m_SndBuf.size() + m_SndQueue.size(); }

        bool IsCongestedStale(uint32_t nowMs, size_t maxPending, uint32_t maxNoAckMs) const
        {
            if (WaitSnd() < maxPending) return false;
            if (m_LastAckMs == 0)
            {
                return m_FirstSendMs != 0 && TimeDiff(nowMs, m_FirstSendMs) >= static_cast<int32_t>(maxNoAckMs);
            }
            return TimeDiff(nowMs, m_LastAckMs) >= static_cast<int32_t>(maxNoAckMs);
        }

        bool Send(const uint8_t* data, size_t len, uint32_t nowMs)
        {
            if (!data || len == 0) return false;
            if (m_Mss == 0) return false;

            const size_t count = (len <= m_Mss) ? 1U : ((len + m_Mss - 1U) / m_Mss);
            if (count > 127U) return false;

            if (m_FirstSendMs == 0)
            {
                m_FirstSendMs = nowMs;
            }

            size_t offset = 0;
            for (size_t i = 0; i < count; ++i)
            {
                const size_t segLen = std::min<size_t>(len - offset, m_Mss);
                Segment seg;
                seg.conv = m_Conv;
                seg.cmd = kCmdPush;
                seg.frg = static_cast<uint8_t>(count - 1U - i);
                seg.data.assign(data + offset, data + offset + segLen);
                m_SndQueue.push_back(std::move(seg));
                offset += segLen;
            }
            return true;
        }

        int Input(const uint8_t* data, size_t size, uint32_t nowMs)
        {
            if (!data || size < kOverhead) return -1;

            const uint32_t prevUna = m_SndUna;
            uint32_t maxAck = 0;
            uint32_t latestTs = 0;
            int flag = 0;

            size_t pos = 0;
            while (size - pos >= kOverhead)
            {
                const uint8_t* p = data + pos;
                const uint32_t conv = ReadU32(p);
                if (conv != m_Conv) return -1;

                const uint8_t cmd = p[4];
                const uint8_t frg = p[5];
                const uint16_t wnd = ReadU16(p + 6);
                const uint32_t ts = ReadU32(p + 8);
                const uint32_t sn = ReadU32(p + 12);
                const uint32_t una = ReadU32(p + 16);
                const uint32_t len = ReadU32(p + 20);
                pos += kOverhead;

                if (size - pos < len) return -2;
                if (cmd != kCmdPush && cmd != kCmdAck && cmd != kCmdWask && cmd != kCmdWins)
                {
                    return -3;
                }

                m_RmtWnd = wnd;
                ParseUna(una);
                ShrinkBuf();

                if (cmd == kCmdAck)
                {
                    m_LastAckMs = nowMs;
                    const int32_t rtt = TimeDiff(nowMs, ts);
                    if (rtt >= 0)
                    {
                        UpdateAck(rtt);
                    }
                    ParseAck(sn);
                    ShrinkBuf();
                    if (flag == 0)
                    {
                        flag = 1;
                        maxAck = sn;
                        latestTs = ts;
                    }
                    else if (TimeDiff(sn, maxAck) > 0)
                    {
                        maxAck = sn;
                        latestTs = ts;
                    }
                }
                else if (cmd == kCmdPush)
                {
                    if (TimeDiff(sn, m_RcvNxt + m_RcvWnd) < 0)
                    {
                        m_AckList.push_back({sn, ts});
                        if (TimeDiff(sn, m_RcvNxt) >= 0)
                        {
                            Segment seg;
                            seg.conv = conv;
                            seg.cmd = cmd;
                            seg.frg = frg;
                            seg.wnd = wnd;
                            seg.ts = ts;
                            seg.sn = sn;
                            seg.una = una;
                            if (len > 0)
                            {
                                seg.data.assign(data + pos, data + pos + len);
                            }
                            ParseData(std::move(seg));
                        }
                    }
                }
                else if (cmd == kCmdWask)
                {
                    m_Probe |= kAskTell;
                }

                pos += len;
            }

            if (flag != 0)
            {
                ParseFastAck(maxAck, latestTs);
            }

            if (TimeDiff(m_SndUna, prevUna) > 0)
            {
                if (m_Cwnd < m_RmtWnd)
                {
                    const uint32_t mss = m_Mss;
                    if (m_Cwnd < m_Ssthresh)
                    {
                        m_Cwnd++;
                        m_Incr += mss;
                    }
                    else
                    {
                        if (m_Incr < mss) m_Incr = mss;
                        m_Incr += (mss * mss) / m_Incr + (mss / 16U);
                        if ((m_Cwnd + 1U) * mss <= m_Incr)
                        {
                            m_Cwnd = (m_Incr + mss - 1U) / (mss > 0 ? mss : 1U);
                        }
                    }
                    if (m_Cwnd > m_RmtWnd)
                    {
                        m_Cwnd = m_RmtWnd;
                        m_Incr = m_RmtWnd * mss;
                    }
                }
            }

            return 0;
        }

        bool Recv(std::vector<uint8_t>& outMsg)
        {
            const int peekSize = PeekSize();
            if (peekSize < 0) return false;

            const bool recover = (m_RcvQueue.size() >= m_RcvWnd);
            outMsg.clear();
            outMsg.reserve(static_cast<size_t>(peekSize));

            while (!m_RcvQueue.empty())
            {
                const Segment& seg = m_RcvQueue.front();
                const uint8_t frg = seg.frg;
                if (!seg.data.empty())
                {
                    outMsg.insert(outMsg.end(), seg.data.begin(), seg.data.end());
                }
                m_RcvQueue.pop_front();
                if (frg == 0) break;
            }

            MoveRcvBufToQueue();

            if (m_RcvQueue.size() < m_RcvWnd && recover)
            {
                m_Probe |= kAskTell;
            }
            return true;
        }

        void Update(uint32_t nowMs, std::vector<std::vector<uint8_t>>& outDatagrams, uint32_t maxDataSegments = 0)
        {
            m_Current = nowMs;
            if (!m_Updated)
            {
                m_Updated = true;
                m_TsFlush = m_Current;
            }
            int32_t slap = TimeDiff(m_Current, m_TsFlush);
            if (slap >= 10000 || slap < -10000)
            {
                m_TsFlush = m_Current;
                slap = 0;
            }
            if (slap >= 0)
            {
                m_TsFlush += m_Interval;
                if (TimeDiff(m_Current, m_TsFlush) >= 0)
                {
                    m_TsFlush = m_Current + m_Interval;
                }
                Flush(nowMs, outDatagrams, maxDataSegments);
            }
        }

        uint32_t Check(uint32_t nowMs) const
        {
            if (!m_Updated) return nowMs;
            uint32_t tsFlush = m_TsFlush;
            uint32_t tmPacket = 0x7FFFFFFFU;
            if (TimeDiff(nowMs, tsFlush) >= 10000 || TimeDiff(nowMs, tsFlush) < -10000)
            {
                tsFlush = nowMs;
            }
            if (TimeDiff(nowMs, tsFlush) >= 0)
            {
                return nowMs;
            }
            const uint32_t tmFlush = static_cast<uint32_t>(TimeDiff(tsFlush, nowMs));
            for (const auto& seg : m_SndBuf)
            {
                const int32_t diff = TimeDiff(seg.resendts, nowMs);
                if (diff <= 0) return nowMs;
                if (static_cast<uint32_t>(diff) < tmPacket)
                {
                    tmPacket = static_cast<uint32_t>(diff);
                }
            }
            uint32_t minimal = std::min<uint32_t>(tmPacket, tmFlush);
            if (minimal >= m_Interval) minimal = m_Interval;
            return nowMs + minimal;
        }

        void Flush(uint32_t nowMs, std::vector<std::vector<uint8_t>>& outDatagrams, uint32_t maxDataSegments = 0)
        {
            m_Current = nowMs;
            if (!m_Updated)
            {
                m_Updated = true;
                m_TsFlush = m_Current + m_Interval;
            }

            m_FlushBuf.clear();
            const uint16_t wnd = WndUnused();

            auto emitBuf = [&]()
            {
                if (!m_FlushBuf.empty())
                {
                    outDatagrams.emplace_back(m_FlushBuf.begin(), m_FlushBuf.end());
                    m_FlushBuf.clear();
                }
            };

            auto appendSeg = [&](uint8_t cmd, uint8_t frg, uint32_t ts, uint32_t sn, const uint8_t* body, uint32_t len)
            {
                if (m_FlushBuf.size() + kOverhead + len > m_Mtu)
                {
                    emitBuf();
                }
                const size_t start = m_FlushBuf.size();
                m_FlushBuf.resize(start + kOverhead + len);
                uint8_t* p = m_FlushBuf.data() + start;
                WriteU32(p, m_Conv);
                p[4] = cmd;
                p[5] = frg;
                WriteU16(p + 6, wnd);
                WriteU32(p + 8, ts);
                WriteU32(p + 12, sn);
                WriteU32(p + 16, m_RcvNxt);
                WriteU32(p + 20, len);
                if (len > 0 && body)
                {
                    std::memcpy(p + kOverhead, body, len);
                }
            };

            for (const auto& ack : m_AckList)
            {
                appendSeg(kCmdAck, 0, ack.ts, ack.sn, nullptr, 0);
            }
            m_AckList.clear();

            if (m_RmtWnd == 0)
            {
                if (m_ProbeWait == 0)
                {
                    m_ProbeWait = kProbeInit;
                    m_TsProbe = m_Current + m_ProbeWait;
                }
                else if (TimeDiff(m_Current, m_TsProbe) >= 0)
                {
                    if (m_ProbeWait < kProbeInit) m_ProbeWait = kProbeInit;
                    m_ProbeWait += m_ProbeWait / 2U;
                    if (m_ProbeWait > kProbeLimit) m_ProbeWait = kProbeLimit;
                    m_TsProbe = m_Current + m_ProbeWait;
                    m_Probe |= kAskSend;
                }
            }
            else
            {
                m_TsProbe = 0;
                m_ProbeWait = 0;
            }

            if ((m_Probe & kAskSend) != 0)
            {
                appendSeg(kCmdWask, 0, 0, 0, nullptr, 0);
            }
            if ((m_Probe & kAskTell) != 0)
            {
                appendSeg(kCmdWins, 0, 0, 0, nullptr, 0);
            }
            m_Probe = 0;

            uint32_t cwnd = std::min<uint32_t>(m_SndWnd, m_RmtWnd);
            if (m_NoCwnd == 0)
            {
                cwnd = std::min<uint32_t>(m_Cwnd, cwnd);
            }

            while (!m_SndQueue.empty() && TimeDiff(m_SndNxt, m_SndUna + cwnd) < 0)
            {
                Segment seg = std::move(m_SndQueue.front());
                m_SndQueue.pop_front();
                seg.conv = m_Conv;
                seg.cmd = kCmdPush;
                seg.wnd = wnd;
                seg.ts = m_Current;
                seg.sn = m_SndNxt++;
                seg.una = m_RcvNxt;
                seg.resendts = m_Current;
                seg.rto = m_RxRto;
                seg.fastack = 0;
                seg.xmit = 0;
                m_SndBuf.push_back(std::move(seg));
            }

            const uint32_t resent = (m_FastResend > 0) ? m_FastResend : 0xFFFFFFFFU;
            const uint32_t rtoMin = (m_NoDelay == 0) ? (m_RxRto >> 3) : 0U;

            bool change = false;
            bool lost = false;
            uint32_t sentDataCount = 0;

            for (auto& seg : m_SndBuf)
            {
                if (maxDataSegments > 0 && sentDataCount >= maxDataSegments)
                {
                    break;
                }

                bool needSend = false;
                if (seg.xmit == 0)
                {
                    needSend = true;
                    seg.xmit++;
                    seg.rto = m_RxRto;
                    seg.resendts = m_Current + seg.rto + rtoMin;
                }
                else if (TimeDiff(m_Current, seg.resendts) >= 0)
                {
                    needSend = true;
                    seg.xmit++;
                    if (m_NoDelay == 0)
                    {
                        seg.rto += std::max<uint32_t>(seg.rto, m_RxRto);
                    }
                    else
                    {
                        seg.rto += seg.rto / 2U;
                    }
                    if (seg.rto > kRtoMax) seg.rto = kRtoMax;
                    seg.resendts = m_Current + seg.rto;
                    lost = true;
                }
                else if (seg.fastack >= resent)
                {
                    needSend = true;
                    seg.xmit++;
                    seg.fastack = 0;
                    seg.resendts = m_Current + seg.rto;
                    change = true;
                }

                if (needSend)
                {
                    seg.ts = m_Current;
                    seg.wnd = wnd;
                    seg.una = m_RcvNxt;
                    appendSeg(
                        seg.cmd,
                        seg.frg,
                        seg.ts,
                        seg.sn,
                        seg.data.empty() ? nullptr : seg.data.data(),
                        static_cast<uint32_t>(seg.data.size()));
                    sentDataCount++;
                }
            }

            emitBuf();

            if (change)
            {
                const uint32_t inflight = m_SndNxt - m_SndUna;
                m_Ssthresh = std::max<uint32_t>(inflight / 2U, kThreshMin);
                m_Cwnd = m_Ssthresh + resent;
                m_Incr = m_Cwnd * m_Mss;
            }
            if (lost)
            {
                m_Ssthresh = std::max<uint32_t>(cwnd / 2U, kThreshMin);
                m_Cwnd = 1;
                m_Incr = m_Mss;
            }
            if (m_Cwnd < 1)
            {
                m_Cwnd = 1;
                m_Incr = m_Mss;
            }
        }

    private:
        struct Segment
        {
            uint32_t conv = 0;
            uint8_t cmd = 0;
            uint8_t frg = 0;
            uint16_t wnd = 0;
            uint32_t ts = 0;
            uint32_t sn = 0;
            uint32_t una = 0;
            uint32_t resendts = 0;
            uint32_t rto = 0;
            uint32_t fastack = 0;
            uint32_t xmit = 0;
            std::vector<uint8_t> data;
        };

        struct AckItem
        {
            uint32_t sn = 0;
            uint32_t ts = 0;
        };

        static int32_t TimeDiff(uint32_t later, uint32_t earlier)
        {
            return static_cast<int32_t>(later - earlier);
        }

        static uint16_t ReadU16(const uint8_t* p)
        {
            return static_cast<uint16_t>(p[0]) | (static_cast<uint16_t>(p[1]) << 8);
        }

        static uint32_t ReadU32(const uint8_t* p)
        {
            return static_cast<uint32_t>(p[0]) |
                (static_cast<uint32_t>(p[1]) << 8) |
                (static_cast<uint32_t>(p[2]) << 16) |
                (static_cast<uint32_t>(p[3]) << 24);
        }

        static void WriteU16(uint8_t* p, uint16_t v)
        {
            p[0] = static_cast<uint8_t>(v & 0xFFU);
            p[1] = static_cast<uint8_t>((v >> 8) & 0xFFU);
        }

        static void WriteU32(uint8_t* p, uint32_t v)
        {
            p[0] = static_cast<uint8_t>(v & 0xFFU);
            p[1] = static_cast<uint8_t>((v >> 8) & 0xFFU);
            p[2] = static_cast<uint8_t>((v >> 16) & 0xFFU);
            p[3] = static_cast<uint8_t>((v >> 24) & 0xFFU);
        }

        uint16_t WndUnused() const
        {
            if (m_RcvQueue.size() < m_RcvWnd)
            {
                return static_cast<uint16_t>(std::min<size_t>(m_RcvWnd - m_RcvQueue.size(), 65535U));
            }
            return 0;
        }

        int PeekSize() const
        {
            if (m_RcvQueue.empty()) return -1;
            const Segment& first = m_RcvQueue.front();
            if (first.frg == 0) return static_cast<int>(first.data.size());
            if (m_RcvQueue.size() < static_cast<size_t>(first.frg) + 1U) return -1;
            size_t total = 0;
            for (const auto& seg : m_RcvQueue)
            {
                total += seg.data.size();
                if (seg.frg == 0) return static_cast<int>(total);
            }
            return -1;
        }

        void UpdateAck(int32_t rtt)
        {
            if (m_RxSrtt == 0)
            {
                m_RxSrtt = rtt;
                m_RxRttVal = rtt / 2;
            }
            else
            {
                int32_t delta = rtt - m_RxSrtt;
                if (delta < 0) delta = -delta;
                m_RxRttVal = (3 * m_RxRttVal + delta) / 4;
                m_RxSrtt = (7 * m_RxSrtt + rtt) / 8;
                if (m_RxSrtt < 1) m_RxSrtt = 1;
            }
            const uint32_t rto = static_cast<uint32_t>(m_RxSrtt) +
                std::max<uint32_t>(m_Interval, static_cast<uint32_t>(4 * m_RxRttVal));
            m_RxRto = std::clamp<uint32_t>(rto, m_RxMinRto, kRtoMax);
        }

        void ShrinkBuf()
        {
            if (!m_SndBuf.empty())
            {
                m_SndUna = m_SndBuf.front().sn;
            }
            else
            {
                m_SndUna = m_SndNxt;
            }
        }

        void ParseAck(uint32_t sn)
        {
            if (TimeDiff(sn, m_SndUna) < 0 || TimeDiff(sn, m_SndNxt) >= 0) return;
            for (auto it = m_SndBuf.begin(); it != m_SndBuf.end(); ++it)
            {
                if (it->sn == sn)
                {
                    m_SndBuf.erase(it);
                    break;
                }
                if (TimeDiff(sn, it->sn) < 0) break;
            }
        }

        void ParseUna(uint32_t una)
        {
            while (!m_SndBuf.empty() && TimeDiff(una, m_SndBuf.front().sn) > 0)
            {
                m_SndBuf.pop_front();
            }
        }

        void ParseFastAck(uint32_t sn, uint32_t ts)
        {
            if (TimeDiff(sn, m_SndUna) < 0 || TimeDiff(sn, m_SndNxt) >= 0) return;
            for (auto& seg : m_SndBuf)
            {
                if (TimeDiff(sn, seg.sn) < 0) break;
                if (sn != seg.sn && TimeDiff(ts, seg.ts) >= 0)
                {
                    seg.fastack++;
                }
            }
        }

        void ParseData(Segment&& newSeg)
        {
            const uint32_t sn = newSeg.sn;
            if (TimeDiff(sn, m_RcvNxt + m_RcvWnd) >= 0 || TimeDiff(sn, m_RcvNxt) < 0)
            {
                return;
            }

            bool duplicate = false;
            auto insertPos = m_RcvBuf.end();
            for (auto it = m_RcvBuf.rbegin(); it != m_RcvBuf.rend(); ++it)
            {
                if (it->sn == sn)
                {
                    duplicate = true;
                    break;
                }
                if (TimeDiff(sn, it->sn) > 0)
                {
                    insertPos = it.base();
                    break;
                }
                insertPos = std::prev(it.base());
            }
            if (!duplicate)
            {
                m_RcvBuf.insert(insertPos, std::move(newSeg));
            }

            MoveRcvBufToQueue();
        }

        void MoveRcvBufToQueue()
        {
            while (!m_RcvBuf.empty() && m_RcvQueue.size() < m_RcvWnd)
            {
                if (m_RcvBuf.front().sn == m_RcvNxt)
                {
                    m_RcvQueue.push_back(std::move(m_RcvBuf.front()));
                    m_RcvBuf.pop_front();
                    m_RcvNxt++;
                }
                else
                {
                    break;
                }
            }
        }

        uint32_t m_Conv = 0;
        uint32_t m_Mtu = 1180;
        uint32_t m_Mss = 1156;

        uint32_t m_SndUna = 0;
        uint32_t m_SndNxt = 0;
        uint32_t m_RcvNxt = 0;

        uint32_t m_Ssthresh = kThreshInit;
        int32_t m_RxRttVal = 0;
        int32_t m_RxSrtt = 0;
        uint32_t m_RxRto = kRtoDef;
        uint32_t m_RxMinRto = kRtoMin;

        uint32_t m_SndWnd = 128;
        uint32_t m_RcvWnd = 128;
        uint32_t m_RmtWnd = 128;
        uint32_t m_Cwnd = 16;
        uint32_t m_Probe = 0;

        uint32_t m_Current = 0;
        uint32_t m_Interval = 10;
        uint32_t m_TsFlush = 0;
        bool m_Updated = false;

        uint32_t m_NoDelay = 0;
        uint32_t m_TsProbe = 0;
        uint32_t m_ProbeWait = 0;
        uint32_t m_Incr = 0;
        uint32_t m_FastResend = 0;
        uint32_t m_NoCwnd = 0;

        uint32_t m_FirstSendMs = 0;
        uint32_t m_LastAckMs = 0;

        std::deque<Segment> m_SndQueue;
        std::deque<Segment> m_RcvQueue;
        std::list<Segment> m_SndBuf;
        std::list<Segment> m_RcvBuf;
        std::vector<AckItem> m_AckList;
        std::vector<uint8_t> m_FlushBuf;
    };
}
