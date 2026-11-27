#pragma once

#include "OsmiumCommon.h"
#include "OsmiumKcp.h"
#include "OsmiumProtocol.h"

#include <bcrypt.h>
#include <atomic>
#include <condition_variable>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace osmium
{
    enum class TransportRoute : int32_t
    {
        Unknown = 0,
        LanIPv4 = 1,
        PublicIPv6 = 2,
        P2pIPv4 = 3,
        CloudRelay = 4
    };

    using RelaySendFunc = std::function<bool(const uint8_t* data, size_t len, int32_t dropGroupId)>;

    struct CandidateEndpoint
    {
        sockaddr_storage addr{};
        int addrLen = 0;
        std::string type;
        int tier = 3;
    };

    class OsmiumTransport
    {
    public:
        OsmiumTransport();
        ~OsmiumTransport();

        OsmiumTransport(const OsmiumTransport&) = delete;
        OsmiumTransport& operator=(const OsmiumTransport&) = delete;

        bool InitializeAndProbeStun(
            const char* stunHost,
            uint16_t stunPort,
            std::string& outCandidatesJson);

        bool ConnectPeer(
            const uint8_t sessionToken[16],
            proto::PeerRole localRole,
            const char* peerCandidatesJson,
            int32_t punchTimeoutMs,
            int32_t& outTransportRoute,
            std::string& outActiveEndpoint);

        bool EnableRelay(
            const uint8_t sessionToken[16],
            proto::PeerRole localRole,
            RelaySendFunc sendFn,
            const char* endpointLabel);

        bool PushRelayPacket(const uint8_t* data, size_t len);
        bool PushRelayBatch(const uint8_t* batchData, size_t batchLen);

        bool SetSessionKey(const uint8_t aesKey[32]);
        void SetEncryptionEnabled(bool enabled) { m_EncryptionEnabled.store(enabled, std::memory_order_release); }
        bool IsEncryptionEnabled() const { return m_EncryptionEnabled.load(std::memory_order_acquire); }
        void SetKcpEnabled(bool enabled);
        bool IsKcpActive() const { return m_KcpActive.load(std::memory_order_acquire); }

        bool SendEncrypted(
            proto::AppPacketType pktType,
            const void* payload,
            size_t payloadLen);

        int ReceiveEncrypted(
            std::vector<uint8_t>& outPlaintext,
            int timeoutMs);

        void Close();
        bool IsConnected() const
        {
            return m_Connected.load(std::memory_order_acquire) && !m_Closed.load(std::memory_order_acquire);
        }

        int32_t GetTransportRoute() const { return m_Route.load(); }
        std::string GetActiveEndpointString() const;
        std::string GetLocalEndpointString() const;
        std::string GetRemoteEndpointString() const;
        uint16_t GetLocalPort() const { return m_ActiveLocalPort.load(); }

        void RecordRttMs(uint32_t rttMs)
        {
            m_LastRttMs.store(rttMs, std::memory_order_relaxed);
            m_RttSampleSeq.fetch_add(1, std::memory_order_release);
        }
        uint32_t GetLastRttMs() const { return m_LastRttMs.load(std::memory_order_relaxed); }
        uint64_t GetRttSampleSeq() const { return m_RttSampleSeq.load(std::memory_order_acquire); }
        void GetPacketStats(uint64_t& outReceived, uint64_t& outLost) const;
        uint32_t GetRecentLossPercentX100() const { return m_RecentLossX100.load(std::memory_order_relaxed); }

    private:
        bool ResolveIPv4(const char* host, uint16_t port, sockaddr_in& outAddr);
        std::vector<CandidateEndpoint> ParseCandidatesJson(const char* jsonStr);
        std::string BuildCandidatesJson(const std::vector<CandidateEndpoint>& list);
        bool RawSendTo(SOCKET sock, const sockaddr_storage& target, int addrLen, const void* data, size_t len);
        bool EncryptFrameWithEncKey(const uint8_t* plain, size_t plainLen, uint8_t* outWire, size_t& outWireLen);
        bool FramePlaintext(const uint8_t* plain, size_t plainLen, uint8_t* outWire, size_t& outWireLen);
        bool DecryptFrame(const uint8_t* wire, size_t wireLen, std::vector<uint8_t>& outPlain);
        bool UnframePlaintext(const uint8_t* wire, size_t wireLen, std::vector<uint8_t>& outPlain);
        bool RecordReceivedSequence(uint64_t seq);
        void RefreshSessionKeyForRole();
        bool SendRawEncrypted(proto::AppPacketType pktType, const void* payload, size_t payloadLen);
        void SendKcpSegments(const std::vector<std::vector<uint8_t>>& segments);
        bool HandleIncomingKcpSegment(const uint8_t* segData, size_t segLen, std::vector<uint8_t>& outPlaintext);
        void StartKcp();
        void StopKcp();
        void KcpPumpLoop();

        SOCKET m_Sock4 = INVALID_SOCKET;
        SOCKET m_Sock6 = INVALID_SOCKET;
        std::atomic<SOCKET> m_ActiveSock{INVALID_SOCKET};
        uint16_t m_LocalPort4 = 0;
        uint16_t m_LocalPort6 = 0;
        std::atomic<uint16_t> m_ActiveLocalPort{0};
        bool m_HasPublicV6 = false;
        sockaddr_in m_StunKeepaliveAddr{};
        bool m_HasStunKeepaliveAddr = false;
        int32_t m_PunchAttemptCount = 0;

        uint8_t m_SessionToken[16]{};
        proto::PeerRole m_LocalRole = proto::PeerRole::Client;

        sockaddr_storage m_PeerDirectAddr{};
        int m_PeerDirectAddrLen = 0;
        std::atomic<int32_t> m_Route{0};
        std::atomic<bool> m_Connected{false};
        std::atomic<bool> m_UdpConnected{false};
        std::atomic<bool> m_Closed{false};
        std::atomic<bool> m_EncryptionEnabled{true};

        BCRYPT_ALG_HANDLE m_AesAlg = nullptr;
        BCRYPT_KEY_HANDLE m_EncKey = nullptr;
        BCRYPT_KEY_HANDLE m_DecKey = nullptr;
        std::vector<uint8_t> m_EncKeyObj;
        std::vector<uint8_t> m_DecKeyObj;
        uint8_t m_MasterKey[32]{};
        bool m_HasMasterKey = false;
        proto::PeerRole m_KeyDerivedRole = proto::PeerRole::Client;

        std::mutex m_SendMutex;
        std::mutex m_RecvCryptoMutex;

        std::atomic<uint64_t> m_SendSequence{1};
        uint8_t m_NoncePrefix[4]{};
        std::atomic<uint32_t> m_LastRttMs{0};
        std::atomic<uint64_t> m_RttSampleSeq{0};

        std::string m_LocalEndpointStr;
        std::string m_RemotePeerHintStr;
        mutable std::mutex m_StatsMutex;
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

        std::atomic<bool> m_RelayMode{false};
        std::mutex m_RelaySendMutex;
        RelaySendFunc m_RelaySendFn;
        std::mutex m_RelayRecvMutex;
        std::condition_variable m_RelayRecvCv;
        std::deque<std::vector<uint8_t>> m_RelayRecvQueue;
        std::vector<std::vector<uint8_t>> m_RelayFreePool;
        std::string m_RelayEndpointStr;

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
