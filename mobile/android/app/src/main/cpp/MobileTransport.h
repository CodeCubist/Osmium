#pragma once

#include "MobileCrypto.h"
#include "OsmiumKcp.h"
#include "OsmiumProtocol.h"

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <memory>
#include <mutex>
#include <netinet/in.h>
#include <string>
#include <sys/socket.h>
#include <thread>
#include <vector>

namespace osmium::mobile
{
    enum class TransportRoute : int32_t
    {
        Unknown = 0,
        LanIPv4 = 1,
        PublicIPv6 = 2,
        P2pIPv4 = 3,
        CloudRelay = 4
    };

    struct CandidateEndpoint
    {
        sockaddr_storage addr{};
        socklen_t addrLen = 0;
        std::string type;
        int tier = 3;
    };

    struct RelayOutgoingPacket
    {
        std::vector<uint8_t> data;
        bool isDroppable = false;
    };

    class MobileTransport
    {
    public:
        MobileTransport();
        ~MobileTransport();

        MobileTransport(const MobileTransport&) = delete;
        MobileTransport& operator=(const MobileTransport&) = delete;

        bool InitializeAndProbeStun(
            const char* stunHost,
            uint16_t stunPort,
            std::string& outCandidatesJson);

        bool ConnectPeerP2P(
            const uint8_t sessionToken[16],
            const char* peerCandidatesJson,
            int32_t punchTimeoutMs,
            int32_t& outTransportRoute,
            std::string& outActiveEndpoint);

        bool EnableRelay(
            const uint8_t sessionToken[16],
            const char* label);

        void PushRelayBatch(const uint8_t* batch, size_t len);
        int32_t PollRelayOutgoing(uint8_t* outBuf, int32_t bufCap, int32_t* outIsDroppable);

        bool SetSessionKey(const uint8_t aesKey[32]);
        void SetEncryptionEnabled(bool enabled) { m_EncryptionEnabled.store(enabled, std::memory_order_release); }
        bool IsEncryptionEnabled() const { return m_EncryptionEnabled.load(std::memory_order_acquire); }
        void SetKcpEnabled(bool enabled);
        bool IsKcpActive() const { return m_KcpActive.load(std::memory_order_acquire); }

        bool SendEncrypted(proto::AppPacketType type, const void* appPayload, size_t len);
        int ReceiveEncrypted(std::vector<uint8_t>& outPlain, int timeoutMs);

        int32_t AuthenticateClient(
            const uint8_t authTag[32],
            int32_t desiredWidth,
            int32_t desiredHeight,
            int32_t desiredFps,
            int32_t desiredBitrateKbps,
            int32_t codecPreference,
            int32_t enableHdr,
            int32_t timeoutMs,
            std::string& outError);

        void Close();
        bool IsConnected() const
        {
            return m_Connected.load(std::memory_order_acquire) && !m_Closed.load(std::memory_order_acquire);
        }

        int32_t GetRoute() const { return m_Route.load(std::memory_order_relaxed); }
        void RecordRttMs(uint32_t rttMs) { m_RttMs.store(rttMs, std::memory_order_relaxed); }
        uint32_t GetRttMs() const { return m_RttMs.load(std::memory_order_relaxed); }
        uint32_t GetPacketLossPercentX100() const;

        static uint64_t NowMillis();
        static uint64_t NowMicros();

    private:
        bool ResolveIPv4(const char* host, uint16_t port, sockaddr_in& outAddr);
        bool RawSendTo(int sock, const sockaddr_storage& target, socklen_t addrLen, const void* data, size_t len);
        bool EncryptFrame(const uint8_t* plain, size_t plainLen, uint8_t* outWire, size_t& outWireLen);
        bool FramePlaintext(const uint8_t* plain, size_t plainLen, uint8_t* outWire, size_t& outWireLen);
        bool DecryptFrame(const uint8_t* wire, size_t wireLen, std::vector<uint8_t>& outPlain);
        bool UnframePlaintext(const uint8_t* wire, size_t wireLen, std::vector<uint8_t>& outPlain);
        bool RecordReceivedSequence(uint64_t seq);
        bool SendRawEncrypted(proto::AppPacketType type, const void* appPayload, size_t len);
        void SendKcpSegments(const std::vector<std::vector<uint8_t>>& segments);
        bool HandleIncomingKcpSegment(const uint8_t* segData, size_t segLen, std::vector<uint8_t>& outPlain);
        void StartKcp();
        void StopKcp();
        void KcpPumpLoop();

        static std::string BuildCandidatesJson(const std::vector<CandidateEndpoint>& list);
        static std::vector<CandidateEndpoint> ParseCandidatesJson(const char* jsonStr);
        static std::string SockAddrToString(const sockaddr_storage& ss);

        int m_Sock4 = -1;
        int m_Sock6 = -1;
        std::atomic<int> m_ActiveSock{-1};
        std::atomic<bool> m_UdpConnected{false};
        uint16_t m_LocalPort4 = 0;
        uint16_t m_LocalPort6 = 0;
        sockaddr_in m_StunKeepaliveAddr{};
        bool m_HasStunKeepaliveAddr = false;
        int32_t m_PunchAttemptCount = 0;
        sockaddr_storage m_PeerAddr{};
        socklen_t m_PeerAddrLen = 0;

        uint8_t m_SessionToken[16]{};
        uint8_t m_NoncePrefix[4]{};
        proto::PeerRole m_LocalRole = proto::PeerRole::Client;

        std::atomic<bool> m_Closed{false};
        std::atomic<bool> m_Connected{false};
        std::atomic<bool> m_RelayMode{false};
        std::atomic<bool> m_EncryptionEnabled{true};
        std::atomic<int32_t> m_Route{0};
        std::atomic<uint32_t> m_RttMs{0};

        std::mutex m_SendMutex;
        std::mutex m_RecvCryptoMutex;
        Aes256GcmContext m_EncCtx;
        Aes256GcmContext m_DecCtx;
        std::atomic<uint64_t> m_SendSequence{1};

        uint64_t m_FirstRecvSeq = 0;
        uint64_t m_MaxRecvSeq = 0;
        uint64_t m_RecvWindowMask = 0;
        uint64_t m_WinStartMs = 0;
        uint64_t m_WinRecv = 0;
        uint64_t m_WinLost = 0;
        uint64_t m_PrevWinRecv = 0;
        uint64_t m_PrevWinLost = 0;
        std::atomic<uint32_t> m_RecentLossX100{0};
        std::atomic<uint64_t> m_RecvPacketCount{0};
        std::atomic<uint64_t> m_LostPacketCount{0};

        std::mutex m_RelayRecvMutex;
        std::condition_variable m_RelayRecvCv;
        std::deque<std::vector<uint8_t>> m_RelayRecvQueue;

        std::mutex m_RelaySendMutex;
        std::deque<RelayOutgoingPacket> m_RelayHighSendQueue;
        std::deque<RelayOutgoingPacket> m_RelayAudioSendQueue;
        std::deque<RelayOutgoingPacket> m_RelayDropSendQueue;

        std::atomic<bool> m_KcpEnabled{false};
        std::atomic<bool> m_KcpActive{false};
        std::mutex m_KcpMutex;
        std::condition_variable m_KcpPumpCv;
        bool m_KcpStop = false;
        std::unique_ptr<kcp::KcpSession> m_KcpCtrl;
        std::unique_ptr<kcp::KcpSession> m_KcpAux;
        std::deque<std::vector<uint8_t>> m_KcpDeliveredQueue;
        std::thread m_KcpPumpThread;
    };
}
