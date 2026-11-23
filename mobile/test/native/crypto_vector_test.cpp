// Byte-for-byte check of the Android crypto/framing implementation against
// vectors produced by the Windows algorithm (BCrypt via .NET, see
// vectors_gen/Program.cs and the generated crypto_vectors.h).
//
// The translation unit compiles the exact sources that ship in the APK:
//   android/app/src/main/cpp/MobileCrypto.cpp
//   native/osmium-engine/include/OsmiumProtocol.h
// The frame construction below mirrors MobileTransport::EncryptFrame /
// DecryptFrame (android/app/src/main/cpp/MobileTransport.cpp) line for line.
//
// Build + run: test/native/run_native_tests.ps1 (MSVC + bundled mbedTLS).

#include "MobileCrypto.h"
#include "OsmiumProtocol.h"
#include "crypto_vectors.h"

#include <mbedtls/pk.h>

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace
{
    int g_failures = 0;

    void Check(bool condition, const char* what)
    {
        if (condition)
        {
            std::printf("  ok   %s\n", what);
        }
        else
        {
            std::printf("  FAIL %s\n", what);
            ++g_failures;
        }
    }

    bool EqualsHex(const uint8_t* actual, size_t actualLen, const uint8_t* expected, size_t expectedLen)
    {
        return actualLen == expectedLen && std::memcmp(actual, expected, expectedLen) == 0;
    }

    int TestRng(void*, unsigned char* out, size_t len)
    {
        return osmium::mobile::RandomBytes(out, len) ? 0 : -1;
    }

    bool ImportFixedKey(const uint8_t* der, size_t len, osmium::mobile::EcdhKeyPair& out)
    {
        auto* pk = new mbedtls_pk_context();
        mbedtls_pk_init(pk);
        if (mbedtls_pk_parse_key(pk, der, len, nullptr, 0, TestRng, nullptr) != 0)
        {
            mbedtls_pk_free(pk);
            delete pk;
            return false;
        }
        uint8_t buf[256]{};
        int n = mbedtls_pk_write_pubkey_der(pk, buf, sizeof(buf));
        if (n <= 0)
        {
            mbedtls_pk_free(pk);
            delete pk;
            return false;
        }
        const uint8_t* start = buf + (sizeof(buf) - static_cast<size_t>(n));
        out.pubSpkiDer.assign(start, start + n);
        out.opaquePk = pk;
        return true;
    }

    // Mirrors MobileTransport::EncryptFrame.
    std::vector<uint8_t> EncryptFrame(
        const osmium::mobile::Aes256GcmContext& ctx,
        uint64_t seq,
        uint8_t role,
        const uint8_t prefix[4],
        const uint8_t* plain,
        size_t plainLen)
    {
        std::vector<uint8_t> wire(sizeof(osmium::proto::EncryptedFrameHeader) + plainLen);
        auto* hdr = reinterpret_cast<osmium::proto::EncryptedFrameHeader*>(wire.data());
        std::memcpy(hdr->magic, osmium::proto::kMagic, 4);
        hdr->version = osmium::proto::kVersion;
        hdr->msgType = static_cast<uint8_t>(osmium::proto::WireMsgType::EncryptedData);
        hdr->flags = static_cast<uint16_t>((seq >> 32) & 0xFFFFU);
        hdr->sequence = static_cast<uint32_t>(seq & 0xFFFFFFFFULL);
        std::memcpy(hdr->nonce, prefix, 4);
        hdr->nonce[4] = role;
        for (int i = 0; i < 7; ++i)
        {
            hdr->nonce[5 + i] = static_cast<uint8_t>((seq >> (i * 8)) & 0xFFU);
        }
        const bool ok = ctx.Encrypt(
            hdr->nonce,
            wire.data(),
            12,
            plain,
            plainLen,
            wire.data() + sizeof(osmium::proto::EncryptedFrameHeader),
            hdr->tag);
        if (!ok) wire.clear();
        return wire;
    }
}

int main()
{
    using namespace osmium::mobile;

    std::printf("Osmium mobile crypto/framing vectors (Windows BCrypt fixture)\n");

    std::printf("[ecdh + hkdf + auth tag]\n");
    EcdhKeyPair clientKey;
    Check(ImportFixedKey(osmium_vectors::kClientPkcs8, osmium_vectors::kClientPkcs8Len, clientKey),
          "client PKCS#8 parses");
    Check(EqualsHex(clientKey.pubSpkiDer.data(), clientKey.pubSpkiDer.size(),
                    osmium_vectors::kClientSpki, osmium_vectors::kClientSpkiLen),
          "exported client SPKI equals fixture");

    uint8_t aesKey[32]{};
    uint8_t authTag[32]{};
    const bool derived = DeriveSessionMaterial(
        clientKey,
        osmium_vectors::kHostSpki,
        osmium_vectors::kHostSpkiLen,
        osmium_vectors::kToken,
        osmium_vectors::kChallengeNonce,
        osmium_vectors::kChallengeNonceLen,
        osmium_vectors::kAccessCode,
        aesKey,
        authTag);
    Check(derived, "DeriveSessionMaterial succeeds");
    Check(EqualsHex(aesKey, 32, osmium_vectors::kAesKey, osmium_vectors::kAesKeyLen),
          "HKDF-SHA256 session key matches");
    Check(EqualsHex(authTag, 32, osmium_vectors::kAuthTag, osmium_vectors::kAuthTagLen),
          "HMAC auth tag matches (trim + uppercase, inner spaces kept)");

    std::printf("[directional subkeys]\n");
    uint8_t c2h[32]{};
    uint8_t h2c[32]{};
    Check(DeriveDirectionalSubkey(aesKey, "OSM1-C2H-AES256-GCM", c2h), "C2H subkey derives");
    Check(DeriveDirectionalSubkey(aesKey, "OSM1-H2C-AES256-GCM", h2c), "H2C subkey derives");
    Check(EqualsHex(c2h, 32, osmium_vectors::kC2hKey, osmium_vectors::kC2hKeyLen),
          "C2H subkey matches Windows");
    Check(EqualsHex(h2c, 32, osmium_vectors::kH2cKey, osmium_vectors::kH2cKeyLen),
          "H2C subkey matches Windows");

    std::printf("[client -> host frame, seq=1, role=client]\n");
    Aes256GcmContext c2hCtx;
    Check(c2hCtx.SetKey(c2h), "AES-GCM key set");
    const uint8_t clientPrefix[4] = {0xDE, 0xAD, 0xBE, 0xEF};
    std::vector<uint8_t> clientFrame =
        EncryptFrame(c2hCtx, 1, static_cast<uint8_t>(osmium::proto::PeerRole::Client),
                     clientPrefix, osmium_vectors::kClientPlain, osmium_vectors::kClientPlainLen);
    Check(!clientFrame.empty(), "frame encrypts");
    Check(EqualsHex(clientFrame.data(), clientFrame.size(),
                    osmium_vectors::kClientFrame, osmium_vectors::kClientFrameLen),
          "40-byte header + ciphertext + tag match Windows byte for byte");

    std::printf("[host -> client frame, seq=0x100000005, role=host]\n");
    const auto* hostHdr =
        reinterpret_cast<const osmium::proto::EncryptedFrameHeader*>(osmium_vectors::kHostFrame);
    Check(std::memcmp(hostHdr->magic, osmium::proto::kMagic, 4) == 0, "magic is OSM1");
    Check(hostHdr->version == osmium::proto::kVersion, "version byte");
    Check(hostHdr->msgType == static_cast<uint8_t>(osmium::proto::WireMsgType::EncryptedData),
          "encrypted-data msg type");
    const uint64_t hostSeq = (static_cast<uint64_t>(hostHdr->flags) << 32) | hostHdr->sequence;
    Check(hostSeq == 0x0000000100000005ULL, "flags/sequence split matches");

    Aes256GcmContext h2cCtx;
    Check(h2cCtx.SetKey(h2c), "H2C AES-GCM key set");
    std::vector<uint8_t> hostPlain(osmium_vectors::kHostFrameLen - sizeof(osmium::proto::EncryptedFrameHeader));
    const size_t cipherLen = hostPlain.size();
    Check(h2cCtx.Decrypt(
              hostHdr->nonce,
              osmium_vectors::kHostFrame,
              12,
              osmium_vectors::kHostFrame + sizeof(osmium::proto::EncryptedFrameHeader),
              cipherLen,
              hostHdr->tag,
              hostPlain.data()),
          "host frame decrypts");
    Check(EqualsHex(hostPlain.data(), hostPlain.size(),
                    osmium_vectors::kHostPlain, osmium_vectors::kHostPlainLen),
          "decrypted host payload matches");

    if (g_failures == 0)
    {
        std::printf("PASS all crypto/framing vectors\n");
        return 0;
    }
    std::printf("FAIL %d check(s)\n", g_failures);
    return 1;
}
