#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <mutex>
#include <string>

#include "OsmiumNative.h"

namespace osmium
{
    inline uint64_t NowMicros()
    {
        using namespace std::chrono;
        return static_cast<uint64_t>(
            duration_cast<microseconds>(steady_clock::now().time_since_epoch()).count());
    }

    inline uint64_t NowMillis()
    {
        return NowMicros() / 1000ULL;
    }

    class Logger
    {
    public:
        static void SetCallback(OsmiumLogCallback cb)
        {
            std::lock_guard<std::mutex> lock(Mutex());
            Callback() = cb;
        }

        static void Log(const char* level, const char* fmt, ...)
        {
            char body[1024];
            va_list args;
            va_start(args, fmt);
            vsnprintf(body, sizeof(body), fmt, args);
            va_end(args);

            char full[1152];
            snprintf(full, sizeof(full), "[Osmium/%s] %s", level, body);

            OsmiumLogCallback cb = nullptr;
            {
                std::lock_guard<std::mutex> lock(Mutex());
                cb = Callback();
            }
            if (cb)
            {
                cb(full);
            }
            else
            {
                OutputDebugStringA(full);
                OutputDebugStringA("\n");
            }
        }

    private:
        static std::mutex& Mutex()
        {
            static std::mutex m;
            return m;
        }

        static OsmiumLogCallback& Callback()
        {
            static OsmiumLogCallback cb = nullptr;
            return cb;
        }
    };

    inline void CopyStringSafe(char* dst, size_t dstCap, const std::string& src)
    {
        if (!dst || dstCap == 0) return;
        size_t n = src.size();
        if (n >= dstCap) n = dstCap - 1;
        if (n > 0) memcpy(dst, src.data(), n);
        dst[n] = '\0';
    }

    inline std::string SockAddrToString(const sockaddr_in& addr)
    {
        char ip[INET_ADDRSTRLEN] = {};
        inet_ntop(AF_INET, &addr.sin_addr, ip, sizeof(ip));
        return std::string(ip) + ":" + std::to_string(ntohs(addr.sin_port));
    }

    inline std::string SockAddrToString(const sockaddr_storage& ss)
    {
        if (ss.ss_family == AF_INET)
        {
            return SockAddrToString(*reinterpret_cast<const sockaddr_in*>(&ss));
        }
        if (ss.ss_family == AF_INET6)
        {
            const auto* sa6 = reinterpret_cast<const sockaddr_in6*>(&ss);
            char ip[INET6_ADDRSTRLEN] = {};
            inet_ntop(AF_INET6, &sa6->sin6_addr, ip, sizeof(ip));
            return "[" + std::string(ip) + "]:" + std::to_string(ntohs(sa6->sin6_port));
        }
        return "";
    }
}

#define OSM_LOG_INFO(...)  ::osmium::Logger::Log("INFO", __VA_ARGS__)
#define OSM_LOG_WARN(...)  ::osmium::Logger::Log("WARN", __VA_ARGS__)
#define OSM_LOG_ERROR(...) ::osmium::Logger::Log("ERR", __VA_ARGS__)
