#pragma once

#include "OsmiumCommon.h"
#include "OsmiumProtocol.h"

#include <bcrypt.h>
#include <atomic>
#include <condition_variable>
#include <deque>
#include <functional>
#include <mutex>
#include <string>
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

    using RelaySendFunc = std::function<bool(const uint8_t* data, size_t len, bool isDroppable)>;

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

        bool SetSessionKey(const uint8_t aesKey[32]);
        void SetEncryptionEnabled(bool enabled) { m_EncryptionEnabled.store(enabled, std::memory_order_release); }
        bool IsEncryptionEnabled() const { return m_EncryptionEnabled.load(std::memory_order_acquire); }

        bool SendEncrypted(
            proto::AppPacketType pktType,
            const void* payload,
            size_t payloadLen);

        int ReceiveEncrypted(
            std::vector<uint8_t>& outPlaintext,
            int timeoutMs);

        void Close();

        int32_t GetTransportRoute() const { return m_Route.load(); }
        std::string GetActiveEndpointString() const;
        std::string GetLocalEndpointString() const;
        std::string GetRemoteEndpointString() const;
        uint16_t GetLocalPort() const { return m_ActiveLocalPort.load(); }

        void RecordRttMs(uint32_t rttMs) { m_LastRttMs.store(rttMs); }
        uint32_t GetLastRttMs() const { return m_LastRttMs.load(); }
        void GetPacketStats(uint64_t& outReceived, uint64_t& outLost) const;

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

        SOCKET m_Sock4 = INVALID_SOCKET;
        SOCKET m_Sock6 = INVALID_SOCKET;
        SOCKET m_ActiveSock = INVALID_SOCKET;
        uint16_t m_LocalPort4 = 0;
        uint16_t m_LocalPort6 = 0;
        std::atomic<uint16_t> m_ActiveLocalPort{0};
        bool m_HasPublicV6 = false;

        uint8_t m_SessionToken[16]{};
        proto::PeerRole m_LocalRole = proto::PeerRole::Client;

        sockaddr_storage m_PeerDirectAddr{};
        int m_PeerDirectAddrLen = 0;
        std::atomic<int32_t> m_Route{0};
        std::atomic<bool> m_Connected{false};
        std::atomic<bool> m_Closed{false};
        std::atomic<bool> m_EncryptionEnabled{true};

        BCRYPT_ALG_HANDLE m_AesAlg = nullptr;
        BCRYPT_KEY_HANDLE m_EncKey = nullptr;
        BCRYPT_KEY_HANDLE m_DecKey = nullptr;
        std::vector<uint8_t> m_EncKeyObj;
        std::vector<uint8_t> m_DecKeyObj;

        std::mutex m_SendMutex;
        std::mutex m_RecvCryptoMutex;
        std::vector<uint8_t> m_SendScratchPlain;
        std::vector<uint8_t> m_SendScratchWire;

        std::atomic<uint64_t> m_SendSequence{1};
        uint8_t m_NoncePrefix[4]{};
        std::atomic<uint32_t> m_LastRttMs{0};

        std::string m_LocalEndpointStr;
        std::string m_RemotePeerHintStr;
        mutable std::mutex m_StatsMutex;
        uint64_t m_FirstRecvSeq = 0;
        uint64_t m_MaxRecvSeq = 0;
        uint64_t m_RecvWindowMask = 0;
        std::atomic<uint64_t> m_RecvPacketCount{0};
        std::atomic<uint64_t> m_LostPacketCount{0};

        std::atomic<bool> m_RelayMode{false};
        std::mutex m_RelaySendMutex;
        RelaySendFunc m_RelaySendFn;
        std::mutex m_RelayRecvMutex;
        std::condition_variable m_RelayRecvCv;
        std::deque<std::vector<uint8_t>> m_RelayRecvQueue;
        std::string m_RelayEndpointStr;
    };
}
