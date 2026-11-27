#include "OsmiumTransport.h"

#include <mswsock.h>
#include <iphlpapi.h>
#include <algorithm>
#include <chrono>
#include <cstring>
#include <regex>
#include <sstream>

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "iphlpapi.lib")
#pragma comment(lib, "bcrypt.lib")

namespace osmium
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
                return memcmp(&a6->sin6_addr, &b6->sin6_addr, sizeof(in6_addr)) == 0 &&
                    a6->sin6_port == b6->sin6_port;
            }
            return false;
        }

        enum class IPv4Class { Invalid, Loopback, Lan, Public };

        IPv4Class ClassifyIPv4(uint32_t ipHost)
        {
            if ((ipHost >> 24) == 0 || ipHost >= 0xE0000000U) return IPv4Class::Invalid;
            if ((ipHost >> 24) == 127) return IPv4Class::Loopback;
            if ((ipHost & 0xFFC00000U) == 0x64400000U) return IPv4Class::Invalid;
            if ((ipHost >> 16) == 0xA9FEU) return IPv4Class::Invalid;
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
            if ((a6.u.Byte[0] & 0xE0) != 0x20) return false;
            if (a6.u.Byte[0] == 0x20 && a6.u.Byte[1] == 0x01 && a6.u.Byte[2] == 0x00 && a6.u.Byte[3] == 0x00) return
                false;
            if (a6.u.Byte[0] == 0x20 && a6.u.Byte[1] == 0x02) return false;
            if (a6.u.Byte[0] == 0x20 && a6.u.Byte[1] == 0x01 && a6.u.Byte[2] == 0x0D && a6.u.Byte[3] == 0xB8) return
                false;
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
            if (ss.ss_family == AF_INET6)
            {
                return 2;
            }
            return 3;
        }

        uint16_t ReadBE16(const uint8_t* p)
        {
            return static_cast<uint16_t>((uint16_t(p[0]) << 8) | uint16_t(p[1]));
        }

        void WriteBE16(uint8_t* p, uint16_t v)
        {
            p[0] = static_cast<uint8_t>((v >> 8) & 0xff);
            p[1] = static_cast<uint8_t>(v & 0xff);
        }
    }

    OsmiumTransport::OsmiumTransport()
    {
        if (!BCRYPT_SUCCESS(
            BCryptGenRandom(nullptr, m_NoncePrefix, sizeof(m_NoncePrefix), BCRYPT_USE_SYSTEM_PREFERRED_RNG)))
        {
            LARGE_INTEGER pc{};
            QueryPerformanceCounter(&pc);
            uint32_t mix = static_cast<uint32_t>(pc.QuadPart ^ (pc.QuadPart >> 32) ^ GetCurrentProcessId() ^
                GetCurrentThreadId());
            memcpy(m_NoncePrefix, &mix, sizeof(m_NoncePrefix));
        }
    }

    OsmiumTransport::~OsmiumTransport()
    {
        Close();
        std::lock_guard<std::mutex> lockSend(m_SendMutex);
        std::lock_guard<std::mutex> lockRecv(m_RecvCryptoMutex);
        if (m_EncKey)
        {
            BCryptDestroyKey(m_EncKey);
            m_EncKey = nullptr;
        }
        if (m_DecKey)
        {
            BCryptDestroyKey(m_DecKey);
            m_DecKey = nullptr;
        }
        if (!m_EncKeyObj.empty())
        {
            SecureZeroMemory(m_EncKeyObj.data(), m_EncKeyObj.size());
            m_EncKeyObj.clear();
        }
        if (!m_DecKeyObj.empty())
        {
            SecureZeroMemory(m_DecKeyObj.data(), m_DecKeyObj.size());
            m_DecKeyObj.clear();
        }
        if (m_AesAlg)
        {
            BCryptCloseAlgorithmProvider(m_AesAlg, 0);
            m_AesAlg = nullptr;
        }
    }

    void OsmiumTransport::Close()
    {
        m_Closed.store(true);
        m_Connected.store(false);
        StopKcp();
        {
            std::lock_guard<std::mutex> rl(m_RelaySendMutex);
            m_RelaySendFn = nullptr;
        }
        {
            std::lock_guard<std::mutex> ql(m_RelayRecvMutex);
            m_RelayRecvQueue.clear();
            m_RelayFreePool.clear();
        }
        m_RelayRecvCv.notify_all();
        SOCKET s4 = m_Sock4;
        SOCKET s6 = m_Sock6;
        m_Sock4 = INVALID_SOCKET;
        m_Sock6 = INVALID_SOCKET;
        m_ActiveSock = INVALID_SOCKET;
        m_UdpConnected.store(false, std::memory_order_release);
        m_PunchAttemptCount = 0;
        if (s4 != INVALID_SOCKET) closesocket(s4);
        if (s6 != INVALID_SOCKET && s6 != s4) closesocket(s6);
    }

    bool OsmiumTransport::ResolveIPv4(const char* host, uint16_t port, sockaddr_in& outAddr)
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

    bool OsmiumTransport::InitializeAndProbeStun(
        const char* stunHost,
        uint16_t stunPort,
        std::string& outCandidatesJson)
    {
        m_Sock4 = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        if (m_Sock4 == INVALID_SOCKET)
        {
            OSM_LOG_ERROR("Failed to create IPv4 UDP socket: %d", WSAGetLastError());
            return false;
        }

        auto configureUdpSock = [](SOCKET s)
        {
            // Keep SO_RCVBUF generous (4 MB) to absorb RX bursts without drops,
            // but cap SO_SNDBUF at 512 KB to eliminate multi-second TX bufferbloat
            // when Windows NDIS multimedia throttling or 2.4GHz Wi-Fi slows draining.
            int rcvBuf = 4 * 1024 * 1024;
            int sndBuf = 512 * 1024;
            setsockopt(s, SOL_SOCKET, SO_RCVBUF, reinterpret_cast<const char*>(&rcvBuf), sizeof(rcvBuf));
            setsockopt(s, SOL_SOCKET, SO_SNDBUF, reinterpret_cast<const char*>(&sndBuf), sizeof(sndBuf));
            BOOL newBehavior = FALSE;
            DWORD bytesReturned = 0;
            WSAIoctl(s, SIO_UDP_CONNRESET, &newBehavior, sizeof(newBehavior), nullptr, 0, &bytesReturned, nullptr,
                     nullptr);
        };

        configureUdpSock(m_Sock4);

        sockaddr_in bindAddr4{};
        bindAddr4.sin_family = AF_INET;
        bindAddr4.sin_addr.s_addr = INADDR_ANY;
        bindAddr4.sin_port = 0;
        if (bind(m_Sock4, reinterpret_cast<const sockaddr*>(&bindAddr4), sizeof(bindAddr4)) == SOCKET_ERROR)
        {
            OSM_LOG_ERROR("Failed to bind IPv4 UDP socket: %d", WSAGetLastError());
            Close();
            return false;
        }

        int addrLen4 = sizeof(bindAddr4);
        getsockname(m_Sock4, reinterpret_cast<sockaddr*>(&bindAddr4), &addrLen4);
        m_LocalPort4 = ntohs(bindAddr4.sin_port);
        m_ActiveLocalPort.store(m_LocalPort4);

        m_Sock6 = socket(AF_INET6, SOCK_DGRAM, IPPROTO_UDP);
        if (m_Sock6 != INVALID_SOCKET)
        {
            DWORD v6Only = 1;
            setsockopt(m_Sock6, IPPROTO_IPV6, IPV6_V6ONLY, reinterpret_cast<const char*>(&v6Only), sizeof(v6Only));
            configureUdpSock(m_Sock6);

            sockaddr_in6 bindAddr6{};
            bindAddr6.sin6_family = AF_INET6;
            bindAddr6.sin6_addr = in6addr_any;
            bindAddr6.sin6_port = 0;
            if (bind(m_Sock6, reinterpret_cast<const sockaddr*>(&bindAddr6), sizeof(bindAddr6)) == SOCKET_ERROR)
            {
                closesocket(m_Sock6);
                m_Sock6 = INVALID_SOCKET;
            }
            else
            {
                int addrLen6 = sizeof(bindAddr6);
                getsockname(m_Sock6, reinterpret_cast<sockaddr*>(&bindAddr6), &addrLen6);
                m_LocalPort6 = ntohs(bindAddr6.sin6_port);
            }
        }

        std::vector<CandidateEndpoint> candidates;
        m_HasPublicV6 = false;

        ULONG adapterBufLen = 32768;
        std::vector<uint8_t> adapterBuf(adapterBufLen);
        auto* addresses = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(adapterBuf.data());
        ULONG gaaFlags = GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER;
        ULONG ret = GetAdaptersAddresses(AF_UNSPEC, gaaFlags, nullptr, addresses, &adapterBufLen);
        if (ret == ERROR_BUFFER_OVERFLOW)
        {
            adapterBuf.resize(adapterBufLen);
            addresses = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(adapterBuf.data());
            ret = GetAdaptersAddresses(AF_UNSPEC, gaaFlags, nullptr, addresses, &adapterBufLen);
        }

        if (ret == NO_ERROR)
        {
            for (auto* cur = addresses; cur != nullptr; cur = cur->Next)
            {
                if (cur->OperStatus != IfOperStatusUp) continue;
                if (cur->IfType == IF_TYPE_SOFTWARE_LOOPBACK || cur->IfType == IF_TYPE_TUNNEL) continue;

                for (auto* ua = cur->FirstUnicastAddress; ua != nullptr; ua = ua->Next)
                {
                    if (!ua->Address.lpSockaddr) continue;

                    if (ua->Address.lpSockaddr->sa_family == AF_INET)
                    {
                        auto* sa = reinterpret_cast<sockaddr_in*>(ua->Address.lpSockaddr);
                        uint32_t ipHost = ntohl(sa->sin_addr.s_addr);
                        IPv4Class cls = ClassifyIPv4(ipHost);
                        if (cls == IPv4Class::Invalid || cls == IPv4Class::Loopback) continue;

                        CandidateEndpoint ep;
                        memcpy(&ep.addr, sa, sizeof(sockaddr_in));
                        reinterpret_cast<sockaddr_in*>(&ep.addr)->sin_port = htons(m_LocalPort4);
                        ep.addrLen = sizeof(sockaddr_in);
                        if (cls == IPv4Class::Lan)
                        {
                            ep.type = "lan4";
                            ep.tier = 1;
                        }
                        else
                        {
                            ep.type = "pub4";
                            ep.tier = 3;
                        }
                        candidates.push_back(ep);
                    }
                    else if (ua->Address.lpSockaddr->sa_family == AF_INET6 && m_Sock6 != INVALID_SOCKET)
                    {
                        if (ua->DadState != IpDadStatePreferred || ua->ValidLifetime == 0 || ua->PreferredLifetime == 0)
                        {
                            continue;
                        }
                        auto* sa6 = reinterpret_cast<sockaddr_in6*>(ua->Address.lpSockaddr);
                        if (!IsPublicDynamicIPv6(sa6->sin6_addr)) continue;

                        CandidateEndpoint ep;
                        sockaddr_in6 clean6{};
                        clean6.sin6_family = AF_INET6;
                        clean6.sin6_addr = sa6->sin6_addr;
                        clean6.sin6_port = htons(m_LocalPort6);
                        memcpy(&ep.addr, &clean6, sizeof(sockaddr_in6));
                        ep.addrLen = sizeof(sockaddr_in6);
                        ep.type = "pub6";
                        ep.tier = 2;
                        candidates.push_back(ep);
                        m_HasPublicV6 = true;
                    }
                }
            }
        }

        struct StunProbeTarget
        {
            std::string host;
            uint16_t port;
            int maxAttempts;
        };
        std::vector<StunProbeTarget> stunTargets;
        if (stunHost && *stunHost && stunPort > 0)
        {
            stunTargets.push_back({stunHost, stunPort, 3});
        }
        auto addFallbackStun = [&](const char* h, uint16_t p)
        {
            for (const auto& t : stunTargets)
            {
                if (_stricmp(t.host.c_str(), h) == 0 && t.port == p) return;
            }
            stunTargets.push_back({h, p, 2});
        };
        addFallbackStun("stun.miwifi.com", 3478);
        addFallbackStun("stun.qq.com", 3478);
        addFallbackStun("stun.l.google.com", 19302);

        bool stunResolved = false;
        const uint64_t totalStunDeadline = NowMillis() + 2400ULL;
        for (const auto& target : stunTargets)
        {
            if (stunResolved || NowMillis() >= totalStunDeadline) break;

            sockaddr_in stunAddr4{};
            if (!ResolveIPv4(target.host.c_str(), target.port, stunAddr4)) continue;
            if (!m_HasStunKeepaliveAddr)
            {
                m_StunKeepaliveAddr = stunAddr4;
                m_HasStunKeepaliveAddr = true;
            }

            sockaddr_storage stunSs{};
            memcpy(&stunSs, &stunAddr4, sizeof(sockaddr_in));

            const bool useRfc5389 = (target.port == 3478 || target.port == 19302);
            uint8_t osmReq[16]{};
            uint64_t reqId = NowMicros();
            memcpy(osmReq, proto::kMagic, 4);
            osmReq[4] = proto::kVersion;
            osmReq[5] = static_cast<uint8_t>(proto::WireMsgType::StunReq);
            memcpy(osmReq + 8, &reqId, 8);

            uint8_t rfcReq[20]{};
            rfcReq[0] = 0x00;
            rfcReq[1] = 0x01;
            rfcReq[2] = 0x00;
            rfcReq[3] = 0x00;
            rfcReq[4] = 0x21;
            rfcReq[5] = 0x12;
            rfcReq[6] = 0xA4;
            rfcReq[7] = 0x42;
            memcpy(rfcReq + 8, &reqId, 8);
            memcpy(rfcReq + 16, m_NoncePrefix, 4);

            static constexpr uint64_t kStunWaitBudgetsMs[] = {240, 340, 460};
            for (int attempt = 0; attempt < target.maxAttempts && !stunResolved; ++attempt)
            {
                if (NowMillis() >= totalStunDeadline) break;

                if (useRfc5389)
                {
                    RawSendTo(m_Sock4, stunSs, sizeof(sockaddr_in), rfcReq, sizeof(rfcReq));
                }
                else
                {
                    RawSendTo(m_Sock4, stunSs, sizeof(sockaddr_in), osmReq, sizeof(osmReq));
                }

                const uint64_t budget = kStunWaitBudgetsMs[std::min(attempt, 2)];
                const uint64_t attemptDeadline = std::min(NowMillis() + budget, totalStunDeadline);
                while (!stunResolved)
                {
                    uint64_t nowMs = NowMillis();
                    if (nowMs >= attemptDeadline) break;
                    uint64_t remMs = attemptDeadline - nowMs;

                    fd_set rfds;
                    FD_ZERO(&rfds);
                    FD_SET(m_Sock4, &rfds);
                    timeval tv{};
                    tv.tv_sec = static_cast<long>(remMs / 1000);
                    tv.tv_usec = static_cast<long>((remMs % 1000) * 1000);
                    int sel = select(0, &rfds, nullptr, nullptr, &tv);
                    if (sel <= 0) break;

                    uint8_t resp[256]{};
                    sockaddr_in from{};
                    int fromLen = sizeof(from);
                    int n = recvfrom(m_Sock4, reinterpret_cast<char*>(resp), sizeof(resp), 0,
                                     reinterpret_cast<sockaddr*>(&from), &fromLen);
                    if (n <= 0) continue;

                    uint16_t mappedPort = 0;
                    uint8_t mappedIpBytes[4]{};

                    if (n >= 24 && memcmp(resp, proto::kMagic, 4) == 0 &&
                        resp[5] == static_cast<uint8_t>(proto::WireMsgType::StunResp) &&
                        memcmp(resp + 8, &reqId, 8) == 0)
                    {
                        mappedPort = ReadBE16(resp + 20);
                        memcpy(mappedIpBytes, resp + 16, 4);
                    }
                    else if (n >= 20 && resp[0] == 0x01 && resp[1] == 0x01 &&
                        resp[4] == 0x21 && resp[5] == 0x12 && resp[6] == 0xA4 && resp[7] == 0x42 &&
                        memcmp(resp + 8, rfcReq + 8, 12) == 0)
                    {
                        uint16_t attrTotalLen = ReadBE16(resp + 2);
                        int pos = 20;
                        int limit = (std::min)(n, 20 + static_cast<int>(attrTotalLen));
                        while (pos + 4 <= limit)
                        {
                            uint16_t attrType = ReadBE16(resp + pos);
                            uint16_t attrLen = ReadBE16(resp + pos + 2);
                            const uint8_t* val = resp + pos + 4;
                            if (pos + 4 + attrLen > limit) break;

                            if (attrType == 0x0020 && attrLen >= 8 && val[1] == 0x01)
                            {
                                mappedPort = static_cast<uint16_t>(ReadBE16(val + 2) ^ 0x2112U);
                                mappedIpBytes[0] = val[4] ^ 0x21;
                                mappedIpBytes[1] = val[5] ^ 0x12;
                                mappedIpBytes[2] = val[6] ^ 0xA4;
                                mappedIpBytes[3] = val[7] ^ 0x42;
                                break;
                            }
                            else if (attrType == 0x0001 && attrLen >= 8 && val[1] == 0x01 && mappedPort == 0)
                            {
                                mappedPort = ReadBE16(val + 2);
                                memcpy(mappedIpBytes, val + 4, 4);
                            }
                            pos += 4 + ((attrLen + 3) & ~3);
                        }
                    }

                    if (mappedPort > 0)
                    {
                        sockaddr_in mapped4{};
                        mapped4.sin_family = AF_INET;
                        memcpy(&mapped4.sin_addr, mappedIpBytes, 4);
                        mapped4.sin_port = htons(mappedPort);
                        uint32_t mappedHostIp = ntohl(mapped4.sin_addr.s_addr);

                        if (ClassifyIPv4(mappedHostIp) != IPv4Class::Invalid)
                        {
                            CandidateEndpoint srflx;
                            memcpy(&srflx.addr, &mapped4, sizeof(sockaddr_in));
                            srflx.addrLen = sizeof(sockaddr_in);
                            srflx.type = "srflx";
                            srflx.tier = 3;

                            bool dup = false;
                            for (const auto& c : candidates)
                            {
                                if (IsSameAddr(c.addr, srflx.addr))
                                {
                                    dup = true;
                                    break;
                                }
                            }
                            if (!dup)
                            {
                                candidates.push_back(srflx);
                            }
                            OSM_LOG_INFO("STUN reflexive IPv4 endpoint discovered via %s:%u -> %s",
                                         target.host.c_str(), target.port, SockAddrToString(srflx.addr).c_str());
                        }
                        else
                        {
                            OSM_LOG_WARN("Ignored STUN mapped address inside carrier/reserved NAT range");
                        }
                        stunResolved = true;
                    }
                }
            }
        }

        {
            CandidateEndpoint loopback;
            sockaddr_in lb4{};
            lb4.sin_family = AF_INET;
            inet_pton(AF_INET, "127.0.0.1", &lb4.sin_addr);
            lb4.sin_port = htons(m_LocalPort4);
            memcpy(&loopback.addr, &lb4, sizeof(sockaddr_in));
            loopback.addrLen = sizeof(sockaddr_in);
            loopback.type = "loopback";
            loopback.tier = 1;
            candidates.push_back(loopback);
        }

        outCandidatesJson = BuildCandidatesJson(candidates);
        {
            std::string lan4Str, pub6Str, srflxStr, lbStr;
            for (const auto& c : candidates)
            {
                std::string s = SockAddrToString(c.addr);
                if (c.type == "lan4" && lan4Str.empty()) lan4Str = s;
                else if (c.type == "pub6" && pub6Str.empty()) pub6Str = s;
                else if ((c.type == "srflx" || c.type == "pub4") && srflxStr.empty()) srflxStr = s;
                else if (c.type == "loopback" && lbStr.empty()) lbStr = s;
            }
            std::lock_guard<std::mutex> sl(m_StatsMutex);
            m_LocalEndpointStr = !lan4Str.empty()
                                     ? lan4Str
                                     : (!pub6Str.empty() ? pub6Str : (!srflxStr.empty() ? srflxStr : lbStr));
        }
        OSM_LOG_INFO("Direct UDP transport bound (v4=%u, v6=%u, hasPubV6=%d), candidates=%s",
                     m_LocalPort4, m_LocalPort6, m_HasPublicV6 ? 1 : 0, outCandidatesJson.c_str());
        return true;
    }

    std::string OsmiumTransport::BuildCandidatesJson(const std::vector<CandidateEndpoint>& list)
    {
        std::ostringstream oss;
        oss << "[";
        for (size_t i = 0; i < list.size(); ++i)
        {
            if (i > 0) oss << ",";
            char ip[INET6_ADDRSTRLEN] = {};
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
            oss << "{\"ip\":\"" << ip << "\",\"port\":" << port
                << ",\"type\":\"" << list[i].type << "\"}";
        }
        oss << "]";
        return oss.str();
    }

    std::vector<CandidateEndpoint> OsmiumTransport::ParseCandidatesJson(const char* jsonStr)
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
            if (std::regex_search(obj, mIp, ipRegex) && std::regex_search(obj, mPort, portRegex))
            {
                int portVal = std::stoi(mPort[1].str());
                if (portVal <= 0 || portVal > 65535) continue;
                std::string ipStr = mIp[1].str();
                std::string candType = std::regex_search(obj, mType, typeRegex) ? mType[1].str() : "lan4";

                sockaddr_in sa4{};
                if (inet_pton(AF_INET, ipStr.c_str(), &sa4.sin_addr) == 1)
                {
                    uint32_t ipHost = ntohl(sa4.sin_addr.s_addr);
                    IPv4Class cls = ClassifyIPv4(ipHost);
                    if (cls == IPv4Class::Invalid) continue;

                    sa4.sin_family = AF_INET;
                    sa4.sin_port = htons(static_cast<uint16_t>(portVal));
                    CandidateEndpoint ep;
                    memcpy(&ep.addr, &sa4, sizeof(sockaddr_in));
                    ep.addrLen = sizeof(sockaddr_in);
                    ep.type = candType;
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
                    memcpy(&ep.addr, &sa6, sizeof(sockaddr_in6));
                    ep.addrLen = sizeof(sockaddr_in6);
                    ep.type = "pub6";
                    ep.tier = 2;
                    result.push_back(ep);
                }
            }
        }
        return result;
    }

    bool OsmiumTransport::ConnectPeer(
        const uint8_t sessionToken[16],
        proto::PeerRole localRole,
        const char* peerCandidatesJson,
        int32_t punchTimeoutMs,
        int32_t& outTransportRoute,
        std::string& outActiveEndpoint)
    {
        if (m_Sock4 == INVALID_SOCKET || !sessionToken) return false;

        const int32_t attempt = ++m_PunchAttemptCount;
        memcpy(m_SessionToken, sessionToken, 16);
        m_LocalRole = localRole;
        outTransportRoute = 0;

        u_long nbInit = 1;
        ioctlsocket(m_Sock4, FIONBIO, &nbInit);
        if (m_Sock6 != INVALID_SOCKET)
        {
            ioctlsocket(m_Sock6, FIONBIO, &nbInit);
        }

        auto peerCandidates = ParseCandidatesJson(peerCandidatesJson);
        if (attempt >= 3 && !peerCandidates.empty())
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
                    if (!exists)
                    {
                        expanded.push_back(extra);
                    }
                }
            }
            for (auto& ex : expanded)
            {
                peerCandidates.push_back(std::move(ex));
            }
        }

        bool hasTier1 = false, hasTier2 = false, hasTier3 = false;
        {
            std::string pLan4, pPub6, pSrflx;
            for (const auto& c : peerCandidates)
            {
                std::string s = SockAddrToString(c.addr);
                if (c.tier == 1)
                {
                    hasTier1 = true;
                    if (pLan4.empty() && c.type != "loopback") pLan4 = s;
                }
                else if (c.tier == 2 && m_Sock6 != INVALID_SOCKET)
                {
                    hasTier2 = true;
                    if (pPub6.empty()) pPub6 = s;
                }
                else if (c.tier == 3)
                {
                    hasTier3 = true;
                    if (pSrflx.empty()) pSrflx = s;
                }
            }
            std::lock_guard<std::mutex> sl(m_StatsMutex);
            m_RemotePeerHintStr = !pLan4.empty() ? pLan4 : (!pPub6.empty() ? pPub6 : pSrflx);
        }

        if (peerCandidates.empty())
        {
            OSM_LOG_ERROR("No valid direct/P2P peer candidates (carrier NAT/invalid addresses filtered)");
            return false;
        }

        if (attempt >= 2 && m_HasStunKeepaliveAddr)
        {
            uint8_t keepReq[20]{};
            uint64_t reqId = NowMicros();
            keepReq[1] = 0x01;
            keepReq[4] = 0x21;
            keepReq[5] = 0x12;
            keepReq[6] = 0xA4;
            keepReq[7] = 0x42;
            memcpy(keepReq + 8, &reqId, 8);
            memcpy(keepReq + 16, m_NoncePrefix, 4);
            sockaddr_storage stunSs{};
            memcpy(&stunSs, &m_StunKeepaliveAddr, sizeof(sockaddr_in));
            RawSendTo(m_Sock4, stunSs, sizeof(sockaddr_in), keepReq, sizeof(keepReq));
        }

        uint8_t punchPkt[24]{};
        memcpy(punchPkt, proto::kMagic, 4);
        punchPkt[4] = proto::kVersion;
        punchPkt[5] = static_cast<uint8_t>(proto::WireMsgType::Punch);
        punchPkt[6] = static_cast<uint8_t>(localRole);
        memcpy(punchPkt + 8, m_SessionToken, 16);

        uint8_t punchAckPkt[24]{};
        memcpy(punchAckPkt, proto::kMagic, 4);
        punchAckPkt[4] = proto::kVersion;
        punchAckPkt[5] = static_cast<uint8_t>(proto::WireMsgType::PunchAck);
        punchAckPkt[6] = static_cast<uint8_t>(localRole);
        memcpy(punchAckPkt + 8, m_SessionToken, 16);

        const uint64_t tStart = NowMillis();
        const uint64_t tStartTier2 = (attempt == 1 && hasTier1) ? 80ULL : 0ULL;
        const uint64_t tStartTier3 = (attempt == 1) ? (tStartTier2 + (hasTier2 ? 180ULL : 0ULL)) : 0ULL;
        const uint64_t deadline = tStart + static_cast<uint64_t>(std::max(600, punchTimeoutMs));

        uint64_t lastBlastMs = 0;
        uint64_t ackGraceDeadline = 0;
        bool receivedPunch = false;
        bool receivedAck = false;
        int bestTier = 99;
        sockaddr_storage bestPeer{};
        int bestPeerLen = 0;
        SOCKET bestSock = INVALID_SOCKET;

        while (!m_Closed.load())
        {
            uint64_t now = NowMillis();
            if (now >= deadline) break;
            if (ackGraceDeadline > 0 && now >= ackGraceDeadline) break;

            uint64_t elapsed = now - tStart;
            const uint64_t blastIntervalMs = elapsed < 1000ULL ? 35ULL : (elapsed < 2200ULL ? 65ULL : 110ULL);
            if (lastBlastMs == 0 || now - lastBlastMs >= blastIntervalMs)
            {
                lastBlastMs = now;
                for (const auto& cand : peerCandidates)
                {
                    if (cand.tier == 2 && (elapsed < tStartTier2 || m_Sock6 == INVALID_SOCKET)) continue;
                    if (cand.tier == 3 && elapsed < tStartTier3) continue;

                    SOCKET txSock = (cand.addr.ss_family == AF_INET6) ? m_Sock6 : m_Sock4;
                    if (txSock != INVALID_SOCKET)
                    {
                        RawSendTo(txSock, cand.addr, cand.addrLen, punchPkt, sizeof(punchPkt));
                    }
                }
                if (receivedPunch && bestSock != INVALID_SOCKET)
                {
                    RawSendTo(bestSock, bestPeer, bestPeerLen, punchAckPkt, sizeof(punchAckPkt));
                }
            }

            fd_set rfds;
            FD_ZERO(&rfds);
            FD_SET(m_Sock4, &rfds);
            if (m_Sock6 != INVALID_SOCKET)
                FD_SET(m_Sock6, &rfds);
            timeval tv{0, 20000};
            int sel = select(0, &rfds, nullptr, nullptr, &tv);
            if (sel > 0)
            {
                SOCKET readySocks[2] = {m_Sock4, m_Sock6};
                for (SOCKET rs : readySocks)
                {
                    if (rs == INVALID_SOCKET || !FD_ISSET(rs, &rfds)) continue;
                    for (int drain = 0; drain < 32; ++drain)
                    {
                        uint8_t buf[2048];
                        sockaddr_storage from{};
                        int fromLen = sizeof(from);
                        int n = recvfrom(rs, reinterpret_cast<char*>(buf), sizeof(buf), 0,
                                         reinterpret_cast<sockaddr*>(&from), &fromLen);
                        if (n <= 0) break;
                        if (n >= 24 && memcmp(buf, proto::kMagic, 4) == 0 && buf[4] == proto::kVersion)
                        {
                            auto type = static_cast<proto::WireMsgType>(buf[5]);
                            if ((type == proto::WireMsgType::Punch || type == proto::WireMsgType::PunchAck) &&
                                memcmp(buf + 8, m_SessionToken, 16) == 0 &&
                                buf[6] != static_cast<uint8_t>(localRole))
                            {
                                int tier = ClassifySockAddrTier(from);
                                for (const auto& c : peerCandidates)
                                {
                                    if (IsSameAddr(c.addr, from))
                                    {
                                        tier = c.tier;
                                        break;
                                    }
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
                    if (ackGraceDeadline > 0 && NowMillis() >= ackGraceDeadline) break;
                }
            }
        }

        if (!receivedPunch || bestSock == INVALID_SOCKET)
        {
            OSM_LOG_WARN("Direct LAN/IPv6/P2P hole punch did not complete; eligible for cloud relay fallback");
            return false;
        }

        for (int i = 0; i < 4; ++i)
        {
            if (i > 0) Sleep(10);
            RawSendTo(bestSock, bestPeer, bestPeerLen, punchAckPkt, sizeof(punchAckPkt));
        }

        m_ActiveSock.store(bestSock, std::memory_order_release);
        m_PeerDirectAddr = bestPeer;
        m_PeerDirectAddrLen = bestPeerLen;
        m_ActiveLocalPort.store((bestSock == m_Sock6) ? m_LocalPort6 : m_LocalPort4);
        m_Route.store(RouteFromTier(bestTier));
        m_Connected.store(true);

        if (bestSock == m_Sock4 && m_Sock6 != INVALID_SOCKET)
        {
            closesocket(m_Sock6);
            m_Sock6 = INVALID_SOCKET;
        }
        else if (bestSock == m_Sock6 && m_Sock4 != INVALID_SOCKET)
        {
            closesocket(m_Sock4);
            m_Sock4 = INVALID_SOCKET;
        }

        u_long nonBlocking = 1;
        ioctlsocket(bestSock, FIONBIO, &nonBlocking);
        // Keep DSCP at default Best-Effort (0x00 / WMM AC_BE). Marking high-bitrate
        // video UDP sockets with EF (0xB8 / WMM AC_VO) disables A-MPDU aggregation
        // and triggers AP voice-queue policing/drops on Wi-Fi LAN & P2P links.
        int tos = 0;
        if (m_PeerDirectAddr.ss_family == AF_INET6)
        {
            setsockopt(bestSock, IPPROTO_IPV6, IPV6_TCLASS, reinterpret_cast<const char*>(&tos), sizeof(tos));
        }
        else
        {
            setsockopt(bestSock, IPPROTO_IP, IP_TOS, reinterpret_cast<const char*>(&tos), sizeof(tos));
        }

        if (connect(bestSock, reinterpret_cast<const sockaddr*>(&m_PeerDirectAddr), m_PeerDirectAddrLen) == 0)
        {
            m_UdpConnected.store(true, std::memory_order_release);
        }
        else
        {
            m_UdpConnected.store(false, std::memory_order_release);
        }

        outTransportRoute = m_Route.load();
        outActiveEndpoint = SockAddrToString(m_PeerDirectAddr);
        if (m_KcpEnabled.load(std::memory_order_acquire))
        {
            StartKcp();
        }
        const char* routeLabel = outTransportRoute == 1
                                     ? "LAN-IPv4 Direct"
                                     : (outTransportRoute == 2 ? "IPv6 Direct" : "IPv4 P2P Direct");
        OSM_LOG_INFO("%s established with %s", routeLabel, outActiveEndpoint.c_str());
        return true;
    }

    namespace
    {
        bool DeriveDirectionalSubkey(const uint8_t masterKey[32], const char* label, uint8_t outSubkey[32])
        {
            BCRYPT_ALG_HANDLE hAlg = nullptr;
            BCRYPT_HASH_HANDLE hHash = nullptr;
            bool ok = false;

            if (BCRYPT_SUCCESS(
                BCryptOpenAlgorithmProvider(&hAlg, BCRYPT_SHA256_ALGORITHM, nullptr, BCRYPT_ALG_HANDLE_HMAC_FLAG)))
            {
                DWORD objLen = 0, cb = 0;
                if (BCRYPT_SUCCESS(
                        BCryptGetProperty(hAlg, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&objLen), sizeof(objLen),
                            &cb, 0)) &&
                    objLen > 0)
                {
                    std::vector<uint8_t> hashObj(objLen, 0);
                    if (BCRYPT_SUCCESS(BCryptCreateHash(
                        hAlg, &hHash, hashObj.data(), objLen,
                        const_cast<PUCHAR>(masterKey), 32, 0)))
                    {
                        if (BCRYPT_SUCCESS(
                                BCryptHashData(hHash, reinterpret_cast<PUCHAR>(const_cast<char*>(label)), static_cast<
                                    ULONG>(strlen(label)), 0)) &&
                            BCRYPT_SUCCESS(BCryptFinishHash(hHash, outSubkey, 32, 0)))
                        {
                            ok = true;
                        }
                        BCryptDestroyHash(hHash);
                    }
                    SecureZeroMemory(hashObj.data(), hashObj.size());
                }
                BCryptCloseAlgorithmProvider(hAlg, 0);
            }
            return ok;
        }
    }

    bool OsmiumTransport::SetSessionKey(const uint8_t aesKey[32])
    {
        if (!aesKey) return false;
        std::lock_guard<std::mutex> lockSend(m_SendMutex);
        std::lock_guard<std::mutex> lockRecv(m_RecvCryptoMutex);

        if (m_EncKey)
        {
            BCryptDestroyKey(m_EncKey);
            m_EncKey = nullptr;
        }
        if (m_DecKey)
        {
            BCryptDestroyKey(m_DecKey);
            m_DecKey = nullptr;
        }
        if (!m_AesAlg)
        {
            NTSTATUS st = BCryptOpenAlgorithmProvider(&m_AesAlg, BCRYPT_AES_ALGORITHM, nullptr, 0);
            if (!BCRYPT_SUCCESS(st))
            {
                OSM_LOG_ERROR("BCryptOpenAlgorithmProvider failed: 0x%08X", st);
                return false;
            }
            st = BCryptSetProperty(
                m_AesAlg,
                BCRYPT_CHAINING_MODE,
                reinterpret_cast<PUCHAR>(const_cast<wchar_t*>(BCRYPT_CHAIN_MODE_GCM)),
                sizeof(BCRYPT_CHAIN_MODE_GCM),
                0);
            if (!BCRYPT_SUCCESS(st))
            {
                OSM_LOG_ERROR("BCryptSetProperty(GCM) failed: 0x%08X", st);
                return false;
            }
        }

        uint8_t c2hKey[32]{};
        uint8_t h2cKey[32]{};
        if (!DeriveDirectionalSubkey(aesKey, "OSM1-C2H-AES256-GCM", c2hKey) ||
            !DeriveDirectionalSubkey(aesKey, "OSM1-H2C-AES256-GCM", h2cKey))
        {
            SecureZeroMemory(c2hKey, sizeof(c2hKey));
            SecureZeroMemory(h2cKey, sizeof(h2cKey));
            return false;
        }

        const uint8_t* encBytes = (m_LocalRole == proto::PeerRole::Host) ? h2cKey : c2hKey;
        const uint8_t* decBytes = (m_LocalRole == proto::PeerRole::Host) ? c2hKey : h2cKey;

        DWORD keyObjLen = 0, cbData = 0;
        NTSTATUS stLen = BCryptGetProperty(
            m_AesAlg,
            BCRYPT_OBJECT_LENGTH,
            reinterpret_cast<PUCHAR>(&keyObjLen),
            sizeof(keyObjLen),
            &cbData,
            0);
        if (!BCRYPT_SUCCESS(stLen) || keyObjLen == 0)
        {
            keyObjLen = 1024;
        }

        m_EncKeyObj.assign(keyObjLen, 0);
        m_DecKeyObj.assign(keyObjLen, 0);

        NTSTATUS st1 = BCryptGenerateSymmetricKey(
            m_AesAlg,
            &m_EncKey,
            m_EncKeyObj.data(),
            static_cast<ULONG>(m_EncKeyObj.size()),
            const_cast<PUCHAR>(encBytes),
            32,
            0);
        NTSTATUS st2 = BCryptGenerateSymmetricKey(
            m_AesAlg,
            &m_DecKey,
            m_DecKeyObj.data(),
            static_cast<ULONG>(m_DecKeyObj.size()),
            const_cast<PUCHAR>(decBytes),
            32,
            0);

        SecureZeroMemory(c2hKey, sizeof(c2hKey));
        SecureZeroMemory(h2cKey, sizeof(h2cKey));

        if (!BCRYPT_SUCCESS(st1) || !BCRYPT_SUCCESS(st2))
        {
            OSM_LOG_ERROR("BCryptGenerateSymmetricKey failed: enc=0x%08X dec=0x%08X", st1, st2);
            return false;
        }

        return true;
    }

    bool OsmiumTransport::RawSendTo(SOCKET sock, const sockaddr_storage& target, int addrLen, const void* data,
                                    size_t len)
    {
        if (sock == INVALID_SOCKET || m_Closed.load() || addrLen <= 0) return false;
        const char* ptr = reinterpret_cast<const char*>(data);
        int iLen = static_cast<int>(len);
        int sent = sendto(sock, ptr, iLen, 0, reinterpret_cast<const sockaddr*>(&target), addrLen);
        if (sent == SOCKET_ERROR && WSAGetLastError() == WSAEWOULDBLOCK)
        {
            Sleep(0);
            sent = sendto(sock, ptr, iLen, 0, reinterpret_cast<const sockaddr*>(&target), addrLen);
        }
        return sent == iLen;
    }

    bool OsmiumTransport::EncryptFrameWithEncKey(const uint8_t* plain, size_t plainLen, uint8_t* outWire,
                                                 size_t& outWireLen)
    {
        if (!m_EncKey) return false;

        outWireLen = sizeof(proto::EncryptedFrameHeader) + plainLen;
        auto* hdr = reinterpret_cast<proto::EncryptedFrameHeader*>(outWire);
        memcpy(hdr->magic, proto::kMagic, 4);
        hdr->version = proto::kVersion;
        hdr->msgType = static_cast<uint8_t>(proto::WireMsgType::EncryptedData);

        const uint64_t seq64 = m_SendSequence.fetch_add(1, std::memory_order_relaxed);
        hdr->flags = static_cast<uint16_t>((seq64 >> 32) & 0xFFFFU);
        hdr->sequence = static_cast<uint32_t>(seq64 & 0xFFFFFFFFULL);

        memcpy(hdr->nonce, m_NoncePrefix, 4);
        hdr->nonce[4] = static_cast<uint8_t>(m_LocalRole);
        for (int i = 0; i < 7; ++i)
        {
            hdr->nonce[5 + i] = static_cast<uint8_t>((seq64 >> (i * 8)) & 0xFFU);
        }

        BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO authInfo;
        BCRYPT_INIT_AUTH_MODE_INFO(authInfo);
        authInfo.pbNonce = hdr->nonce;
        authInfo.cbNonce = sizeof(hdr->nonce);
        authInfo.pbAuthData = reinterpret_cast<PUCHAR>(hdr);
        authInfo.cbAuthData = 12;
        authInfo.pbTag = hdr->tag;
        authInfo.cbTag = sizeof(hdr->tag);

        ULONG cbResult = 0;
        NTSTATUS st = BCryptEncrypt(
            m_EncKey,
            const_cast<PUCHAR>(plain),
            static_cast<ULONG>(plainLen),
            &authInfo,
            nullptr,
            0,
            outWire + sizeof(proto::EncryptedFrameHeader),
            static_cast<ULONG>(plainLen),
            &cbResult,
            0);

        return BCRYPT_SUCCESS(st);
    }

    bool OsmiumTransport::FramePlaintext(const uint8_t* plain, size_t plainLen, uint8_t* outWire, size_t& outWireLen)
    {
        outWireLen = sizeof(proto::EncryptedFrameHeader) + plainLen;
        auto* hdr = reinterpret_cast<proto::EncryptedFrameHeader*>(outWire);
        memset(hdr, 0, sizeof(*hdr));
        memcpy(hdr->magic, proto::kMagic, 4);
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
            memcpy(outWire + sizeof(proto::EncryptedFrameHeader), plain, plainLen);
        }
        return true;
    }

    bool OsmiumTransport::RecordReceivedSequence(uint64_t seq)
    {
        uint64_t newlyLost = 0;
        if (m_MaxRecvSeq == 0)
        {
            m_FirstRecvSeq = seq;
            m_MaxRecvSeq = seq;
            m_RecvWindowMask = 1ULL;
        }
        else if (seq > m_MaxRecvSeq)
        {
            uint64_t adv = seq - m_MaxRecvSeq;
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
            m_MaxRecvSeq = seq;
            if (newlyLost > 0) m_LostPacketCount.fetch_add(newlyLost, std::memory_order_relaxed);
        }
        else
        {
            uint64_t diff = m_MaxRecvSeq - seq;
            if (diff >= 4096)
            {
                return false;
            }
            if (diff < 64)
            {
                const uint64_t bit = 1ULL << diff;
                if ((m_RecvWindowMask & bit) != 0)
                {
                    return false;
                }
                m_RecvWindowMask |= bit;
            }
        }

        m_RecvPacketCount.fetch_add(1, std::memory_order_relaxed);

        const uint64_t nowMs = NowMillis();
        if (m_WinStartMs == 0)
        {
            m_WinStartMs = nowMs;
        }
        else if (nowMs - m_WinStartMs >= 1000ULL)
        {
            if (nowMs - m_WinStartMs >= 2000ULL)
            {
                m_PrevWinRecv = 0;
                m_PrevWinLost = 0;
            }
            else
            {
                m_PrevWinRecv = m_WinRecv;
                m_PrevWinLost = m_WinLost;
            }
            m_WinRecv = 0;
            m_WinLost = 0;
            m_WinStartMs = nowMs;
        }
        m_WinRecv += 1;
        m_WinLost += newlyLost;
        const uint64_t winTotal = m_WinRecv + m_PrevWinRecv + m_WinLost + m_PrevWinLost;
        const uint32_t pctX100 = (winTotal > 0)
                                     ? static_cast<uint32_t>((std::min<uint64_t>)(
                                           10000ULL, ((m_WinLost + m_PrevWinLost) * 10000ULL) / winTotal))
                                     : 0U;
        m_RecentLossX100.store(pctX100, std::memory_order_relaxed);
        return true;
    }

    bool OsmiumTransport::UnframePlaintext(const uint8_t* wire, size_t wireLen, std::vector<uint8_t>& outPlain)
    {
        if (wireLen < sizeof(proto::EncryptedFrameHeader)) return false;

        const auto* hdr = reinterpret_cast<const proto::EncryptedFrameHeader*>(wire);
        if (memcmp(hdr->magic, proto::kMagic, 4) != 0 ||
            hdr->version != proto::kVersion ||
            hdr->msgType != static_cast<uint8_t>(proto::WireMsgType::PlainData))
        {
            return false;
        }

        const uint8_t expectedPeerRole = (m_LocalRole == proto::PeerRole::Client)
                                             ? static_cast<uint8_t>(proto::PeerRole::Host)
                                             : static_cast<uint8_t>(proto::PeerRole::Client);
        if (hdr->nonce[4] != expectedPeerRole)
        {
            return false;
        }

        const uint64_t seq = (static_cast<uint64_t>(hdr->flags) << 32) | static_cast<uint64_t>(hdr->sequence);
        if (seq == 0) return false;
        for (int i = 0; i < 6; ++i)
        {
            if (hdr->nonce[5 + i] != static_cast<uint8_t>((seq >> (i * 8)) & 0xFFU))
            {
                return false;
            }
        }

        if (!RecordReceivedSequence(seq)) return false;

        size_t bodyLen = wireLen - sizeof(proto::EncryptedFrameHeader);
        outPlain.resize(bodyLen);
        if (bodyLen > 0)
        {
            memcpy(outPlain.data(), wire + sizeof(proto::EncryptedFrameHeader), bodyLen);
        }
        return true;
    }

    bool OsmiumTransport::DecryptFrame(const uint8_t* wire, size_t wireLen, std::vector<uint8_t>& outPlain)
    {
        if (wireLen < sizeof(proto::EncryptedFrameHeader)) return false;

        const auto* hdr = reinterpret_cast<const proto::EncryptedFrameHeader*>(wire);
        if (memcmp(hdr->magic, proto::kMagic, 4) != 0 ||
            hdr->version != proto::kVersion ||
            hdr->msgType != static_cast<uint8_t>(proto::WireMsgType::EncryptedData))
        {
            return false;
        }

        const uint8_t expectedPeerRole = (m_LocalRole == proto::PeerRole::Client)
                                             ? static_cast<uint8_t>(proto::PeerRole::Host)
                                             : static_cast<uint8_t>(proto::PeerRole::Client);
        if (hdr->nonce[4] != expectedPeerRole)
        {
            return false;
        }

        const uint64_t seq = (static_cast<uint64_t>(hdr->flags) << 32) | static_cast<uint64_t>(hdr->sequence);
        if (seq == 0) return false;
        for (int i = 0; i < 6; ++i)
        {
            if (hdr->nonce[5 + i] != static_cast<uint8_t>((seq >> (i * 8)) & 0xFFU))
            {
                return false;
            }
        }

        size_t cipherLen = wireLen - sizeof(proto::EncryptedFrameHeader);
        outPlain.resize(cipherLen);

        {
            std::lock_guard<std::mutex> lock(m_RecvCryptoMutex);
            if (!m_DecKey) return false;

            BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO authInfo;
            BCRYPT_INIT_AUTH_MODE_INFO(authInfo);
            authInfo.pbNonce = const_cast<PUCHAR>(hdr->nonce);
            authInfo.cbNonce = sizeof(hdr->nonce);
            authInfo.pbAuthData = const_cast<PUCHAR>(wire);
            authInfo.cbAuthData = 12;
            authInfo.pbTag = const_cast<PUCHAR>(hdr->tag);
            authInfo.cbTag = sizeof(hdr->tag);

            ULONG cbResult = 0;
            NTSTATUS st = BCryptDecrypt(
                m_DecKey,
                const_cast<PUCHAR>(wire + sizeof(proto::EncryptedFrameHeader)),
                static_cast<ULONG>(cipherLen),
                &authInfo,
                nullptr,
                0,
                outPlain.data(),
                static_cast<ULONG>(cipherLen),
                &cbResult,
                0);

            if (!BCRYPT_SUCCESS(st)) return false;
            outPlain.resize(cbResult);
        }

        return RecordReceivedSequence(seq);
    }

    bool OsmiumTransport::EnableRelay(
        const uint8_t sessionToken[16],
        proto::PeerRole localRole,
        RelaySendFunc sendFn,
        const char* endpointLabel)
    {
        if (!sessionToken || !sendFn || m_Closed.load()) return false;

        StopKcp();
        memcpy(m_SessionToken, sessionToken, 16);
        m_LocalRole = localRole;

        SOCKET s4 = m_Sock4;
        SOCKET s6 = m_Sock6;
        m_Sock4 = INVALID_SOCKET;
        m_Sock6 = INVALID_SOCKET;
        m_ActiveSock = INVALID_SOCKET;
        m_UdpConnected.store(false, std::memory_order_release);
        m_PunchAttemptCount = 0;
        if (s4 != INVALID_SOCKET) closesocket(s4);
        if (s6 != INVALID_SOCKET && s6 != s4) closesocket(s6);

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
            m_WinStartMs = 0;
            m_WinRecv = 0;
            m_WinLost = 0;
            m_PrevWinRecv = 0;
            m_PrevWinLost = 0;
            m_RecentLossX100.store(0, std::memory_order_relaxed);
            m_RecvPacketCount.store(0, std::memory_order_relaxed);
            m_LostPacketCount.store(0, std::memory_order_relaxed);
        }
        {
            std::lock_guard<std::mutex> ql(m_RelayRecvMutex);
            m_RelayRecvQueue.clear();
            m_RelayFreePool.clear();
        }
        {
            std::lock_guard<std::mutex> fnl(m_RelaySendMutex);
            m_RelaySendFn = std::move(sendFn);
        }
        {
            std::lock_guard<std::mutex> st(m_StatsMutex);
            m_RelayEndpointStr = (endpointLabel && *endpointLabel) ? endpointLabel : "Cloud Relay (WSS)";
        }

        m_ActiveLocalPort.store(0);
        m_Route.store(static_cast<int32_t>(TransportRoute::CloudRelay));
        m_RelayMode.store(true, std::memory_order_release);
        m_Connected.store(true, std::memory_order_release);
        OSM_LOG_INFO("OsmiumTransport switched to Cloud Relay (WSS) mode");
        return true;
    }

    bool OsmiumTransport::PushRelayPacket(const uint8_t* data, size_t len)
    {
        if (!m_RelayMode.load(std::memory_order_acquire) || m_Closed.load() || !data || len < 8 || len > 32768)
        {
            return false;
        }
        {
            std::lock_guard<std::mutex> ql(m_RelayRecvMutex);
            if (m_RelayRecvQueue.size() >= 1024)
            {
                auto dropped = std::move(m_RelayRecvQueue.front());
                m_RelayRecvQueue.pop_front();
                if (m_RelayFreePool.size() < 128 && dropped.capacity() <= 36864)
                {
                    dropped.clear();
                    m_RelayFreePool.push_back(std::move(dropped));
                }
            }
            std::vector<uint8_t> buf;
            if (!m_RelayFreePool.empty())
            {
                buf = std::move(m_RelayFreePool.back());
                m_RelayFreePool.pop_back();
            }
            buf.assign(data, data + len);
            m_RelayRecvQueue.push_back(std::move(buf));
        }
        m_RelayRecvCv.notify_one();
        return true;
    }

    bool OsmiumTransport::PushRelayBatch(const uint8_t* batchData, size_t batchLen)
    {
        if (!m_RelayMode.load(std::memory_order_acquire) || m_Closed.load() || !batchData || batchLen < 10)
        {
            return false;
        }

        size_t pushed = 0;
        {
            std::lock_guard<std::mutex> ql(m_RelayRecvMutex);
            size_t pos = 0;
            while (pos + 2 <= batchLen)
            {
                const uint16_t pktLen = static_cast<uint16_t>(batchData[pos]) |
                                        (static_cast<uint16_t>(batchData[pos + 1]) << 8);
                pos += 2;
                if (pktLen == 0 || pos + pktLen > batchLen) break;
                if (pktLen < 8 || pktLen > 32768)
                {
                    pos += pktLen;
                    continue;
                }

                if (m_RelayRecvQueue.size() >= 1024)
                {
                    auto dropped = std::move(m_RelayRecvQueue.front());
                    m_RelayRecvQueue.pop_front();
                    if (m_RelayFreePool.size() < 128 && dropped.capacity() <= 36864)
                    {
                        dropped.clear();
                        m_RelayFreePool.push_back(std::move(dropped));
                    }
                }

                std::vector<uint8_t> buf;
                if (!m_RelayFreePool.empty())
                {
                    buf = std::move(m_RelayFreePool.back());
                    m_RelayFreePool.pop_back();
                }
                buf.assign(batchData + pos, batchData + pos + pktLen);
                m_RelayRecvQueue.push_back(std::move(buf));
                pushed++;
                pos += pktLen;
            }
        }

        if (pushed > 0)
        {
            m_RelayRecvCv.notify_one();
            return true;
        }
        return false;
    }

    void OsmiumTransport::SetKcpEnabled(bool enabled)
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

    void OsmiumTransport::StartKcp()
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
            m_KcpPumpThread = std::thread(&OsmiumTransport::KcpPumpLoop, this);
        }
        m_KcpPumpCv.notify_one();
    }

    void OsmiumTransport::StopKcp()
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

    void OsmiumTransport::SendKcpSegments(const std::vector<std::vector<uint8_t>>& segments)
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
                memset(pktBuf, 0, sizeof(proto::AppPacketHeader));
                memcpy(pktBuf + sizeof(proto::AppPacketHeader), seg.data(), seg.size());
                SendRawEncrypted(proto::AppPacketType::KcpSegment, pktBuf,
                                 sizeof(proto::AppPacketHeader) + seg.size());
            }
            else
            {
                std::vector<uint8_t> dynBuf(sizeof(proto::AppPacketHeader) + seg.size(), 0);
                memcpy(dynBuf.data() + sizeof(proto::AppPacketHeader), seg.data(), seg.size());
                SendRawEncrypted(proto::AppPacketType::KcpSegment, dynBuf.data(), dynBuf.size());
            }
        }
    }

    void OsmiumTransport::KcpPumpLoop()
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

    bool OsmiumTransport::HandleIncomingKcpSegment(
        const uint8_t* segData,
        size_t segLen,
        std::vector<uint8_t>& outPlaintext)
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
                outPlaintext = std::move(m_KcpDeliveredQueue.front());
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

    bool OsmiumTransport::SendEncrypted(
        proto::AppPacketType pktType,
        const void* payload,
        size_t payloadLen)
    {
        if (!payload || payloadLen < sizeof(proto::AppPacketHeader)) return false;
        const bool isRelay = m_RelayMode.load(std::memory_order_acquire);
        if (!m_Connected.load() || m_Closed.load() || (!isRelay && m_ActiveSock == INVALID_SOCKET)) return false;

        if (!isRelay && m_KcpActive.load(std::memory_order_acquire))
        {
            const uint32_t conv = proto::SelectKcpChannel(pktType, payload, payloadLen);
            if (conv != 0)
            {
                std::vector<uint8_t> appMsg(payloadLen);
                memcpy(appMsg.data(), payload, payloadLen);
                appMsg[0] = static_cast<uint8_t>(pktType);

                std::vector<std::vector<uint8_t>> outSegs;
                bool sent = false;
                {
                    std::lock_guard<std::mutex> kl(m_KcpMutex);
                    kcp::KcpSession* sess = (conv == proto::kKcpConvControl) ? m_KcpCtrl.get() : m_KcpAux.get();
                    if (sess)
                    {
                        const uint32_t nowMs = static_cast<uint32_t>(NowMillis());
                        if (conv == proto::kKcpConvControl &&
                            proto::IsDroppableStalePress(pktType, appMsg.data(), appMsg.size()) &&
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

        return SendRawEncrypted(pktType, payload, payloadLen);
    }

    bool OsmiumTransport::SendRawEncrypted(
        proto::AppPacketType pktType,
        const void* payload,
        size_t payloadLen)
    {
        if (!payload || payloadLen < sizeof(proto::AppPacketHeader)) return false;
        const bool isRelay = m_RelayMode.load(std::memory_order_acquire);
        const SOCKET activeSock = m_ActiveSock.load(std::memory_order_acquire);
        if (!m_Connected.load(std::memory_order_acquire) || m_Closed.load(std::memory_order_acquire) ||
            (!isRelay && activeSock == INVALID_SOCKET))
        {
            return false;
        }

        // Classify priority/drop hint for relay & WebRTC P2P transports (zero ABI break):
        //   0        : critical control (Ping/Pong/Input/Auth/Control/IDR/Aux)
        //  -1        : real-time audio (bounded low-latency lane, never HoL-blocks control)
        //  <= -2     : video keyframe (group = -(frameId + 2))
        //  > 0       : droppable video P-frame (group = frameId)
        int32_t dropGroupId = 0;
        if (isRelay)
        {
            if (pktType == proto::AppPacketType::AudioFrame ||
                pktType == proto::AppPacketType::ClientMicAudioFrame)
            {
                dropGroupId = -1;
            }
            else if (pktType == proto::AppPacketType::VideoChunk && payloadLen >= sizeof(proto::VideoChunkHeader))
            {
                const auto* vhdr = reinterpret_cast<const proto::VideoChunkHeader*>(payload);
                const uint32_t g = vhdr->frameId & 0x3FFFFFFFU;
                if (vhdr->isKeyFrame == 0)
                {
                    dropGroupId = static_cast<int32_t>(g == 0 ? 1U : g);
                }
                else
                {
                    dropGroupId = -static_cast<int32_t>(g + 2U);
                }
            }
        }

        constexpr size_t kFastStackPlainCap = 17408;
        alignas(16) uint8_t stackPlain[kFastStackPlainCap];
        alignas(16) uint8_t stackWire[sizeof(proto::EncryptedFrameHeader) + kFastStackPlainCap];
        std::vector<uint8_t> dynPlain;
        std::vector<uint8_t> dynWire;

        uint8_t* plainPtr = nullptr;
        uint8_t* wireBuf = nullptr;
        if (payloadLen <= kFastStackPlainCap)
        {
            plainPtr = stackPlain;
            wireBuf = stackWire;
        }
        else
        {
            dynPlain.resize(payloadLen);
            dynWire.resize(sizeof(proto::EncryptedFrameHeader) + payloadLen);
            plainPtr = dynPlain.data();
            wireBuf = dynWire.data();
        }

        memcpy(plainPtr, payload, payloadLen);
        reinterpret_cast<proto::AppPacketHeader*>(plainPtr)->type = static_cast<uint8_t>(pktType);

        size_t wireLen = 0;
        bool ok = false;
        {
            std::lock_guard<std::mutex> lock(m_SendMutex);
            const bool useEncryption = m_EncryptionEnabled.load(std::memory_order_acquire);
            if ((useEncryption && !m_EncKey) || m_Closed.load(std::memory_order_acquire))
            {
                if (payloadLen <= 256 || pktType == proto::AppPacketType::AuthRequest)
                {
                    SecureZeroMemory(plainPtr, payloadLen);
                }
                return false;
            }
            ok = useEncryption
                     ? EncryptFrameWithEncKey(plainPtr, payloadLen, wireBuf, wireLen)
                     : FramePlaintext(plainPtr, payloadLen, wireBuf, wireLen);
        }

        if (payloadLen <= 256 || pktType == proto::AppPacketType::AuthRequest)
        {
            SecureZeroMemory(plainPtr, payloadLen);
        }
        if (!ok)
        {
            return false;
        }

        // Socket / Relay I/O runs completely outside m_SendMutex so audio, Ping/Pong,
        // input, KCP ACKs, and video bursts never block each other on syscalls or P/Invoke.
        if (isRelay)
        {
            RelaySendFunc fn;
            {
                std::lock_guard<std::mutex> rl(m_RelaySendMutex);
                fn = m_RelaySendFn;
            }
            if (!fn || m_Closed.load(std::memory_order_acquire)) return false;
            return fn(wireBuf, wireLen, dropGroupId);
        }

        const SOCKET sendSock = m_ActiveSock.load(std::memory_order_acquire);
        if (sendSock == INVALID_SOCKET || m_Closed.load(std::memory_order_acquire)) return false;

        if (m_UdpConnected.load(std::memory_order_acquire))
        {
            const char* ptr = reinterpret_cast<const char*>(wireBuf);
            const int iLen = static_cast<int>(wireLen);
            int sent = send(sendSock, ptr, iLen, 0);
            if (sent == SOCKET_ERROR && WSAGetLastError() == WSAEWOULDBLOCK)
            {
                SwitchToThread();
                sent = send(sendSock, ptr, iLen, 0);
            }
            return sent == iLen;
        }

        return RawSendTo(sendSock, m_PeerDirectAddr, m_PeerDirectAddrLen, wireBuf, wireLen);
    }

    int OsmiumTransport::ReceiveEncrypted(std::vector<uint8_t>& outPlaintext, int timeoutMs)
    {
        if (m_Closed.load()) return -1;

        if (!m_RelayMode.load(std::memory_order_acquire) && m_KcpActive.load(std::memory_order_acquire))
        {
            std::lock_guard<std::mutex> kl(m_KcpMutex);
            if (!m_KcpDeliveredQueue.empty())
            {
                outPlaintext = std::move(m_KcpDeliveredQueue.front());
                m_KcpDeliveredQueue.pop_front();
                return static_cast<int>(outPlaintext.size());
            }
        }

        if (m_RelayMode.load(std::memory_order_acquire))
        {
            const bool nonBlocking = (timeoutMs <= 0);
            const auto deadline = nonBlocking
                                      ? std::chrono::steady_clock::time_point{}
                                      : (std::chrono::steady_clock::now() + std::chrono::milliseconds(timeoutMs));
            while (!m_Closed.load())
            {
                std::vector<uint8_t> pkt;
                {
                    std::unique_lock<std::mutex> ql(m_RelayRecvMutex);
                    if (m_RelayRecvQueue.empty())
                    {
                        if (nonBlocking) return 0;
                        if (!m_RelayRecvCv.wait_until(ql, deadline, [this]
                            {
                                return m_Closed.load() || !m_RelayRecvQueue.empty();
                            }))
                        {
                            return 0;
                        }
                    }
                    if (m_Closed.load()) return -1;
                    if (m_RelayRecvQueue.empty()) continue;
                    pkt = std::move(m_RelayRecvQueue.front());
                    m_RelayRecvQueue.pop_front();
                }

                auto recyclePkt = [this](std::vector<uint8_t>& buf)
                {
                    if (buf.capacity() > 0 && buf.capacity() <= 36864)
                    {
                        std::lock_guard<std::mutex> ql(m_RelayRecvMutex);
                        if (m_RelayFreePool.size() < 128)
                        {
                            buf.clear();
                            m_RelayFreePool.push_back(std::move(buf));
                        }
                    }
                };

                if (pkt.size() < 8 || memcmp(pkt.data(), proto::kMagic, 4) != 0)
                {
                    recyclePkt(pkt);
                    continue;
                }
                auto wireType = static_cast<proto::WireMsgType>(pkt[5]);
                if (wireType == proto::WireMsgType::Punch || wireType == proto::WireMsgType::PunchAck)
                {
                    recyclePkt(pkt);
                    continue;
                }

                bool decoded = false;
                if (m_EncryptionEnabled.load(std::memory_order_acquire))
                {
                    decoded = DecryptFrame(pkt.data(), pkt.size(), outPlaintext);
                }
                else
                {
                    decoded = UnframePlaintext(pkt.data(), pkt.size(), outPlaintext);
                }
                recyclePkt(pkt);
                if (decoded)
                {
                    return static_cast<int>(outPlaintext.size());
                }
            }
            return -1;
        }

        const SOCKET activeSock = m_ActiveSock.load(std::memory_order_acquire);
        if (activeSock == INVALID_SOCKET) return -1;

        const bool nonBlocking = (timeoutMs <= 0);
        uint64_t deadline = 0;
        const bool udpConn = m_UdpConnected.load(std::memory_order_acquire);
        while (!m_Closed.load())
        {
            uint8_t buf[4096];
            sockaddr_storage from{};
            int fromLen = sizeof(from);
            int n = udpConn
                        ? recv(activeSock, reinterpret_cast<char*>(buf), sizeof(buf), 0)
                        : recvfrom(activeSock, reinterpret_cast<char*>(buf), sizeof(buf), 0,
                                   reinterpret_cast<sockaddr*>(&from), &fromLen);

            if (n < 0)
            {
                int wsaErr = WSAGetLastError();
                if (wsaErr != WSAEWOULDBLOCK)
                {
                    return m_Closed.load() ? -1 : 0;
                }
                if (nonBlocking) return 0;

                const uint64_t now = NowMillis();
                if (deadline == 0) deadline = now + static_cast<uint64_t>(timeoutMs);
                if (now >= deadline) return 0;

                uint64_t remMs = deadline - now;
                fd_set rfds;
                FD_ZERO(&rfds);
                FD_SET(activeSock, &rfds);
                timeval tv{};
                tv.tv_sec = static_cast<long>(remMs / 1000);
                tv.tv_usec = static_cast<long>((remMs % 1000) * 1000);

                int sel = select(0, &rfds, nullptr, nullptr, &tv);
                if (sel == 0) return 0;
                if (sel < 0) return m_Closed.load() ? -1 : 0;
                continue;
            }

            if (n == 0)
            {
                if (m_Closed.load()) return -1;
                continue;
            }
            if (n < 8 || memcmp(buf, proto::kMagic, 4) != 0) continue;

            auto wireType = static_cast<proto::WireMsgType>(buf[5]);
            if (wireType == proto::WireMsgType::Punch)
            {
                if (n >= 24 && memcmp(buf + 8, m_SessionToken, 16) == 0 && buf[6] != static_cast<uint8_t>(m_LocalRole))
                {
                    uint8_t ack[24]{};
                    memcpy(ack, proto::kMagic, 4);
                    ack[4] = proto::kVersion;
                    ack[5] = static_cast<uint8_t>(proto::WireMsgType::PunchAck);
                    ack[6] = static_cast<uint8_t>(m_LocalRole);
                    memcpy(ack + 8, m_SessionToken, 16);
                    if (udpConn)
                    {
                        send(activeSock, reinterpret_cast<const char*>(ack), sizeof(ack), 0);
                    }
                    else
                    {
                        RawSendTo(activeSock, from, fromLen, ack, sizeof(ack));
                    }
                }
                continue;
            }
            if (wireType == proto::WireMsgType::PunchAck)
            {
                continue;
            }

            bool decoded = false;
            if (m_EncryptionEnabled.load(std::memory_order_acquire))
            {
                decoded = DecryptFrame(buf, static_cast<size_t>(n), outPlaintext);
            }
            else
            {
                decoded = UnframePlaintext(buf, static_cast<size_t>(n), outPlaintext);
            }
            if (decoded)
            {
                if (outPlaintext.size() > sizeof(proto::AppPacketHeader) &&
                    static_cast<proto::AppPacketType>(outPlaintext[0]) == proto::AppPacketType::KcpSegment)
                {
                    std::vector<uint8_t> kcpRaw = std::move(outPlaintext);
                    if (HandleIncomingKcpSegment(
                            kcpRaw.data() + sizeof(proto::AppPacketHeader),
                            kcpRaw.size() - sizeof(proto::AppPacketHeader),
                            outPlaintext))
                    {
                        return static_cast<int>(outPlaintext.size());
                    }
                    continue;
                }
                return static_cast<int>(outPlaintext.size());
            }
        }

        return -1;
    }

    std::string OsmiumTransport::GetActiveEndpointString() const
    {
        if (!m_Connected.load()) return "";
        if (m_RelayMode.load(std::memory_order_acquire))
        {
            std::lock_guard<std::mutex> sl(m_StatsMutex);
            return m_RelayEndpointStr;
        }
        return SockAddrToString(m_PeerDirectAddr);
    }

    std::string OsmiumTransport::GetLocalEndpointString() const
    {
        std::lock_guard<std::mutex> sl(m_StatsMutex);
        return m_LocalEndpointStr;
    }

    std::string OsmiumTransport::GetRemoteEndpointString() const
    {
        if (m_Connected.load())
        {
            if (m_RelayMode.load(std::memory_order_acquire))
            {
                std::lock_guard<std::mutex> sl(m_StatsMutex);
                return m_RelayEndpointStr;
            }
            return SockAddrToString(m_PeerDirectAddr);
        }
        std::lock_guard<std::mutex> sl(m_StatsMutex);
        return m_RemotePeerHintStr;
    }

    void OsmiumTransport::GetPacketStats(uint64_t& outReceived, uint64_t& outLost) const
    {
        outReceived = m_RecvPacketCount.load(std::memory_order_relaxed);
        outLost = m_LostPacketCount.load(std::memory_order_relaxed);
    }
}
