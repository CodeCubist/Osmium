#pragma once

#include "MobileCrypto.h"
#include "OsmiumProtocol.h"

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <mutex>
#include <netinet/in.h>
#include <string>
#include <sys/socket.h>
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

        bool SendEncrypted(proto::AppPacketType type, const void* appPayload, size_t len);
        // Returns 1 if packet received, 0 on timeout, -1 on closed/error
        int ReceiveEncrypted(std::vector<uint8_t>& outPlain, int timeoutMs);

        // Returns 1 when accepted, 0 on timeout/closed, -1 when the host
        // explicitly rejected the access code (mirrors Osmium_AuthenticateClient).
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

        static std::string BuildCandidatesJson(const std::vector<CandidateEndpoint>& list);
        static std::vector<CandidateEndpoint> ParseCandidatesJson(const char* jsonStr);
        static std::string SockAddrToString(const sockaddr_storage& ss);

        int m_Sock4 = -1;
        int m_Sock6 = -1;
        int m_ActiveSock = -1;
        uint16_t m_LocalPort4 = 0;
        uint16_t m_LocalPort6 = 0;
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
        std::atomic<uint64_t> m_RecvPacketCount{0};
        std::atomic<uint64_t> m_LostPacketCount{0};

        std::mutex m_RelayRecvMutex;
        std::condition_variable m_RelayRecvCv;
        std::deque<std::vector<uint8_t>> m_RelayRecvQueue;

        std::mutex m_RelaySendMutex;
        std::deque<RelayOutgoingPacket> m_RelaySendQueue;
    };
}
