#include "ClientMicCapture.h"

#include <functiondiscoverykeys_devpkey.h>
#include <objbase.h>
#include <algorithm>
#include <cstring>

namespace osmium
{
    using Microsoft::WRL::ComPtr;

    namespace
    {
        std::string WideToUtf8(const wchar_t* wide)
        {
            if (!wide || !*wide) return {};
            int n = WideCharToMultiByte(CP_UTF8, 0, wide, -1, nullptr, 0, nullptr, nullptr);
            if (n <= 1) return {};
            std::string s(static_cast<size_t>(n - 1), '\0');
            WideCharToMultiByte(CP_UTF8, 0, wide, -1, s.data(), n, nullptr, nullptr);
            return s;
        }

        std::wstring Utf8ToWideLocal(const std::string& utf8)
        {
            if (utf8.empty()) return {};
            int n = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
            if (n <= 0) return {};
            std::wstring w(static_cast<size_t>(n), L'\0');
            MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), w.data(), n);
            return w;
        }
    }

    ClientMicCapture::~ClientMicCapture()
    {
        Stop();
    }

    std::vector<OsmiumAudioDeviceInfo> ClientMicCapture::EnumerateMicDevices()
    {
        std::vector<OsmiumAudioDeviceInfo> result;
        HRESULT hrCom = CoInitializeEx(nullptr, COINIT_MULTITHREADED);

        {
            ComPtr<IMMDeviceEnumerator> en;
            if (SUCCEEDED(CoCreateInstance(__uuidof(IMMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&en))))
            {
                std::wstring defaultId;
                ComPtr<IMMDevice> defDev;
                if (SUCCEEDED(en->GetDefaultAudioEndpoint(eCapture, eConsole, &defDev)))
                {
                    LPWSTR wid = nullptr;
                    if (SUCCEEDED(defDev->GetId(&wid)) && wid)
                    {
                        defaultId = wid;
                        CoTaskMemFree(wid);
                    }
                }

                ComPtr<IMMDeviceCollection> col;
                if (SUCCEEDED(en->EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, &col)))
                {
                    UINT count = 0;
                    col->GetCount(&count);
                    for (UINT i = 0; i < count; ++i)
                    {
                        ComPtr<IMMDevice> dev;
                        if (FAILED(col->Item(i, &dev))) continue;

                        LPWSTR wid = nullptr;
                        if (FAILED(dev->GetId(&wid)) || !wid) continue;
                        std::wstring idW(wid);
                        CoTaskMemFree(wid);

                        std::wstring friendlyW = L"Microphone";
                        ComPtr<IPropertyStore> ps;
                        if (SUCCEEDED(dev->OpenPropertyStore(STGM_READ, &ps)))
                        {
                            PROPVARIANT pv{};
                            PropVariantInit(&pv);
                            if (SUCCEEDED(ps->GetValue(PKEY_Device_FriendlyName, &pv)) && pv.vt == VT_LPWSTR && pv.
                                pwszVal)
                            {
                                friendlyW = pv.pwszVal;
                            }
                            PropVariantClear(&pv);
                        }

                        if (friendlyW.find(L"Osmium Virtual") != std::wstring::npos)
                        {
                            continue;
                        }

                        OsmiumAudioDeviceInfo info{};
                        info.index = static_cast<int32_t>(result.size());
                        info.isDefault = (!defaultId.empty() && idW == defaultId) ? 1 : 0;
                        CopyStringSafe(info.id, sizeof(info.id), WideToUtf8(idW.c_str()));
                        CopyStringSafe(info.name, sizeof(info.name), WideToUtf8(friendlyW.c_str()));
                        result.push_back(info);
                    }
                }
            }
        }

        if (SUCCEEDED(hrCom)) CoUninitialize();
        return result;
    }

    bool ClientMicCapture::Start(const std::string& deviceIdUtf8, EncodedMicCallback callback)
    {
        Stop();
        m_Callback = std::move(callback);
        m_StopFlag.store(false);
        m_Thread = std::thread(&ClientMicCapture::CaptureThreadProc, this, deviceIdUtf8);
        return true;
    }

    void ClientMicCapture::Stop()
    {
        m_StopFlag.store(true);
        if (m_Thread.joinable())
        {
            m_Thread.join();
        }
        m_Running.store(false);
    }

    void ClientMicCapture::CaptureThreadProc(std::string deviceIdUtf8)
    {
        HRESULT hrCom = CoInitializeEx(nullptr, COINIT_MULTITHREADED);

        ComPtr<IMMDeviceEnumerator> en;
        ComPtr<IMMDevice> device;
        ComPtr<IAudioClient> client;
        ComPtr<IAudioCaptureClient> capture;
        WAVEFORMATEX* mixFmt = nullptr;
        OpusEncoder* opus = nullptr;
        SwrContext* swr = nullptr;

        auto cleanup = [&]()
        {
            m_Running.store(false);
            if (mixFmt)
            {
                CoTaskMemFree(mixFmt);
                mixFmt = nullptr;
            }
            capture.Reset();
            if (client)
            {
                client->Stop();
                client.Reset();
            }
            device.Reset();
            en.Reset();
            if (swr)
            {
                swr_free(&swr);
                swr = nullptr;
            }
            if (opus)
            {
                opus_encoder_destroy(opus);
                opus = nullptr;
            }
            if (SUCCEEDED(hrCom)) CoUninitialize();
        };

        if (FAILED(CoCreateInstance(__uuidof(IMMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&en))))
        {
            cleanup();
            return;
        }

        HRESULT hrDev = E_FAIL;
        if (!deviceIdUtf8.empty())
        {
            std::wstring wid = Utf8ToWideLocal(deviceIdUtf8);
            hrDev = en->GetDevice(wid.c_str(), &device);
        }
        if (FAILED(hrDev) || !device)
        {
            hrDev = en->GetDefaultAudioEndpoint(eCapture, eConsole, &device);
        }
        if (FAILED(hrDev) || !device ||
            FAILED(
                device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(client.
                    GetAddressOf()))) ||
            FAILED(client->GetMixFormat(&mixFmt)) || !mixFmt)
        {
            OSM_LOG_WARN("Client microphone unavailable; virtual mic uplink idle");
            cleanup();
            return;
        }

        if (FAILED(client->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, 200000, 0, mixFmt, nullptr)) ||
            FAILED(client->GetService(IID_PPV_ARGS(&capture))))
        {
            OSM_LOG_WARN("Failed to initialize WASAPI microphone capture");
            cleanup();
            return;
        }

        int opusErr = OPUS_OK;
        opus = opus_encoder_create(48000, 2, OPUS_APPLICATION_VOIP, &opusErr);
        if (!opus || opusErr != OPUS_OK)
        {
            cleanup();
            return;
        }
        opus_encoder_ctl(opus, OPUS_SET_BITRATE(64000));

        AVChannelLayout outLayout{}, inLayout{};
        av_channel_layout_default(&outLayout, 2);
        av_channel_layout_default(&inLayout, mixFmt->nChannels);

        AVSampleFormat inFmt = AV_SAMPLE_FMT_FLT;
        if (mixFmt->wFormatTag == WAVE_FORMAT_PCM ||
            (mixFmt->wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
                reinterpret_cast<WAVEFORMATEXTENSIBLE*>(mixFmt)->SubFormat == KSDATAFORMAT_SUBTYPE_PCM))
        {
            inFmt = (mixFmt->wBitsPerSample == 16) ? AV_SAMPLE_FMT_S16 : AV_SAMPLE_FMT_S32;
        }

        if (swr_alloc_set_opts2(&swr, &outLayout, AV_SAMPLE_FMT_FLT, 48000,
                                &inLayout, inFmt, static_cast<int>(mixFmt->nSamplesPerSec), 0, nullptr) < 0 ||
            swr_init(swr) < 0)
        {
            cleanup();
            return;
        }

        if (FAILED(client->Start()))
        {
            cleanup();
            return;
        }

        m_Running.store(true);
        OSM_LOG_INFO("Client WASAPI microphone capture started (%u Hz, %u ch -> 48kHz Opus)",
                     mixFmt->nSamplesPerSec, mixFmt->nChannels);

        constexpr int kFrameSamples = 480;
        std::vector<float> fifo;
        fifo.reserve(4800);
        std::vector<float> resampled(4096 * 2);
        uint8_t opusBuf[1500]{};

        while (!m_StopFlag.load())
        {
            UINT32 pktLen = 0;
            if (FAILED(capture->GetNextPacketSize(&pktLen))) break;
            if (pktLen == 0)
            {
                Sleep(3);
                continue;
            }

            while (pktLen > 0 && !m_StopFlag.load())
            {
                BYTE* pData = nullptr;
                UINT32 numFrames = 0;
                DWORD flags = 0;
                if (FAILED(capture->GetBuffer(&pData, &numFrames, &flags, nullptr, nullptr))) break;

                if (numFrames > 0)
                {
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) || m_Muted.load())
                    {
                        size_t outCount = static_cast<size_t>((uint64_t(numFrames) * 48000ULL) / mixFmt->
                            nSamplesPerSec);
                        fifo.insert(fifo.end(), outCount * 2, 0.0f);
                    }
                    else
                    {
                        const uint8_t* inPtr[1] = {pData};
                        int maxOut = static_cast<int>(swr_get_out_samples(swr, static_cast<int>(numFrames))) + 64;
                        if (static_cast<int>(resampled.size()) < maxOut * 2)
                        {
                            resampled.resize(static_cast<size_t>(maxOut) * 2);
                        }
                        uint8_t* outPtr[1] = {reinterpret_cast<uint8_t*>(resampled.data())};
                        int converted = swr_convert(swr, outPtr, maxOut, inPtr, static_cast<int>(numFrames));
                        if (converted > 0)
                        {
                            fifo.insert(fifo.end(), resampled.data(),
                                        resampled.data() + static_cast<size_t>(converted) * 2);
                        }
                    }
                }

                capture->ReleaseBuffer(numFrames);

                while (fifo.size() >= static_cast<size_t>(kFrameSamples) * 2)
                {
                    int encoded = opus_encode_float(opus, fifo.data(), kFrameSamples, opusBuf, sizeof(opusBuf));
                    if (encoded > 0 && m_Callback)
                    {
                        m_Callback(opusBuf, encoded, kFrameSamples);
                    }
                    fifo.erase(fifo.begin(), fifo.begin() + static_cast<ptrdiff_t>(kFrameSamples) * 2);
                }

                if (FAILED(capture->GetNextPacketSize(&pktLen))) break;
            }
        }

        cleanup();
    }
}
