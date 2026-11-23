#include "MobileCrypto.h"

#include <android/log.h>
#include <mbedtls/ctr_drbg.h>
#include <mbedtls/ecdh.h>
#include <mbedtls/ecp.h>
#include <mbedtls/entropy.h>
#include <mbedtls/gcm.h>
#include <mbedtls/hkdf.h>
#include <mbedtls/md.h>
#include <mbedtls/pk.h>
#include <mbedtls/platform_util.h>
#include <mbedtls/sha256.h>

#include <algorithm>
#include <cctype>
#include <cstring>
#include <mutex>
#include <string>

#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, "OsmiumCrypto", __VA_ARGS__)

namespace osmium::mobile
{
    namespace
    {
        struct GlobalRng
        {
            std::mutex mtx;
            mbedtls_entropy_context entropy;
            mbedtls_ctr_drbg_context drbg;
            bool ready = false;

            GlobalRng()
            {
                mbedtls_entropy_init(&entropy);
                mbedtls_ctr_drbg_init(&drbg);
                const char* pers = "osmium-mobile-v1";
                if (mbedtls_ctr_drbg_seed(
                        &drbg,
                        mbedtls_entropy_func,
                        &entropy,
                        reinterpret_cast<const unsigned char*>(pers),
                        std::strlen(pers)) == 0)
                {
                    ready = true;
                }
            }

            ~GlobalRng()
            {
                mbedtls_ctr_drbg_free(&drbg);
                mbedtls_entropy_free(&entropy);
            }
        };

        GlobalRng& GetRng()
        {
            static GlobalRng rng;
            return rng;
        }

        int RngCallback(void* ctx, unsigned char* output, size_t len)
        {
            auto* rng = static_cast<GlobalRng*>(ctx);
            std::lock_guard<std::mutex> lock(rng->mtx);
            return mbedtls_ctr_drbg_random(&rng->drbg, output, len);
        }

        std::string TrimAndUpperAscii(const char* raw)
        {
            if (!raw) return {};
            std::string s(raw);
            auto notSpace = [](unsigned char ch) { return !std::isspace(ch); };
            s.erase(s.begin(), std::find_if(s.begin(), s.end(), notSpace));
            s.erase(std::find_if(s.rbegin(), s.rend(), notSpace).base(), s.end());
            for (char& c : s)
            {
                if (c >= 'a' && c <= 'z')
                {
                    c = static_cast<char>(c - ('a' - 'A'));
                }
            }
            return s;
        }
    }

    EcdhKeyPair::~EcdhKeyPair()
    {
        if (opaquePk)
        {
            auto* pk = static_cast<mbedtls_pk_context*>(opaquePk);
            mbedtls_pk_free(pk);
            delete pk;
            opaquePk = nullptr;
        }
    }

    bool RandomBytes(uint8_t* out, size_t len)
    {
        if (!out || len == 0) return false;
        auto& rng = GetRng();
        if (!rng.ready) return false;
        std::lock_guard<std::mutex> lock(rng.mtx);
        return mbedtls_ctr_drbg_random(&rng.drbg, out, len) == 0;
    }

    bool GenerateEcdhP256KeyPair(EcdhKeyPair& outPair)
    {
        auto& rng = GetRng();
        if (!rng.ready) return false;

        auto* pk = new mbedtls_pk_context();
        mbedtls_pk_init(pk);

        const mbedtls_pk_info_t* info = mbedtls_pk_info_from_type(MBEDTLS_PK_ECKEY);
        if (!info || mbedtls_pk_setup(pk, info) != 0)
        {
            mbedtls_pk_free(pk);
            delete pk;
            return false;
        }

        mbedtls_ecp_keypair* ec = mbedtls_pk_ec(*pk);
        int ret = mbedtls_ecp_gen_key(MBEDTLS_ECP_DP_SECP256R1, ec, RngCallback, &rng);
        if (ret != 0)
        {
            LOGE("mbedtls_ecp_gen_key failed: -0x%04x", -ret);
            mbedtls_pk_free(pk);
            delete pk;
            return false;
        }

        uint8_t derBuf[256]{};
        int derLen = mbedtls_pk_write_pubkey_der(pk, derBuf, sizeof(derBuf));
        if (derLen <= 0)
        {
            LOGE("mbedtls_pk_write_pubkey_der failed: -0x%04x", -derLen);
            mbedtls_pk_free(pk);
            delete pk;
            return false;
        }

        // mbedtls_pk_write_pubkey_der writes at the END of the buffer
        const uint8_t* derStart = derBuf + (sizeof(derBuf) - static_cast<size_t>(derLen));
        outPair.pubSpkiDer.assign(derStart, derStart + derLen);
        outPair.opaquePk = pk;
        return true;
    }

    bool DeriveSessionMaterial(
        const EcdhKeyPair& localPair,
        const uint8_t* peerPubSpkiDer,
        size_t peerPubLen,
        const uint8_t sessionToken16[16],
        const uint8_t* challengeNonce,
        size_t nonceLen,
        const char* accessCodeUtf8,
        uint8_t outAesKey32[32],
        uint8_t outAuthTag32[32])
    {
        if (!localPair.opaquePk || !peerPubSpkiDer || peerPubLen == 0 ||
            !sessionToken16 || !outAesKey32 || !outAuthTag32)
        {
            return false;
        }

        auto& rng = GetRng();
        if (!rng.ready) return false;

        mbedtls_pk_context peerPk;
        mbedtls_pk_init(&peerPk);
        int ret = mbedtls_pk_parse_public_key(&peerPk, peerPubSpkiDer, peerPubLen);
        if (ret != 0 || mbedtls_pk_get_type(&peerPk) != MBEDTLS_PK_ECKEY)
        {
            LOGE("Failed to parse peer SPKI public key: -0x%04x", -ret);
            mbedtls_pk_free(&peerPk);
            return false;
        }

        auto* localPk = static_cast<mbedtls_pk_context*>(localPair.opaquePk);
        mbedtls_ecp_keypair* localEc = mbedtls_pk_ec(*localPk);
        mbedtls_ecp_keypair* peerEc = mbedtls_pk_ec(peerPk);

        mbedtls_ecp_group grp;
        mbedtls_mpi dLocal;
        mbedtls_ecp_point qPeer;
        mbedtls_mpi sharedSecret;

        mbedtls_ecp_group_init(&grp);
        mbedtls_mpi_init(&dLocal);
        mbedtls_ecp_point_init(&qPeer);
        mbedtls_mpi_init(&sharedSecret);

        bool ok = false;
        uint8_t rawSecret[32]{};

        do
        {
            if (mbedtls_ecp_group_load(&grp, MBEDTLS_ECP_DP_SECP256R1) != 0) break;

            // Export local private key d and peer public point Q using public APIs
            if (mbedtls_ecp_export(localEc, nullptr, &dLocal, nullptr) != 0) break;
            if (mbedtls_ecp_export(peerEc, nullptr, nullptr, &qPeer) != 0) break;

            ret = mbedtls_ecdh_compute_shared(
                &grp,
                &sharedSecret,
                &qPeer,
                &dLocal,
                RngCallback,
                &rng);
            if (ret != 0)
            {
                LOGE("mbedtls_ecdh_compute_shared failed: -0x%04x", -ret);
                break;
            }

            if (mbedtls_mpi_write_binary(&sharedSecret, rawSecret, sizeof(rawSecret)) != 0)
            {
                break;
            }

            // HKDF-SHA256(ikm=rawSecret, salt=sessionToken16, info="OSM1-SESSION-KEY-V1", len=32)
            const mbedtls_md_info_t* sha256Info = mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);
            static const char kHkdfInfo[] = "OSM1-SESSION-KEY-V1";
            ret = mbedtls_hkdf(
                sha256Info,
                sessionToken16,
                16,
                rawSecret,
                sizeof(rawSecret),
                reinterpret_cast<const unsigned char*>(kHkdfInfo),
                sizeof(kHkdfInfo) - 1,
                outAesKey32,
                32);
            if (ret != 0)
            {
                LOGE("mbedtls_hkdf failed: -0x%04x", -ret);
                break;
            }

            // authTag = HMAC-SHA256(SHA256(UTF8(UPPER(TRIM(code)))),
            //                       sessionToken16 || challengeNonce || clientPub || hostPub)
            std::string normalizedCode = TrimAndUpperAscii(accessCodeUtf8);
            uint8_t codeHash[32]{};
            mbedtls_sha256(
                reinterpret_cast<const unsigned char*>(normalizedCode.data()),
                normalizedCode.size(),
                codeHash,
                0);

            mbedtls_md_context_t hmacCtx;
            mbedtls_md_init(&hmacCtx);
            if (mbedtls_md_setup(&hmacCtx, sha256Info, 1) == 0 &&
                mbedtls_md_hmac_starts(&hmacCtx, codeHash, sizeof(codeHash)) == 0 &&
                mbedtls_md_hmac_update(&hmacCtx, sessionToken16, 16) == 0 &&
                (nonceLen == 0 || mbedtls_md_hmac_update(&hmacCtx, challengeNonce, nonceLen) == 0) &&
                mbedtls_md_hmac_update(&hmacCtx, localPair.pubSpkiDer.data(), localPair.pubSpkiDer.size()) == 0 &&
                mbedtls_md_hmac_update(&hmacCtx, peerPubSpkiDer, peerPubLen) == 0 &&
                mbedtls_md_hmac_finish(&hmacCtx, outAuthTag32) == 0)
            {
                ok = true;
            }
            mbedtls_md_free(&hmacCtx);
            mbedtls_platform_zeroize(codeHash, sizeof(codeHash));
            if (!normalizedCode.empty())
            {
                mbedtls_platform_zeroize(normalizedCode.data(), normalizedCode.size());
            }
        } while (false);

        mbedtls_platform_zeroize(rawSecret, sizeof(rawSecret));
        mbedtls_mpi_free(&sharedSecret);
        mbedtls_ecp_point_free(&qPeer);
        mbedtls_mpi_free(&dLocal);
        mbedtls_ecp_group_free(&grp);
        mbedtls_pk_free(&peerPk);
        return ok;
    }

    bool DeriveDirectionalSubkey(
        const uint8_t masterKey32[32],
        const char* label,
        uint8_t outSubkey32[32])
    {
        if (!masterKey32 || !label || !outSubkey32) return false;
        const mbedtls_md_info_t* sha256Info = mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);
        return mbedtls_md_hmac(
                   sha256Info,
                   masterKey32,
                   32,
                   reinterpret_cast<const unsigned char*>(label),
                   std::strlen(label),
                   outSubkey32) == 0;
    }

    Aes256GcmContext::Aes256GcmContext()
    {
        auto* ctx = new mbedtls_gcm_context();
        mbedtls_gcm_init(ctx);
        m_Ctx = ctx;
    }

    Aes256GcmContext::~Aes256GcmContext()
    {
        if (m_Ctx)
        {
            auto* ctx = static_cast<mbedtls_gcm_context*>(m_Ctx);
            mbedtls_gcm_free(ctx);
            delete ctx;
            m_Ctx = nullptr;
        }
    }

    bool Aes256GcmContext::SetKey(const uint8_t key32[32])
    {
        if (!m_Ctx || !key32) return false;
        auto* ctx = static_cast<mbedtls_gcm_context*>(m_Ctx);
        mbedtls_gcm_free(ctx);
        mbedtls_gcm_init(ctx);
        int ret = mbedtls_gcm_setkey(ctx, MBEDTLS_CIPHER_ID_AES, key32, 256);
        m_Initialized = (ret == 0);
        return m_Initialized;
    }

    bool Aes256GcmContext::Encrypt(
        const uint8_t nonce[12],
        const uint8_t* aad,
        size_t aadLen,
        const uint8_t* plain,
        size_t plainLen,
        uint8_t* cipherOut,
        uint8_t tagOut[16]) const
    {
        if (!m_Initialized || !m_Ctx) return false;
        auto* ctx = static_cast<mbedtls_gcm_context*>(m_Ctx);
        return mbedtls_gcm_crypt_and_tag(
                   ctx,
                   MBEDTLS_GCM_ENCRYPT,
                   plainLen,
                   nonce,
                   12,
                   aad,
                   aadLen,
                   plain,
                   cipherOut,
                   16,
                   tagOut) == 0;
    }

    bool Aes256GcmContext::Decrypt(
        const uint8_t nonce[12],
        const uint8_t* aad,
        size_t aadLen,
        const uint8_t* cipher,
        size_t cipherLen,
        const uint8_t tag[16],
        uint8_t* plainOut) const
    {
        if (!m_Initialized || !m_Ctx) return false;
        auto* ctx = static_cast<mbedtls_gcm_context*>(m_Ctx);
        return mbedtls_gcm_auth_decrypt(
                   ctx,
                   cipherLen,
                   nonce,
                   12,
                   aad,
                   aadLen,
                   tag,
                   16,
                   cipher,
                   plainOut) == 0;
    }
}
