#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

namespace osmium::mobile
{
    struct EcdhKeyPair
    {
        void* opaquePk = nullptr;
        std::vector<uint8_t> pubSpkiDer;

        EcdhKeyPair() = default;
        ~EcdhKeyPair();
        EcdhKeyPair(const EcdhKeyPair&) = delete;
        EcdhKeyPair& operator=(const EcdhKeyPair&) = delete;
    };

    bool RandomBytes(uint8_t* out, size_t len);

    bool GenerateEcdhP256KeyPair(EcdhKeyPair& outPair);

    bool DeriveSessionMaterial(
        const EcdhKeyPair& localPair,
        const uint8_t* peerPubSpkiDer,
        size_t peerPubLen,
        const uint8_t sessionToken16[16],
        const uint8_t* challengeNonce,
        size_t nonceLen,
        const char* accessCodeUtf8,
        uint8_t outAesKey32[32],
        uint8_t outAuthTag32[32]);

    bool DeriveDirectionalSubkey(
        const uint8_t masterKey32[32],
        const char* label,
        uint8_t outSubkey32[32]);

    class Aes256GcmContext
    {
    public:
        Aes256GcmContext();
        ~Aes256GcmContext();

        Aes256GcmContext(const Aes256GcmContext&) = delete;
        Aes256GcmContext& operator=(const Aes256GcmContext&) = delete;

        bool SetKey(const uint8_t key32[32]);
        bool IsInitialized() const { return m_Initialized; }

        bool Encrypt(
            const uint8_t nonce[12],
            const uint8_t* aad,
            size_t aadLen,
            const uint8_t* plain,
            size_t plainLen,
            uint8_t* cipherOut,
            uint8_t tagOut[16]) const;

        bool Decrypt(
            const uint8_t nonce[12],
            const uint8_t* aad,
            size_t aadLen,
            const uint8_t* cipher,
            size_t cipherLen,
            const uint8_t tag[16],
            uint8_t* plainOut) const;

    private:
        void* m_Ctx = nullptr;
        bool m_Initialized = false;
    };
}
