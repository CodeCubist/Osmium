#include "MobileTransport.h"

#include <android/log.h>
#include <arpa/inet.h>
#include <cerrno>
#include <chrono>
#include <cstring>
#include <fcntl.h>
#include <ifaddrs.h>
#include <net/if.h>
#include <netdb.h>
#include <netinet/in.h>
#include <poll.h>
#include <regex>
#include <sstream>
#include <unistd.h>

#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, "OsmiumTransport", __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, "OsmiumTransport", __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, "OsmiumTransport", __VA_ARGS__)

namespace osmium::mobile
{
    namespace
    {
        bool IsSameAddr(const sockaddr_storage& a, const sockaddr_storage& b)
        {
            if (a.ss_family != b.ss_family) return false;
            if (a.ss_family == AF_INET)
            {
                const auto* a4 = reinterpret_cast<const sockaddr_in*>(&a);
                const auto* b4 = reinterpret_cast<const sockaddr_in*>(&b);
                return a4->sin_addr.s_addr == b4->sin_addr.s_addr && a4->sin_port == b4->sin_port;
            }
            if (a.ss_family == AF_INET6)
            {
                const auto* a6 = reinterpret_cast<const sockaddr_in6*>(&a);
                const auto* b6 = reinterpret_cast<const sockaddr_in6*>(&b);
                return std::memcmp(&a6->sin6_addr, &b6->sin6_addr, sizeof(in6_addr)) == 0 &&
                       a6->sin6_port == b6->sin6_port;
            }
            return false;
        }

        enum class IPv4Class { Invalid, Loopback, Lan, Public };

        IPv4Class ClassifyIPv4(uint32_t ipHost)
        {
            if ((ipHost >> 24) == 0 || ipHost >= 0xE0000000U) return IPv4Class::Invalid;
            if ((ipHost >> 24) == 127) return IPv4Class::Loopback;
            if ((ipHost & 0xFFC00000U) == 0x64400000U) return IPv4Class::Invalid; // 100.64/10 CGNAT
            if ((ipHost >> 16) == 0xA9FEU) return IPv4Class::Invalid;             // 169.254/16
            if ((ipHost & 0xFFFE0000U) == 0xC6120000U) return IPv4Class::Invalid;
            if ((ipHost & 0xFFFFFF00U) == 0xC0000000U ||
                (ipHost & 0xFFFFFF00U) == 0xC0000200U ||
                (ipHost & 0xFFFFFF00U) == 0xC6336400U ||
                (ipHost & 0xFFFFFF00U) == 0xCB007100U)
            {
                return IPv4Class::Invalid;
            }
            if ((ipHost >> 24) == 10 ||
                (ipHost & 0xFFF00000U) == 0xAC100000U ||
                (ipHost >> 16) == 0xC0A8U)
            {
                return IPv4Class::Lan;
            }
            return IPv4Class::Public;
        }

        bool IsPublicDynamicIPv6(const in6_addr& a6)
        {
            const uint8_t* b = a6.s6_addr;
            if ((b[0] & 0xE0) != 0x20) return false;
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return false;
            if (b[0] == 0x20 && b[1] == 0x02) return false;
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false;
            return true;
        }

        int RouteFromTier(int tier)
        {
            if (tier == 1) return static_cast<int>(TransportRoute::LanIPv4);
            if (tier == 2) return static_cast<int>(TransportRoute::PublicIPv6);
            return static_cast<int>(TransportRoute::P2pIPv4);
        }

        int ClassifySockAddrTier(const sockaddr_storage& ss)
        {
            if (ss.ss_family == AF_INET)
            {
                uint32_t ipHost = ntohl(reinterpret_cast<const sockaddr_in*>(&ss)->sin_addr.s_addr);
                auto cls = ClassifyIPv4(ipHost);
                if (cls == IPv4Class::Lan || cls == IPv4Class::Loopback) return 1;
                return 3;
            }
            if (ss.ss_family == AF_INET6) return 2;
            return 3;
        }

        uint16_t ReadBE16(const uint8_t* p)
        {
            return static_cast<uint16_t>((uint16_t(p[0]) << 8) | uint16_t(p[1]));
        }

        void ConfigureUdpBuffers(int sock)
        {
            int bufSize = 4 * 1024 * 1024;
            setsockopt(sock, SOL_SOCKET, SO_RCVBUF, &bufSize, sizeof(bufSize));
            setsockopt(sock, SOL_SOCKET, SO_SNDBUF, &bufSize, sizeof(bufSize));
        }
    }

    uint64_t MobileTransport::NowMillis()
    {
        using namespace std::chrono;
        return static_cast<uint64_t>(
            duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count());
    }

    uint64_t MobileTransport::NowMicros()
    {
        using namespace std::chrono;
        return static_cast<uint64_t>(
            duration_cast<microseconds>(steady_clock::now().time_since_epoch()).count());
    }

    MobileTransport::MobileTransport()
    {
        if (!RandomBytes(m_NoncePrefix, sizeof(m_NoncePrefix)))
        {
            uint64_t t = NowMicros();
            std::memcpy(m_NoncePrefix, &t, sizeof(m_NoncePrefix));
        }
    }

    MobileTransport::~MobileTransport()
    {
        Close();
    }

    void MobileTransport::Close()
    {
        m_Closed.store(true, std::memory_order_release);
        m_Connected.store(false, std::memory_order_release);
        StopKcp();
        m_RelayRecvCv.notify_all();

        int s4 = m_Sock4;
        int s6 = m_Sock6;
        m_Sock4 = -1;
        m_Sock6 = -1;
        m_ActiveSock = -1;
        m_PunchAttemptCount = 0;
        if (s4 >= 0) ::close(s4);
        if (s6 >= 0 && s6 != s4) ::close(s6);
    }

    bool MobileTransport::ResolveIPv4(const char* host, uint16_t port, sockaddr_in& outAddr)
    {
        if (!host || !*host) return false;
        std::memset(&outAddr, 0, sizeof(outAddr));
        outAddr.sin_family = AF_INET;
        outAddr.sin_port = htons(port);

        if (inet_pton(AF_INET, host, &outAddr.sin_addr) == 1)
        {
            return true;
        }

        addrinfo hints{};
        hints.ai_family = AF_INET;
        hints.ai_socktype = SOCK_DGRAM;
        addrinfo* res = nullptr;
        if (getaddrinfo(host, nullptr, &hints, &res) != 0 || !res)
        {
            return false;
        }
        auto* ipv4 = reinterpret_cast<sockaddr_in*>(res->ai_addr);
        outAddr.sin_addr = ipv4->sin_addr;
        freeaddrinfo(res);
        return true;
    }

    bool MobileTransport::InitializeAndProbeStun(
        const char* stunHost,
        uint16_t stunPort,
        std::string& outCandidatesJson)
    {
        m_Sock4 = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        if (m_Sock4 < 0)
        {
            LOGE("Failed to create IPv4 UDP socket: %d", errno);
            return false;
        }
        ConfigureUdpBuffers(m_Sock4);

        sockaddr_in bindAddr4{};
        bindAddr4.sin_family = AF_INET;
        bindAddr4.sin_addr.s_addr = INADDR_ANY;
        bindAddr4.sin_port = 0;
        if (::bind(m_Sock4, reinterpret_cast<const sockaddr*>(&bindAddr4), sizeof(bindAddr4)) != 0)
        {
            LOGE("Failed to bind IPv4 UDP socket: %d", errno);
            Close();
            return false;
        }
        socklen_t len4 = sizeof(bindAddr4);
        ::getsockname(m_Sock4, reinterpret_cast<sockaddr*>(&bindAddr4), &len4);
        m_LocalPort4 = ntohs(bindAddr4.sin_port);

        m_Sock6 = ::socket(AF_INET6, SOCK_DGRAM, IPPROTO_UDP);
        if (m_Sock6 >= 0)
        {
            int v6Only = 1;
            setsockopt(m_Sock6, IPPROTO_IPV6, IPV6_V6ONLY, &v6Only, sizeof(v6Only));
            ConfigureUdpBuffers(m_Sock6);
            sockaddr_in6 bindAddr6{};
            bindAddr6.sin6_family = AF_INET6;
            bindAddr6.sin6_addr = in6addr_any;
            bindAddr6.sin6_port = 0;
            if (::bind(m_Sock6, reinterpret_cast<const sockaddr*>(&bindAddr6), sizeof(bindAddr6)) != 0)
            {
                ::close(m_Sock6);
                m_Sock6 = -1;
            }
            else
            {
                socklen_t len6 = sizeof(bindAddr6);
                ::getsockname(m_Sock6, reinterpret_cast<sockaddr*>(&bindAddr6), &len6);
                m_LocalPort6 = ntohs(bindAddr6.sin6_port);
            }
        }

        std::vector<CandidateEndpoint> candidates;

        // Enumerate local interfaces via getifaddrs
        ifaddrs* ifap = nullptr;
        if (getifaddrs(&ifap) == 0 && ifap)
        {
            for (ifaddrs* ifa = ifap; ifa != nullptr; ifa = ifa->ifa_next)
            {
                if (!ifa->ifa_addr) continue;
                if ((ifa->ifa_flags & IFF_UP) == 0 || (ifa->ifa_flags & IFF_LOOPBACK) != 0) continue;

                if (ifa->ifa_addr->sa_family == AF_INET)
                {
                    auto* sa4 = reinterpret_cast<sockaddr_in*>(ifa->ifa_addr);
                    uint32_t ipHost = ntohl(sa4->sin_addr.s_addr);
                    IPv4Class cls = ClassifyIPv4(ipHost);
                    if (cls == IPv4Class::Invalid || cls == IPv4Class::Loopback) continue;

                    CandidateEndpoint ep;
                    std::memcpy(&ep.addr, sa4, sizeof(sockaddr_in));
                    reinterpret_cast<sockaddr_in*>(&ep.addr)->sin_port = htons(m_LocalPort4);
                    ep.addrLen = sizeof(sockaddr_in);
                    ep.type = (cls == IPv4Class::Lan) ? "lan4" : "pub4";
                    ep.tier = (cls == IPv4Class::Lan) ? 1 : 3;
                    candidates.push_back(ep);
                }
                else if (ifa->ifa_addr->sa_family == AF_INET6 && m_Sock6 >= 0)
                {
                    auto* sa6 = reinterpret_cast<sockaddr_in6*>(ifa->ifa_addr);
                    if (!IsPublicDynamicIPv6(sa6->sin6_addr)) continue;

                    CandidateEndpoint ep;
                    sockaddr_in6 clean6{};
                    clean6.sin6_family = AF_INET6;
                    clean6.sin6_addr = sa6->sin6_addr;
                    clean6.sin6_port = htons(m_LocalPort6);
                    std::memcpy(&ep.addr, &clean6, sizeof(sockaddr_in6));
                    ep.addrLen = sizeof(sockaddr_in6);
                    ep.type = "pub6";
                    ep.tier = 2;
                    candidates.push_back(ep);
                }
            }
            freeifaddrs(ifap);
        }

        // Probe STUN (Osmium proprietary STUN first, then RFC 5389 fallback)
        struct StunTarget { std::string host; uint16_t port; bool rfc5389; };
        std::vector<StunTarget> stunTargets;
        if (stunHost && *stunHost && stunPort > 0)
        {
            stunTargets.push_back({stunHost, stunPort, false});
            stunTargets.push_back({stunHost, stunPort, true});
        }
        stunTargets.push_back({"stun.miwifi.com", 3478, true});
        stunTargets.push_back({"stun.qq.com", 3478, true});
        stunTargets.push_back({"stun.l.google.com", 19302, true});

        uint64_t reqId = NowMicros();
        uint8_t osmReq[16]{};
        std::memcpy(osmReq, proto::kMagic, 4);
        osmReq[4] = proto::kVersion;
        osmReq[5] = static_cast<uint8_t>(proto::WireMsgType::StunReq);
        std::memcpy(osmReq + 8, &reqId, 8);

        uint8_t rfcReq[20]{};
        rfcReq[0] = 0x00; rfcReq[1] = 0x01; // Binding Request
        rfcReq[4] = 0x21; rfcReq[5] = 0x12; rfcReq[6] = 0xA4; rfcReq[7] = 0x42; // Magic Cookie
        RandomBytes(rfcReq + 8, 12);

        const uint64_t totalStunDeadline = NowMillis() + 2400ULL;
        static constexpr uint64_t kStunWaitBudgetsMs[] = {240, 340, 460};
        bool stunResolved = false;

        for (const auto& target : stunTargets)
        {
            if (stunResolved || NowMillis() >= totalStunDeadline) break;
            sockaddr_in stunAddr{};
            if (!ResolveIPv4(target.host.c_str(), target.port, stunAddr)) continue;

            sockaddr_storage stunSs{};
            std::memcpy(&stunSs, &stunAddr, sizeof(sockaddr_in));

            for (int attempt = 0; attempt < 2 && !stunResolved && NowMillis() < totalStunDeadline; ++attempt)
            {
                if (target.rfc5389)
                    RawSendTo(m_Sock4, stunSs, sizeof(sockaddr_in), rfcReq, sizeof(rfcReq));
                else
                    RawSendTo(m_Sock4, stunSs, sizeof(sockaddr_in), osmReq, sizeof(osmReq));

                const uint64_t attemptBudget =
                    kStunWaitBudgetsMs[std::min<size_t>(static_cast<size_t>(attempt), 2)];
                uint64_t attemptDeadline = std::min<uint64_t>(NowMillis() + attemptBudget, totalStunDeadline);
                while (!stunResolved && NowMillis() < attemptDeadline)
                {
                    int remMs = static_cast<int>(attemptDeadline - NowMillis());
                    if (remMs <= 0) break;
                    pollfd pfd{m_Sock4, POLLIN, 0};
                    if (::poll(&pfd, 1, remMs) <= 0) break;

                    uint8_t resp[256]{};
                    sockaddr_in from{};
                    socklen_t fromLen = sizeof(from);
                    ssize_t n = ::recvfrom(m_Sock4, resp, sizeof(resp), 0,
                                           reinterpret_cast<sockaddr*>(&from), &fromLen);
                    if (n <= 0) continue;

                    uint16_t mappedPort = 0;
                    uint8_t mappedIp[4]{};

                    if (n >= 24 && std::memcmp(resp, proto::kMagic, 4) == 0 &&
                        resp[5] == static_cast<uint8_t>(proto::WireMsgType::StunResp) &&
                        std::memcmp(resp + 8, &reqId, 8) == 0)
                    {
                        mappedPort = ReadBE16(resp + 20);
                        std::memcpy(mappedIp, resp + 16, 4);
                    }
                    else if (n >= 20 && resp[0] == 0x01 && resp[1] == 0x01 &&
                             resp[4] == 0x21 && resp[5] == 0x12 && resp[6] == 0xA4 && resp[7] == 0x42 &&
                             std::memcmp(resp + 8, rfcReq + 8, 12) == 0)
                    {
                        uint16_t attrTotal = ReadBE16(resp + 2);
                        int pos = 20;
                        int limit = std::min(static_cast<int>(n), 20 + static_cast<int>(attrTotal));
                        while (pos + 4 <= limit)
                        {
                            uint16_t attrType = ReadBE16(resp + pos);
                            uint16_t attrLen = ReadBE16(resp + pos + 2);
                            const uint8_t* val = resp + pos + 4;
                            if (pos + 4 + attrLen > limit) break;
                            if (attrType == 0x0020 && attrLen >= 8 && val[1] == 0x01)
                            {
                                mappedPort = static_cast<uint16_t>(ReadBE16(val + 2) ^ 0x2112U);
                                mappedIp[0] = val[4] ^ 0x21;
                                mappedIp[1] = val[5] ^ 0x12;
                                mappedIp[2] = val[6] ^ 0xA4;
                                mappedIp[3] = val[7] ^ 0x42;
                                break;
                            }
                            if (attrType == 0x0001 && attrLen >= 8 && val[1] == 0x01 && mappedPort == 0)
                            {
                                mappedPort = ReadBE16(val + 2);
                                std::memcpy(mappedIp, val + 4, 4);
                            }
                            pos += 4 + ((attrLen + 3) & ~3);
                        }
                    }

                    if (mappedPort > 0)
                    {
                        sockaddr_in mapped4{};
                        mapped4.sin_family = AF_INET;
                        std::memcpy(&mapped4.sin_addr, mappedIp, 4);
                        mapped4.sin_port = htons(mappedPort);
                        if (ClassifyIPv4(ntohl(mapped4.sin_addr.s_addr)) != IPv4Class::Invalid)
                        {
                            CandidateEndpoint srflx;
                            std::memcpy(&srflx.addr, &mapped4, sizeof(sockaddr_in));
                            srflx.addrLen = sizeof(sockaddr_in);
                            srflx.type = "srflx";
                            srflx.tier = 3;
                            bool dup = false;
                            for (const auto& c : candidates)
                            {
                                if (IsSameAddr(c.addr, srflx.addr)) { dup = true; break; }
                            }
                            if (!dup) candidates.push_back(srflx);
                        }
                        m_StunKeepaliveAddr = stunAddr;
                        m_HasStunKeepaliveAddr = true;
                        stunResolved = true;
                    }
                }
            }
        }

        // Windows always advertises a loopback candidate so same-machine /
        // emulator peers can hole-punch without NAT. Omitting it made the
        // Android client unreachable from a host running on the same box.
        {
            CandidateEndpoint loopback;
            sockaddr_in lb4{};
            lb4.sin_family = AF_INET;
            inet_pton(AF_INET, "127.0.0.1", &lb4.sin_addr);
            lb4.sin_port = htons(m_LocalPort4);
            std::memcpy(&loopback.addr, &lb4, sizeof(sockaddr_in));
            loopback.addrLen = sizeof(sockaddr_in);
            loopback.type = "loopback";
            loopback.tier = 1;
            candidates.push_back(loopback);
        }

        outCandidatesJson = BuildCandidatesJson(candidates);
        LOGI("UDP bound (v4=%u, v6=%u), candidates=%s", m_LocalPort4, m_LocalPort6, outCandidatesJson.c_str());
        return true;
    }

    std::string MobileTransport::SockAddrToString(const sockaddr_storage& ss)
    {
        char ip[INET6_ADDRSTRLEN]{};
        if (ss.ss_family == AF_INET)
        {
            const auto* sa4 = reinterpret_cast<const sockaddr_in*>(&ss);
            inet_ntop(AF_INET, &sa4->sin_addr, ip, sizeof(ip));
            return std::string(ip) + ":" + std::to_string(ntohs(sa4->sin_port));
        }
        if (ss.ss_family == AF_INET6)
        {
            const auto* sa6 = reinterpret_cast<const sockaddr_in6*>(&ss);
            inet_ntop(AF_INET6, &sa6->sin6_addr, ip, sizeof(ip));
            return "[" + std::string(ip) + "]:" + std::to_string(ntohs(sa6->sin6_port));
        }
        return {};
    }

    std::string MobileTransport::BuildCandidatesJson(const std::vector<CandidateEndpoint>& list)
    {
        std::ostringstream oss;
        oss << "[";
        for (size_t i = 0; i < list.size(); ++i)
        {
            if (i > 0) oss << ",";
            char ip[INET6_ADDRSTRLEN]{};
            uint16_t port = 0;
            if (list[i].addr.ss_family == AF_INET)
            {
                const auto* sa4 = reinterpret_cast<const sockaddr_in*>(&list[i].addr);
                inet_ntop(AF_INET, &sa4->sin_addr, ip, sizeof(ip));
                port = ntohs(sa4->sin_port);
            }
            else if (list[i].addr.ss_family == AF_INET6)
            {
                const auto* sa6 = reinterpret_cast<const sockaddr_in6*>(&list[i].addr);
                inet_ntop(AF_INET6, &sa6->sin6_addr, ip, sizeof(ip));
                port = ntohs(sa6->sin6_port);
            }
            oss << "{\"ip\":\"" << ip << "\",\"port\":" << port << ",\"type\":\"" << list[i].type << "\"}";
        }
        oss << "]";
        return oss.str();
    }

    std::vector<CandidateEndpoint> MobileTransport::ParseCandidatesJson(const char* jsonStr)
    {
        std::vector<CandidateEndpoint> result;
        if (!jsonStr) return result;
        std::string s(jsonStr);
        std::regex objRegex(R"(\{[^}]*\})");
        std::regex ipRegex("\"ip\"\\s*:\\s*\"([^\"]+)\"");
        std::regex portRegex("\"port\"\\s*:\\s*(\\d+)");
        std::regex typeRegex("\"type\"\\s*:\\s*\"([^\"]+)\"");

        for (std::sregex_iterator it(s.begin(), s.end(), objRegex), end; it != end; ++it)
        {
            std::string obj = it->str();
            std::smatch mIp, mPort, mType;
            if (!std::regex_search(obj, mIp, ipRegex) || !std::regex_search(obj, mPort, portRegex))
                continue;
            std::string ipStr = mIp[1].str();
            int portVal = std::atoi(mPort[1].str().c_str());
            if (portVal <= 0 || portVal > 65535) continue;
            std::string typeStr = std::regex_search(obj, mType, typeRegex) ? mType[1].str() : "";

            sockaddr_in sa4{};
            if (inet_pton(AF_INET, ipStr.c_str(), &sa4.sin_addr) == 1)
            {
                uint32_t ipHost = ntohl(sa4.sin_addr.s_addr);
                IPv4Class cls = ClassifyIPv4(ipHost);
                if (cls == IPv4Class::Invalid) continue;
                sa4.sin_family = AF_INET;
                sa4.sin_port = htons(static_cast<uint16_t>(portVal));
                CandidateEndpoint ep;
                std::memcpy(&ep.addr, &sa4, sizeof(sockaddr_in));
                ep.addrLen = sizeof(sockaddr_in);
                ep.type = typeStr.empty() ? (cls == IPv4Class::Lan ? "lan4" : "srflx") : typeStr;
                ep.tier = (cls == IPv4Class::Lan || cls == IPv4Class::Loopback) ? 1 : 3;
                result.push_back(ep);
                continue;
            }

            sockaddr_in6 sa6{};
            if (inet_pton(AF_INET6, ipStr.c_str(), &sa6.sin6_addr) == 1)
            {
                if (!IsPublicDynamicIPv6(sa6.sin6_addr)) continue;
                sa6.sin6_family = AF_INET6;
                sa6.sin6_port = htons(static_cast<uint16_t>(portVal));
                CandidateEndpoint ep;
                std::memcpy(&ep.addr, &sa6, sizeof(sockaddr_in6));
                ep.addrLen = sizeof(sockaddr_in6);
                ep.type = "pub6";
                ep.tier = 2;
                result.push_back(ep);
            }
        }
        return result;
    }

    bool MobileTransport::ConnectPeerP2P(
        const uint8_t sessionToken[16],
        const char* peerCandidatesJson,
        int32_t punchTimeoutMs,
        int32_t& outTransportRoute,
        std::string& outActiveEndpoint)
    {
        if (m_Sock4 < 0 || !sessionToken) return false;
        const int32_t attempt = ++m_PunchAttemptCount;
        std::memcpy(m_SessionToken, sessionToken, 16);
        m_LocalRole = proto::PeerRole::Client;
        outTransportRoute = 0;

        auto setNonBlock = [](int s)
        {
            if (s < 0) return;
            int f = fcntl(s, F_GETFL, 0);
            if (f >= 0) fcntl(s, F_SETFL, f | O_NONBLOCK);
        };
        setNonBlock(m_Sock4);
        setNonBlock(m_Sock6);

        auto peerCandidates = ParseCandidatesJson(peerCandidatesJson);
        if (peerCandidates.empty())
        {
            LOGW("No valid peer candidates for direct P2P");
            return false;
        }

        if (attempt >= 3)
        {
            std::vector<CandidateEndpoint> expanded;
            for (const auto& c : peerCandidates)
            {
                if (c.tier != 3 || c.addr.ss_family != AF_INET) continue;
                const auto* baseSa = reinterpret_cast<const sockaddr_in*>(&c.addr);
                const int basePort = ntohs(baseSa->sin_port);
                static constexpr int kOffsets[] = {-1, 1, 2};
                for (int off : kOffsets)
                {
                    int p = basePort + off;
                    if (p <= 0 || p > 65535) continue;
                    CandidateEndpoint extra = c;
                    reinterpret_cast<sockaddr_in*>(&extra.addr)->sin_port = htons(static_cast<uint16_t>(p));
                    bool exists = false;
                    for (const auto& cur : peerCandidates)
                    {
                        if (IsSameAddr(cur.addr, extra.addr))
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists)
                    {
                        for (const auto& cur : expanded)
                        {
                            if (IsSameAddr(cur.addr, extra.addr))
                            {
                                exists = true;
                                break;
                            }
                        }
                    }
                    if (!exists) expanded.push_back(extra);
                }
            }
            for (auto& ex : expanded)
            {
                peerCandidates.push_back(std::move(ex));
            }
        }

        bool hasTier1 = false, hasTier2 = false;
        for (const auto& c : peerCandidates)
        {
            if (c.tier == 1) hasTier1 = true;
            else if (c.tier == 2 && m_Sock6 >= 0) hasTier2 = true;
        }

        uint8_t punchPkt[24]{};
        std::memcpy(punchPkt, proto::kMagic, 4);
        punchPkt[4] = proto::kVersion;
        punchPkt[5] = static_cast<uint8_t>(proto::WireMsgType::Punch);
        punchPkt[6] = static_cast<uint8_t>(m_LocalRole);
        std::memcpy(punchPkt + 8, m_SessionToken, 16);

        uint8_t punchAckPkt[24]{};
        std::memcpy(punchAckPkt, proto::kMagic, 4);
        punchAckPkt[4] = proto::kVersion;
        punchAckPkt[5] = static_cast<uint8_t>(proto::WireMsgType::PunchAck);
        punchAckPkt[6] = static_cast<uint8_t>(m_LocalRole);
        std::memcpy(punchAckPkt + 8, m_SessionToken, 16);

        const uint64_t tStart = NowMillis();
        const uint64_t tStartTier2 = (attempt == 1 && hasTier1) ? 100ULL : 0ULL;
        const uint64_t tStartTier3 = (attempt == 1) ? (tStartTier2 + (hasTier2 ? 250ULL : 0ULL)) : 0ULL;
        const uint64_t deadline = tStart + static_cast<uint64_t>(std::max(600, punchTimeoutMs));

        uint64_t lastBlastMs = 0;
        uint64_t lastStunKeepaliveMs = 0;
        uint64_t ackGraceDeadline = 0;
        bool receivedPunch = false;
        bool receivedAck = false;
        int bestTier = 99;
        sockaddr_storage bestPeer{};
        socklen_t bestPeerLen = 0;
        int bestSock = -1;

        while (!m_Closed.load(std::memory_order_acquire))
        {
            uint64_t now = NowMillis();
            if (now >= deadline) break;
            if (ackGraceDeadline > 0 && now >= ackGraceDeadline) break;

            uint64_t elapsed = now - tStart;
            const uint64_t blastInterval = elapsed < 1200ULL ? 45ULL : (elapsed < 3000ULL ? 85ULL : 140ULL);
            if (lastBlastMs == 0 || now - lastBlastMs >= blastInterval)
            {
                lastBlastMs = now;
                for (const auto& cand : peerCandidates)
                {
                    if (cand.tier == 2 && (elapsed < tStartTier2 || m_Sock6 < 0)) continue;
                    if (cand.tier == 3 && elapsed < tStartTier3) continue;
                    int txSock = (cand.addr.ss_family == AF_INET6) ? m_Sock6 : m_Sock4;
                    if (txSock >= 0) RawSendTo(txSock, cand.addr, cand.addrLen, punchPkt, sizeof(punchPkt));
                }
                if (attempt >= 2 && m_HasStunKeepaliveAddr && (lastStunKeepaliveMs == 0 || now - lastStunKeepaliveMs >= 350ULL))
                {
                    lastStunKeepaliveMs = now;
                    uint8_t rfcKa[20]{};
                    rfcKa[0] = 0x00;
                    rfcKa[1] = 0x01;
                    rfcKa[4] = 0x21;
                    rfcKa[5] = 0x12;
                    rfcKa[6] = 0xA4;
                    rfcKa[7] = 0x42;
                    sockaddr_storage stunSs{};
                    std::memcpy(&stunSs, &m_StunKeepaliveAddr, sizeof(sockaddr_in));
                    RawSendTo(m_Sock4, stunSs, sizeof(sockaddr_in), rfcKa, sizeof(rfcKa));
                }
                if (receivedPunch && bestSock >= 0)
                {
                    RawSendTo(bestSock, bestPeer, bestPeerLen, punchAckPkt, sizeof(punchAckPkt));
                }
            }

            pollfd pfds[2]{};
            nfds_t nfds = 0;
            pfds[nfds++] = {m_Sock4, POLLIN, 0};
            if (m_Sock6 >= 0) pfds[nfds++] = {m_Sock6, POLLIN, 0};

            int sel = ::poll(pfds, nfds, 30);
            if (sel > 0)
            {
                for (nfds_t i = 0; i < nfds; ++i)
                {
                    if ((pfds[i].revents & POLLIN) == 0) continue;
                    int rs = pfds[i].fd;
                    for (int drain = 0; drain < 32; ++drain)
                    {
                        uint8_t buf[2048];
                        sockaddr_storage from{};
                        socklen_t fromLen = sizeof(from);
                        ssize_t n = ::recvfrom(rs, buf, sizeof(buf), 0,
                                               reinterpret_cast<sockaddr*>(&from), &fromLen);
                        if (n <= 0) break;
                        if (n >= 24 && std::memcmp(buf, proto::kMagic, 4) == 0 && buf[4] == proto::kVersion)
                        {
                            auto type = static_cast<proto::WireMsgType>(buf[5]);
                            if ((type == proto::WireMsgType::Punch || type == proto::WireMsgType::PunchAck) &&
                                std::memcmp(buf + 8, m_SessionToken, 16) == 0 &&
                                buf[6] != static_cast<uint8_t>(m_LocalRole))
                            {
                                int tier = ClassifySockAddrTier(from);
                                for (const auto& c : peerCandidates)
                                {
                                    if (IsSameAddr(c.addr, from)) { tier = c.tier; break; }
                                }
                                RawSendTo(rs, from, fromLen, punchAckPkt, sizeof(punchAckPkt));

                                if (!receivedPunch || tier < bestTier)
                                {
                                    bestTier = tier;
                                    bestPeer = from;
                                    bestPeerLen = fromLen;
                                    bestSock = rs;
                                    receivedPunch = true;
                                }
                                if (type == proto::WireMsgType::PunchAck)
                                {
                                    if (!receivedAck || tier < bestTier)
                                    {
                                        receivedAck = true;
                                        bestTier = tier;
                                        bestPeer = from;
                                        bestPeerLen = fromLen;
                                        bestSock = rs;
                                    }
                                    if (bestTier == 1 || (!hasTier1 && bestTier == 2))
                                    {
                                        ackGraceDeadline = NowMillis();
                                        break;
                                    }
                                    else if (ackGraceDeadline == 0)
                                    {
                                        ackGraceDeadline = NowMillis() + 50;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        if (!receivedPunch || bestSock < 0)
        {
            LOGW("P2P punch attempt %d did not complete within %dms", attempt, punchTimeoutMs);
            return false;
        }

        for (int i = 0; i < 4; ++i)
        {
            if (i > 0) usleep(10000);
            RawSendTo(bestSock, bestPeer, bestPeerLen, punchAckPkt, sizeof(punchAckPkt));
        }

        m_ActiveSock = bestSock;
        m_PeerAddr = bestPeer;
        m_PeerAddrLen = bestPeerLen;
        m_Route.store(RouteFromTier(bestTier), std::memory_order_release);
        m_Connected.store(true, std::memory_order_release);

        if (bestSock == m_Sock4 && m_Sock6 >= 0) { ::close(m_Sock6); m_Sock6 = -1; }
        else if (bestSock == m_Sock6 && m_Sock4 >= 0) { ::close(m_Sock4); m_Sock4 = -1; }

        setNonBlock(m_ActiveSock);

        int tos = 0xB8;
        if (m_PeerAddr.ss_family == AF_INET6)
            setsockopt(m_ActiveSock, IPPROTO_IPV6, IPV6_TCLASS, &tos, sizeof(tos));
        else
            setsockopt(m_ActiveSock, IPPROTO_IP, IP_TOS, &tos, sizeof(tos));

        if (m_KcpEnabled.load(std::memory_order_acquire))
        {
            StartKcp();
        }

        outTransportRoute = m_Route.load(std::memory_order_relaxed);
        outActiveEndpoint = SockAddrToString(m_PeerAddr);
        LOGI("P2P connected route=%d endpoint=%s", outTransportRoute, outActiveEndpoint.c_str());
        return true;
    }

    bool MobileTransport::EnableRelay(const uint8_t sessionToken[16], const char* label)
    {
        if (!sessionToken || m_Closed.load(std::memory_order_acquire)) return false;
        StopKcp();
        std::memcpy(m_SessionToken, sessionToken, 16);
        m_LocalRole = proto::PeerRole::Client;

        int s4 = m_Sock4;
        int s6 = m_Sock6;
        m_Sock4 = -1;
        m_Sock6 = -1;
        m_ActiveSock = -1;
        m_PunchAttemptCount = 0;
        if (s4 >= 0) ::close(s4);
        if (s6 >= 0 && s6 != s4) ::close(s6);

        {
            std::lock_guard<std::mutex> sl(m_SendMutex);
            if (m_SendSequence.load(std::memory_order_relaxed) == 0)
            {
                m_SendSequence.store(1, std::memory_order_relaxed);
            }
        }
        {
            std::lock_guard<std::mutex> rl(m_RecvCryptoMutex);
            m_FirstRecvSeq = 0;
            m_MaxRecvSeq = 0;
            m_RecvWindowMask = 0;
            m_RecvPacketCount.store(0, std::memory_order_relaxed);
            m_LostPacketCount.store(0, std::memory_order_relaxed);
        }
        {
            std::lock_guard<std::mutex> ql(m_RelayRecvMutex);
            m_RelayRecvQueue.clear();
        }
        {
            std::lock_guard<std::mutex> ql(m_RelaySendMutex);
            m_RelayHighSendQueue.clear();
            m_RelayDropSendQueue.clear();
        }

        m_RelayMode.store(true, std::memory_order_release);
        m_Route.store(static_cast<int32_t>(TransportRoute::CloudRelay), std::memory_order_release);
        m_Connected.store(true, std::memory_order_release);
        LOGI("Cloud Relay enabled: %s", label ? label : "WSS");
        return true;
    }

    void MobileTransport::PushRelayBatch(const uint8_t* batch, size_t len)
    {
        if (!batch || len < 2 || m_Closed.load(std::memory_order_acquire)) return;
        size_t pos = 0;
        {
            std::lock_guard<std::mutex> lock(m_RelayRecvMutex);
            while (pos + 2 <= len)
            {
                uint16_t pktLen = static_cast<uint16_t>(batch[pos]) |
                                  (static_cast<uint16_t>(batch[pos + 1]) << 8);
                pos += 2;
                if (pktLen == 0 || pos + pktLen > len) break;
                if (m_RelayRecvQueue.size() > 2048)
                {
                    m_RelayRecvQueue.pop_front();
                }
                m_RelayRecvQueue.emplace_back(batch + pos, batch + pos + pktLen);
                pos += pktLen;
            }
        }
        m_RelayRecvCv.notify_one();
    }

    int32_t MobileTransport::PollRelayOutgoing(uint8_t* outBuf, int32_t bufCap, int32_t* outIsDroppable)
    {
        std::lock_guard<std::mutex> lock(m_RelaySendMutex);
        auto* q = !m_RelayHighSendQueue.empty()
                      ? &m_RelayHighSendQueue
                      : (!m_RelayDropSendQueue.empty() ? &m_RelayDropSendQueue : nullptr);
        if (!q) return 0;
        const auto& front = q->front();
        int32_t len = static_cast<int32_t>(front.data.size());
        if (len > bufCap)
        {
            return -len;
        }
        std::memcpy(outBuf, front.data.data(), front.data.size());
        if (outIsDroppable) *outIsDroppable = front.isDroppable ? 1 : 0;
        q->pop_front();
        return len;
    }

    bool MobileTransport::SetSessionKey(const uint8_t aesKey[32])
    {
        if (!aesKey) return false;
        std::lock_guard<std::mutex> lockSend(m_SendMutex);
        std::lock_guard<std::mutex> lockRecv(m_RecvCryptoMutex);

        uint8_t c2hKey[32]{};
        uint8_t h2cKey[32]{};
        if (!DeriveDirectionalSubkey(aesKey, "OSM1-C2H-AES256-GCM", c2hKey) ||
            !DeriveDirectionalSubkey(aesKey, "OSM1-H2C-AES256-GCM", h2cKey))
        {
            return false;
        }

        // Mobile is always Client: encrypt with c2h, decrypt with h2c
        bool ok = m_EncCtx.SetKey(c2hKey) && m_DecCtx.SetKey(h2cKey);
        std::memset(c2hKey, 0, sizeof(c2hKey));
        std::memset(h2cKey, 0, sizeof(h2cKey));
        return ok;
    }

    bool MobileTransport::RawSendTo(int sock, const sockaddr_storage& target, socklen_t addrLen,
                                    const void* data, size_t len)
    {
        if (sock < 0 || m_Closed.load(std::memory_order_acquire) || addrLen <= 0) return false;
        ssize_t sent = ::sendto(sock, data, len, 0,
                                reinterpret_cast<const sockaddr*>(&target), addrLen);
        if (sent < 0 && (errno == EAGAIN || errno == EWOULDBLOCK))
        {
            usleep(200);
            sent = ::sendto(sock, data, len, 0,
                            reinterpret_cast<const sockaddr*>(&target), addrLen);
        }
        return sent == static_cast<ssize_t>(len);
    }

    bool MobileTransport::EncryptFrame(const uint8_t* plain, size_t plainLen, uint8_t* outWire, size_t& outWireLen)
    {
        if (!m_EncCtx.IsInitialized()) return false;
        outWireLen = sizeof(proto::EncryptedFrameHeader) + plainLen;
        auto* hdr = reinterpret_cast<proto::EncryptedFrameHeader*>(outWire);
        std::memcpy(hdr->magic, proto::kMagic, 4);
        hdr->version = proto::kVersion;
        hdr->msgType = static_cast<uint8_t>(proto::WireMsgType::EncryptedData);

        const uint64_t seq64 = m_SendSequence.fetch_add(1, std::memory_order_relaxed);
        hdr->flags = static_cast<uint16_t>((seq64 >> 32) & 0xFFFFU);
        hdr->sequence = static_cast<uint32_t>(seq64 & 0xFFFFFFFFULL);

        std::memcpy(hdr->nonce, m_NoncePrefix, 4);
        hdr->nonce[4] = static_cast<uint8_t>(m_LocalRole);
        for (int i = 0; i < 7; ++i)
        {
            hdr->nonce[5 + i] = static_cast<uint8_t>((seq64 >> (i * 8)) & 0xFFU);
        }

        return m_EncCtx.Encrypt(
            hdr->nonce,
            outWire,
            12,
            plain,
            plainLen,
            outWire + sizeof(proto::EncryptedFrameHeader),
            hdr->tag);
    }

    bool MobileTransport::FramePlaintext(const uint8_t* plain, size_t plainLen, uint8_t* outWire, size_t& outWireLen)
    {
        outWireLen = sizeof(proto::EncryptedFrameHeader) + plainLen;
        auto* hdr = reinterpret_cast<proto::EncryptedFrameHeader*>(outWire);
        std::memset(hdr, 0, sizeof(*hdr));
        std::memcpy(hdr->magic, proto::kMagic, 4);
        hdr->version = proto::kVersion;
        hdr->msgType = static_cast<uint8_t>(proto::WireMsgType::PlainData);

        const uint64_t seq64 = m_SendSequence.fetch_add(1, std::memory_order_relaxed);
        hdr->flags = static_cast<uint16_t>((seq64 >> 32) & 0xFFFFU);
        hdr->sequence = static_cast<uint32_t>(seq64 & 0xFFFFFFFFULL);
        hdr->nonce[4] = static_cast<uint8_t>(m_LocalRole);
        for (int i = 0; i < 7; ++i)
        {
            hdr->nonce[5 + i] = static_cast<uint8_t>((seq64 >> (i * 8)) & 0xFFU);
        }
        if (plainLen > 0)
        {
            std::memcpy(outWire + sizeof(proto::EncryptedFrameHeader), plain, plainLen);
        }
        return true;
    }

    bool MobileTransport::RecordReceivedSequence(uint64_t seq)
    {
        if (m_MaxRecvSeq == 0)
        {
            m_FirstRecvSeq = seq;
            m_MaxRecvSeq = seq;
            m_RecvWindowMask = 1ULL;
        }
        else if (seq > m_MaxRecvSeq)
        {
            uint64_t adv = seq - m_MaxRecvSeq;
            uint64_t newlyLost = 0;
            if (adv >= 64)
            {
                for (uint64_t b = 0; b < 64; ++b)
                {
                    if ((m_RecvWindowMask & (1ULL << b)) == 0 &&
                        m_MaxRecvSeq >= b && (m_MaxRecvSeq - b) >= m_FirstRecvSeq)
                    {
                        newlyLost++;
                    }
                }
                newlyLost += (adv - 64);
                m_RecvWindowMask = 1ULL;
            }
            else
            {
                for (uint64_t b = 64 - adv; b < 64; ++b)
                {
                    if ((m_RecvWindowMask & (1ULL << b)) == 0 &&
                        m_MaxRecvSeq >= b && (m_MaxRecvSeq - b) >= m_FirstRecvSeq)
                    {
                        newlyLost++;
                    }
                }
                m_RecvWindowMask = (m_RecvWindowMask << adv) | 1ULL;
            }
            if (newlyLost > 0) m_LostPacketCount.fetch_add(newlyLost, std::memory_order_relaxed);
            m_MaxRecvSeq = seq;
        }
        else
        {
            uint64_t diff = m_MaxRecvSeq - seq;
            // Windows tolerates reordering up to 4096 packets; only the last
            // 64 fit the bitmap, deeper-but-recent packets are still accepted.
            if (diff >= 4096) return false;
            if (diff < 64)
            {
                uint64_t bit = 1ULL << diff;
                if ((m_RecvWindowMask & bit) != 0) return false;
                m_RecvWindowMask |= bit;
            }
        }
        m_RecvPacketCount.fetch_add(1, std::memory_order_relaxed);
        return true;
    }

    bool MobileTransport::DecryptFrame(const uint8_t* wire, size_t wireLen, std::vector<uint8_t>& outPlain)
    {
        if (wireLen < sizeof(proto::EncryptedFrameHeader)) return false;
        const auto* hdr = reinterpret_cast<const proto::EncryptedFrameHeader*>(wire);
        if (std::memcmp(hdr->magic, proto::kMagic, 4) != 0 ||
            hdr->version != proto::kVersion ||
            hdr->msgType != static_cast<uint8_t>(proto::WireMsgType::EncryptedData))
        {
            return false;
        }
        if (hdr->nonce[4] != static_cast<uint8_t>(proto::PeerRole::Host)) return false;

        const uint64_t seq = (static_cast<uint64_t>(hdr->flags) << 32) | static_cast<uint64_t>(hdr->sequence);
        if (seq == 0) return false;
        for (int i = 0; i < 6; ++i)
        {
            if (hdr->nonce[5 + i] != static_cast<uint8_t>((seq >> (i * 8)) & 0xFFU)) return false;
        }

        size_t cipherLen = wireLen - sizeof(proto::EncryptedFrameHeader);
        outPlain.resize(cipherLen);
        {
            std::lock_guard<std::mutex> lock(m_RecvCryptoMutex);
            if (!m_DecCtx.Decrypt(
                    hdr->nonce,
                    wire,
                    12,
                    wire + sizeof(proto::EncryptedFrameHeader),
                    cipherLen,
                    hdr->tag,
                    outPlain.data()))
            {
                return false;
            }
        }
        return RecordReceivedSequence(seq);
    }

    bool MobileTransport::UnframePlaintext(const uint8_t* wire, size_t wireLen, std::vector<uint8_t>& outPlain)
    {
        if (wireLen < sizeof(proto::EncryptedFrameHeader)) return false;
        const auto* hdr = reinterpret_cast<const proto::EncryptedFrameHeader*>(wire);
        if (std::memcmp(hdr->magic, proto::kMagic, 4) != 0 ||
            hdr->version != proto::kVersion ||
            hdr->msgType != static_cast<uint8_t>(proto::WireMsgType::PlainData))
        {
            return false;
        }
        if (hdr->nonce[4] != static_cast<uint8_t>(proto::PeerRole::Host)) return false;

        const uint64_t seq = (static_cast<uint64_t>(hdr->flags) << 32) | static_cast<uint64_t>(hdr->sequence);
        if (seq == 0) return false;
        for (int i = 0; i < 6; ++i)
        {
            if (hdr->nonce[5 + i] != static_cast<uint8_t>((seq >> (i * 8)) & 0xFFU)) return false;
        }
        if (!RecordReceivedSequence(seq)) return false;

        size_t bodyLen = wireLen - sizeof(proto::EncryptedFrameHeader);
        outPlain.resize(bodyLen);
        if (bodyLen > 0)
        {
            std::memcpy(outPlain.data(), wire + sizeof(proto::EncryptedFrameHeader), bodyLen);
        }
        return true;
    }

    void MobileTransport::SetKcpEnabled(bool enabled)
    {
        m_KcpEnabled.store(enabled, std::memory_order_release);
        if (enabled)
        {
            if (m_Connected.load(std::memory_order_acquire) &&
                !m_RelayMode.load(std::memory_order_acquire) &&
                !m_Closed.load(std::memory_order_acquire))
            {
                StartKcp();
            }
        }
        else
        {
            StopKcp();
        }
    }

    void MobileTransport::StartKcp()
    {
        if (m_Closed.load(std::memory_order_acquire) ||
            !m_Connected.load(std::memory_order_acquire) ||
            m_RelayMode.load(std::memory_order_acquire))
        {
            return;
        }

        bool needLaunch = false;
        {
            std::lock_guard<std::mutex> kl(m_KcpMutex);
            if (!m_KcpCtrl)
            {
                m_KcpCtrl = std::make_unique<kcp::KcpSession>(proto::kKcpConvControl);
                m_KcpCtrl->SetMtu(proto::kKcpMtu);
                m_KcpCtrl->SetNoDelay(1, 10, 2, 1);
                m_KcpCtrl->SetMinRto(20);
                m_KcpCtrl->SetWndSize(128, 128);
            }
            if (!m_KcpAux)
            {
                m_KcpAux = std::make_unique<kcp::KcpSession>(proto::kKcpConvAux);
                m_KcpAux->SetMtu(proto::kKcpMtu);
                m_KcpAux->SetNoDelay(1, 10, 2, 0);
                m_KcpAux->SetMinRto(30);
                m_KcpAux->SetWndSize(128, 256);
            }
            m_KcpStop = false;
            m_KcpActive.store(true, std::memory_order_release);
            if (!m_KcpPumpThread.joinable())
            {
                needLaunch = true;
            }
        }
        if (needLaunch)
        {
            m_KcpPumpThread = std::thread(&MobileTransport::KcpPumpLoop, this);
        }
        m_KcpPumpCv.notify_one();
    }

    void MobileTransport::StopKcp()
    {
        m_KcpActive.store(false, std::memory_order_release);
        std::thread pumpToJoin;
        {
            std::lock_guard<std::mutex> kl(m_KcpMutex);
            m_KcpStop = true;
            if (m_KcpPumpThread.joinable())
            {
                pumpToJoin = std::move(m_KcpPumpThread);
            }
        }
        m_KcpPumpCv.notify_all();
        if (pumpToJoin.joinable())
        {
            if (pumpToJoin.get_id() == std::this_thread::get_id())
            {
                pumpToJoin.detach();
            }
            else
            {
                pumpToJoin.join();
            }
        }
        {
            std::lock_guard<std::mutex> kl(m_KcpMutex);
            m_KcpCtrl.reset();
            m_KcpAux.reset();
            m_KcpDeliveredQueue.clear();
        }
    }

    void MobileTransport::SendKcpSegments(const std::vector<std::vector<uint8_t>>& segments)
    {
        if (segments.empty() || m_Closed.load(std::memory_order_acquire) || m_RelayMode.load(std::memory_order_acquire))
        {
            return;
        }
        alignas(8) uint8_t pktBuf[sizeof(proto::AppPacketHeader) + 2048];
        for (const auto& seg : segments)
        {
            if (seg.empty()) continue;
            if (seg.size() <= 2048)
            {
                std::memset(pktBuf, 0, sizeof(proto::AppPacketHeader));
                std::memcpy(pktBuf + sizeof(proto::AppPacketHeader), seg.data(), seg.size());
                SendRawEncrypted(proto::AppPacketType::KcpSegment, pktBuf,
                                 sizeof(proto::AppPacketHeader) + seg.size());
            }
            else
            {
                std::vector<uint8_t> dynBuf(sizeof(proto::AppPacketHeader) + seg.size(), 0);
                std::memcpy(dynBuf.data() + sizeof(proto::AppPacketHeader), seg.data(), seg.size());
                SendRawEncrypted(proto::AppPacketType::KcpSegment, dynBuf.data(), dynBuf.size());
            }
        }
    }

    void MobileTransport::KcpPumpLoop()
    {
        while (!m_Closed.load(std::memory_order_acquire))
        {
            std::vector<std::vector<uint8_t>> ctrlSegs;
            std::vector<std::vector<uint8_t>> auxSegs;
            {
                std::unique_lock<std::mutex> kl(m_KcpMutex);
                if (m_KcpStop || !m_KcpActive.load(std::memory_order_acquire))
                {
                    break;
                }
                const uint32_t nowMs = static_cast<uint32_t>(NowMillis());
                if (m_KcpCtrl)
                {
                    m_KcpCtrl->Update(nowMs, ctrlSegs, 0U);
                }
                if (m_KcpAux)
                {
                    m_KcpAux->Update(nowMs, auxSegs, 24U);
                }
                if (ctrlSegs.empty() && auxSegs.empty())
                {
                    uint32_t nextMs = nowMs + 15U;
                    if (m_KcpCtrl)
                    {
                        const uint32_t cNext = m_KcpCtrl->Check(nowMs);
                        if (static_cast<int32_t>(cNext - nextMs) < 0) nextMs = cNext;
                    }
                    if (m_KcpAux)
                    {
                        const uint32_t aNext = m_KcpAux->Check(nowMs);
                        if (static_cast<int32_t>(aNext - nextMs) < 0) nextMs = aNext;
                    }
                    const int32_t diff = static_cast<int32_t>(nextMs - nowMs);
                    const uint32_t sleepMs = static_cast<uint32_t>(std::clamp<int32_t>(diff, 2, 20));
                    m_KcpPumpCv.wait_for(kl, std::chrono::milliseconds(sleepMs));
                }
            }
            if (!ctrlSegs.empty())
            {
                SendKcpSegments(ctrlSegs);
            }
            if (!auxSegs.empty())
            {
                SendKcpSegments(auxSegs);
            }
        }
    }

    bool MobileTransport::HandleIncomingKcpSegment(
        const uint8_t* segData,
        size_t segLen,
        std::vector<uint8_t>& outPlain)
    {
        if (!segData || segLen < kcp::kOverhead || m_RelayMode.load(std::memory_order_acquire))
        {
            return false;
        }
        const uint32_t conv = kcp::ReadConvLE(segData, segLen);
        if (conv != proto::kKcpConvControl && conv != proto::kKcpConvAux)
        {
            return false;
        }
        if (!m_KcpActive.load(std::memory_order_acquire))
        {
            SetKcpEnabled(true);
        }

        std::vector<std::vector<uint8_t>> ackSegs;
        bool delivered = false;
        {
            std::lock_guard<std::mutex> kl(m_KcpMutex);
            kcp::KcpSession* sess = (conv == proto::kKcpConvControl) ? m_KcpCtrl.get() : m_KcpAux.get();
            if (!sess) return false;
            const uint32_t nowMs = static_cast<uint32_t>(NowMillis());
            if (sess->Input(segData, segLen, nowMs) == 0)
            {
                std::vector<uint8_t> msg;
                while (sess->Recv(msg))
                {
                    if (!msg.empty())
                    {
                        m_KcpDeliveredQueue.push_back(std::move(msg));
                        msg.clear();
                    }
                }
                sess->Flush(nowMs, ackSegs, (conv == proto::kKcpConvAux) ? 24U : 0U);
            }
            if (!m_KcpDeliveredQueue.empty())
            {
                outPlain = std::move(m_KcpDeliveredQueue.front());
                m_KcpDeliveredQueue.pop_front();
                delivered = true;
            }
        }
        if (!ackSegs.empty())
        {
            SendKcpSegments(ackSegs);
        }
        m_KcpPumpCv.notify_one();
        return delivered;
    }

    bool MobileTransport::SendEncrypted(proto::AppPacketType type, const void* appPayload, size_t len)
    {
        if (!m_Connected.load(std::memory_order_acquire) || m_Closed.load(std::memory_order_acquire))
            return false;
        if (!appPayload || len < sizeof(proto::AppPacketHeader)) return false;

        if (!m_RelayMode.load(std::memory_order_acquire) && m_KcpActive.load(std::memory_order_acquire))
        {
            const uint32_t conv = proto::SelectKcpChannel(type, appPayload, len);
            if (conv != 0)
            {
                std::vector<uint8_t> appMsg(len);
                std::memcpy(appMsg.data(), appPayload, len);
                appMsg[0] = static_cast<uint8_t>(type);

                std::vector<std::vector<uint8_t>> outSegs;
                bool sent = false;
                {
                    std::lock_guard<std::mutex> kl(m_KcpMutex);
                    kcp::KcpSession* sess = (conv == proto::kKcpConvControl) ? m_KcpCtrl.get() : m_KcpAux.get();
                    if (sess)
                    {
                        const uint32_t nowMs = static_cast<uint32_t>(NowMillis());
                        if (conv == proto::kKcpConvControl &&
                            proto::IsDroppableStalePress(type, appMsg.data(), appMsg.size()) &&
                            sess->IsCongestedStale(nowMs, 48U, 1200U))
                        {
                            return true;
                        }
                        if (sess->Send(appMsg.data(), appMsg.size(), nowMs))
                        {
                            sess->Flush(nowMs, outSegs, (conv == proto::kKcpConvAux) ? 24U : 0U);
                            sent = true;
                        }
                    }
                }
                if (sent)
                {
                    if (!outSegs.empty())
                    {
                        SendKcpSegments(outSegs);
                    }
                    m_KcpPumpCv.notify_one();
                    return true;
                }
            }
        }

        return SendRawEncrypted(type, appPayload, len);
    }

    bool MobileTransport::SendRawEncrypted(proto::AppPacketType type, const void* appPayload, size_t len)
    {
        if (!m_Connected.load(std::memory_order_acquire) || m_Closed.load(std::memory_order_acquire))
            return false;
        if (!appPayload || len < sizeof(proto::AppPacketHeader)) return false;

        std::lock_guard<std::mutex> lock(m_SendMutex);
        std::vector<uint8_t> plain(len);
        std::memcpy(plain.data(), appPayload, len);
        plain[0] = static_cast<uint8_t>(type);

        std::vector<uint8_t> wire(sizeof(proto::EncryptedFrameHeader) + len);
        size_t wireLen = 0;
        bool ok = m_EncryptionEnabled.load(std::memory_order_acquire)
                      ? EncryptFrame(plain.data(), plain.size(), wire.data(), wireLen)
                      : FramePlaintext(plain.data(), plain.size(), wire.data(), wireLen);
        if (!ok) return false;
        wire.resize(wireLen);

        if (m_RelayMode.load(std::memory_order_acquire))
        {
            bool droppable = (type == proto::AppPacketType::VideoChunk ||
                              type == proto::AppPacketType::InputMouseMove);
            std::lock_guard<std::mutex> ql(m_RelaySendMutex);
            if (droppable)
            {
                if (m_RelayDropSendQueue.size() > 128)
                {
                    m_RelayDropSendQueue.pop_front();
                }
                m_RelayDropSendQueue.push_back({std::move(wire), true});
            }
            else
            {
                m_RelayHighSendQueue.push_back({std::move(wire), false});
            }
            return true;
        }

        return RawSendTo(m_ActiveSock, m_PeerAddr, m_PeerAddrLen, wire.data(), wire.size());
    }

    int MobileTransport::ReceiveEncrypted(std::vector<uint8_t>& outPlain, int timeoutMs)
    {
        if (m_Closed.load(std::memory_order_acquire)) return -1;

        if (!m_RelayMode.load(std::memory_order_acquire) && m_KcpActive.load(std::memory_order_acquire))
        {
            std::lock_guard<std::mutex> kl(m_KcpMutex);
            if (!m_KcpDeliveredQueue.empty())
            {
                outPlain = std::move(m_KcpDeliveredQueue.front());
                m_KcpDeliveredQueue.pop_front();
                return 1;
            }
        }

        if (m_RelayMode.load(std::memory_order_acquire))
        {
            std::vector<uint8_t> wire;
            {
                std::unique_lock<std::mutex> lock(m_RelayRecvMutex);
                if (m_RelayRecvQueue.empty())
                {
                    m_RelayRecvCv.wait_for(
                        lock,
                        std::chrono::milliseconds(std::max(1, timeoutMs)),
                        [this] { return !m_RelayRecvQueue.empty() || m_Closed.load(std::memory_order_acquire); });
                }
                if (m_Closed.load(std::memory_order_acquire)) return -1;
                if (m_RelayRecvQueue.empty()) return 0;
                wire = std::move(m_RelayRecvQueue.front());
                m_RelayRecvQueue.pop_front();
            }
            bool ok = m_EncryptionEnabled.load(std::memory_order_acquire)
                          ? DecryptFrame(wire.data(), wire.size(), outPlain)
                          : UnframePlaintext(wire.data(), wire.size(), outPlain);
            return ok ? 1 : 0;
        }

        if (m_ActiveSock < 0) return -1;
        const uint64_t deadline = NowMillis() + static_cast<uint64_t>(std::max(1, timeoutMs));
        while (!m_Closed.load(std::memory_order_acquire))
        {
            uint8_t wireBuf[32768];
            sockaddr_storage from{};
            socklen_t fromLen = sizeof(from);
            ssize_t n = ::recvfrom(m_ActiveSock, wireBuf, sizeof(wireBuf), 0,
                                   reinterpret_cast<sockaddr*>(&from), &fromLen);
            if (n < 0)
            {
                if (errno != EAGAIN && errno != EWOULDBLOCK)
                {
                    return m_Closed.load(std::memory_order_acquire) ? -1 : 0;
                }
                uint64_t now = NowMillis();
                if (now >= deadline) return 0;
                int rem = static_cast<int>(deadline - now);
                pollfd pfd{m_ActiveSock, POLLIN, 0};
                int sel = ::poll(&pfd, 1, std::max(1, rem));
                if (sel == 0) return 0;
                if (sel < 0) return m_Closed.load(std::memory_order_acquire) ? -1 : 0;
                continue;
            }
            if (n == 0) continue;
            if (n >= 24 && std::memcmp(wireBuf, proto::kMagic, 4) == 0 && wireBuf[4] == proto::kVersion)
            {
                auto wireType = static_cast<proto::WireMsgType>(wireBuf[5]);
                if (wireType == proto::WireMsgType::Punch &&
                    std::memcmp(wireBuf + 8, m_SessionToken, 16) == 0 &&
                    wireBuf[6] != static_cast<uint8_t>(m_LocalRole))
                {
                    uint8_t ack[24]{};
                    std::memcpy(ack, proto::kMagic, 4);
                    ack[4] = proto::kVersion;
                    ack[5] = static_cast<uint8_t>(proto::WireMsgType::PunchAck);
                    ack[6] = static_cast<uint8_t>(m_LocalRole);
                    std::memcpy(ack + 8, m_SessionToken, 16);
                    RawSendTo(m_ActiveSock, from, fromLen, ack, sizeof(ack));
                    continue;
                }
                if (wireType == proto::WireMsgType::PunchAck)
                {
                    continue;
                }
            }

            bool ok = m_EncryptionEnabled.load(std::memory_order_acquire)
                          ? DecryptFrame(wireBuf, static_cast<size_t>(n), outPlain)
                          : UnframePlaintext(wireBuf, static_cast<size_t>(n), outPlain);
            if (!ok) continue;

            if (outPlain.size() > sizeof(proto::AppPacketHeader) &&
                static_cast<proto::AppPacketType>(outPlain[0]) == proto::AppPacketType::KcpSegment)
            {
                std::vector<uint8_t> kcpRaw = std::move(outPlain);
                if (HandleIncomingKcpSegment(
                        kcpRaw.data() + sizeof(proto::AppPacketHeader),
                        kcpRaw.size() - sizeof(proto::AppPacketHeader),
                        outPlain))
                {
                    return 1;
                }
                continue;
            }
            return 1;
        }
        return -1;
    }

    int32_t MobileTransport::AuthenticateClient(
        const uint8_t authTag[32],
        int32_t desiredWidth,
        int32_t desiredHeight,
        int32_t desiredFps,
        int32_t desiredBitrateKbps,
        int32_t codecPreference,
        int32_t enableHdr,
        int32_t timeoutMs,
        std::string& outError)
    {
        proto::AuthRequestPayload req{};
        req.hdr.type = static_cast<uint8_t>(proto::AppPacketType::AuthRequest);
        if (authTag) std::memcpy(req.authTag, authTag, 32);
        req.desiredWidth = desiredWidth;
        req.desiredHeight = desiredHeight;
        req.desiredFps = desiredFps;
        req.desiredBitrateKbps = desiredBitrateKbps;
        req.codecPreference = codecPreference;
        req.enableHdr = enableHdr;

        uint64_t deadline = NowMillis() + static_cast<uint64_t>(std::max(1000, timeoutMs));
        uint64_t lastSend = 0;

        std::vector<uint8_t> plain;
        while (!m_Closed.load(std::memory_order_acquire) && NowMillis() < deadline)
        {
            uint64_t now = NowMillis();
            if (lastSend == 0 || now - lastSend >= 150ULL)
            {
                SendEncrypted(proto::AppPacketType::AuthRequest, &req, sizeof(req));
                lastSend = now;
            }

            int r = ReceiveEncrypted(plain, 50);
            if (r < 0)
            {
                outError = "Transport closed during authentication";
                return 0;
            }
            if (r == 0 || plain.size() < sizeof(proto::AppPacketHeader)) continue;

            auto type = static_cast<proto::AppPacketType>(plain[0]);
            if (type == proto::AppPacketType::AuthResponse &&
                plain.size() >= sizeof(proto::AuthResponsePayload))
            {
                const auto* resp = reinterpret_cast<const proto::AuthResponsePayload*>(plain.data());
                if (resp->accepted != 0)
                {
                    LOGI("Client authenticated with host");
                    return 1;
                }
                char msgBuf[97]{};
                std::memcpy(msgBuf, resp->message, 96);
                outError = msgBuf[0] ? std::string(msgBuf) : "Host rejected authentication";
                return -1;
            }
        }
        outError = "Timed out waiting for host authentication response";
        return 0;
    }

    uint32_t MobileTransport::GetPacketLossPercentX100() const
    {
        uint64_t recv = m_RecvPacketCount.load(std::memory_order_relaxed);
        uint64_t lost = m_LostPacketCount.load(std::memory_order_relaxed);
        uint64_t total = recv + lost;
        if (total == 0) return 0;
        return static_cast<uint32_t>((lost * 10000ULL) / total);
    }
}
