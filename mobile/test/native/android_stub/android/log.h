// Minimal replacement for <android/log.h> so the shipping MobileCrypto.cpp can
// be compiled and linked by the host-side vector test.
#pragma once
#include <stdarg.h>

#define ANDROID_LOG_ERROR 6

#ifdef __cplusplus
extern "C" {
#endif

static inline int __android_log_print(int prio, const char* tag, const char* fmt, ...)
{
    (void)prio;
    (void)tag;
    (void)fmt;
    return 0;
}

#ifdef __cplusplus
}
#endif
