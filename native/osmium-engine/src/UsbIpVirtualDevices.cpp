#include "UsbIpVirtualDevices.h"
#include "DriverSetupHelper.h"

#include <cfgmgr32.h>
#include <devioctl.h>
#include <initguid.h>
#include <mmdeviceapi.h>
#include <functiondiscoverykeys_devpkey.h>
#include <audioclient.h>
#include <objbase.h>
#include <wrl/client.h>

#include <algorithm>
#include <chrono>
#include <cstring>
#include <map>

namespace osmium
{
    using Microsoft::WRL::ComPtr;

    namespace
    {
        constexpr wchar_t kUsbIpUdeHwId[] = L"ROOT\\USBIP_WIN2\\UDE";

        DEFINE_GUID(kGuidDevInterfaceUsbIpVhci,
                    0xB4030C06, 0xDC5F, 0x4FCC, 0x87, 0xEB, 0xE5, 0x51, 0x5A, 0x09, 0x35, 0xC0);

        constexpr CLSID kClsidPolicyConfigClient = {
            0x870af99c, 0x171d, 0x4f9e, {0xaf, 0x0d, 0xe6, 0x3d, 0xf4, 0x0c, 0x2b, 0xc9}
        };
        constexpr IID kIidPolicyConfig = {0xf8679f50, 0x850a, 0x41cf, {0x9c, 0x72, 0x43, 0x0f, 0x29, 0x02, 0x90, 0xc8}};

        enum ERoleShim { eConsoleRole = 0, eMultimediaRole = 1, eCommunicationsRole = 2 };

        interface IPolicyConfigShim : public IUnknown
        {
            virtual HRESULT STDMETHODCALLTYPE GetMixFormat(PCWSTR, void**) = 0;
            virtual HRESULT STDMETHODCALLTYPE GetDeviceFormat(PCWSTR, INT, void**) = 0;
            virtual HRESULT STDMETHODCALLTYPE ResetDeviceFormat(PCWSTR) = 0;
            virtual HRESULT STDMETHODCALLTYPE SetDeviceFormat(PCWSTR, void*, void*) = 0;
            virtual HRESULT STDMETHODCALLTYPE GetProcessingPeriod(PCWSTR, INT, PINT64, PINT64) = 0;
            virtual HRESULT STDMETHODCALLTYPE SetProcessingPeriod(PCWSTR, PINT64) = 0;
            virtual HRESULT STDMETHODCALLTYPE GetShareMode(PCWSTR, void*) = 0;
            virtual HRESULT STDMETHODCALLTYPE SetShareMode(PCWSTR, void*) = 0;
            virtual HRESULT STDMETHODCALLTYPE GetPropertyValue(PCWSTR, const PROPERTYKEY&, PROPVARIANT*) = 0;
            virtual HRESULT STDMETHODCALLTYPE SetPropertyValue(PCWSTR, const PROPERTYKEY&, PROPVARIANT*) = 0;
            virtual HRESULT STDMETHODCALLTYPE SetDefaultEndpoint(PCWSTR wszDeviceId, ERoleShim role) = 0;
            virtual HRESULT STDMETHODCALLTYPE SetEndpointVisibility(PCWSTR, INT) = 0;
        };

        constexpr ULONG kIoctlPluginHardwareOnce = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x806, METHOD_BUFFERED,
                                                            FILE_READ_DATA | FILE_WRITE_DATA);
        constexpr ULONG kIoctlPlugoutHardware = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_BUFFERED,
                                                         FILE_READ_DATA | FILE_WRITE_DATA);

#pragma pack(push, 8)
        struct UsbipVhciLocation
        {
            char busid[32];
            char service[32];
            char host[1025];
        };

        struct UsbipVhciDeviceConfig
        {
            UsbipVhciLocation location;
            char serial[16];
            bool wsk_events;
        };

        struct UsbipIoctlPluginHardware
        {
            ULONG size;
            int port;
            ULONG location_hash;
            UsbipVhciDeviceConfig config;
        };

        struct UsbipIoctlPlugoutHardware
        {
            ULONG size;
            int port;
        };
#pragma pack(pop)

        constexpr uint16_t kUsbipVersion = 0x0111;
        constexpr uint16_t kOpReqDevlist = 0x8005;
        constexpr uint16_t kOpRepDevlist = 0x0005;
        constexpr uint16_t kOpReqImport = 0x8003;
        constexpr uint16_t kOpRepImport = 0x0003;

        constexpr uint32_t kCmdSubmit = 1;
        constexpr uint32_t kCmdUnlink = 2;
        constexpr uint32_t kRetSubmit = 3;
        constexpr uint32_t kRetUnlink = 4;

        constexpr uint32_t kDirOut = 0;
        constexpr uint32_t kDirIn = 1;

        constexpr uint32_t kUsbSpeedLow = 1;
        constexpr uint32_t kUsbSpeedFull = 2;

#pragma pack(push, 1)
        struct UsbipOpCommon
        {
            uint16_t version;
            uint16_t code;
            uint32_t status;
        };

        struct UsbipUsbDeviceWire
        {
            char path[256];
            char busid[32];
            uint32_t busnum;
            uint32_t devnum;
            uint32_t speed;
            uint16_t idVendor;
            uint16_t idProduct;
            uint16_t bcdDevice;
            uint8_t bDeviceClass;
            uint8_t bDeviceSubClass;
            uint8_t bDeviceProtocol;
            uint8_t bConfigurationValue;
            uint8_t bNumConfigurations;
            uint8_t bNumInterfaces;
        };

        struct UsbipInterfaceWire
        {
            uint8_t bInterfaceClass;
            uint8_t bInterfaceSubClass;
            uint8_t bInterfaceProtocol;
            uint8_t padding;
        };

        struct UsbipHeaderWire
        {
            uint32_t command;
            uint32_t seqnum;
            uint32_t devid;
            uint32_t direction;
            uint32_t ep;

            union
            {
                struct
                {
                    uint32_t transfer_flags;
                    int32_t transfer_buffer_length;
                    int32_t start_frame;
                    int32_t number_of_packets;
                    int32_t interval;
                    uint8_t setup[8];
                } cmd_submit;

                struct
                {
                    int32_t status;
                    int32_t actual_length;
                    int32_t start_frame;
                    int32_t number_of_packets;
                    int32_t error_count;
                } ret_submit;

                struct
                {
                    uint32_t unlink_seqnum;
                    uint8_t pad[24];
                } cmd_unlink;

                struct
                {
                    int32_t status;
                    uint8_t pad[24];
                } ret_unlink;
            };
        };

        struct UsbipIsoPacketWire
        {
            uint32_t offset;
            uint32_t length;
            uint32_t actual_length;
            uint32_t status;
        };
#pragma pack(pop)

        inline uint16_t Swap16(uint16_t v) { return _byteswap_ushort(v); }
        inline uint32_t Swap32(uint32_t v) { return _byteswap_ulong(v); }

        bool RecvExact(SOCKET s, void* buf, size_t len)
        {
            auto* p = static_cast<char*>(buf);
            size_t got = 0;
            while (got < len)
            {
                int r = recv(s, p + got, static_cast<int>(len - got), 0);
                if (r <= 0) return false;
                got += static_cast<size_t>(r);
            }
            return true;
        }

        bool SendExact(SOCKET s, const void* buf, size_t len)
        {
            const auto* p = static_cast<const char*>(buf);
            size_t sent = 0;
            while (sent < len)
            {
                int r = send(s, p + sent, static_cast<int>(len - sent), 0);
                if (r <= 0) return false;
                sent += static_cast<size_t>(r);
            }
            return true;
        }

        std::vector<uint8_t> MakeUsbStringDesc(const std::u16string& text)
        {
            std::vector<uint8_t> out;
            size_t byteLen = 2 + text.size() * 2;
            if (byteLen > 254) byteLen = 254;
            out.resize(byteLen);
            out[0] = static_cast<uint8_t>(byteLen);
            out[1] = 0x03;
            size_t chars = (byteLen - 2) / 2;
            for (size_t i = 0; i < chars; ++i)
            {
                uint16_t ch = static_cast<uint16_t>(text[i]);
                out[2 + i * 2] = static_cast<uint8_t>(ch & 0xFF);
                out[3 + i * 2] = static_cast<uint8_t>((ch >> 8) & 0xFF);
            }
            return out;
        }

        HANDLE OpenVhciHandle()
        {
            ULONG cch = 0;
            GUID guid = kGuidDevInterfaceUsbIpVhci;
            if (CM_Get_Device_Interface_List_Size(&cch, &guid, nullptr, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) !=
                CR_SUCCESS || cch <= 1)
            {
                return INVALID_HANDLE_VALUE;
            }
            std::wstring multi(cch, L'\0');
            if (CM_Get_Device_Interface_List(&guid, nullptr, multi.data(), cch, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) !=
                CR_SUCCESS || multi[0] == L'\0')
            {
                return INVALID_HANDLE_VALUE;
            }
            return CreateFileW(multi.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        }

        class PcmRingBuffer
        {
        public:
            explicit PcmRingBuffer(size_t capacityBytes) : m_Buf(capacityBytes, 0)
            {
            }

            void Reset()
            {
                std::lock_guard<std::mutex> lock(m_Mutex);
                m_Head = m_Tail = m_Count = 0;
            }

            void Write(const uint8_t* data, size_t len)
            {
                if (!data || len == 0 || m_Buf.empty()) return;
                std::lock_guard<std::mutex> lock(m_Mutex);
                if (len >= m_Buf.size())
                {
                    data += (len - m_Buf.size());
                    len = m_Buf.size();
                    m_Head = m_Tail = m_Count = 0;
                }
                size_t freeSpace = m_Buf.size() - m_Count;
                if (len > freeSpace)
                {
                    size_t drop = len - freeSpace;
                    m_Tail = (m_Tail + drop) % m_Buf.size();
                    m_Count -= drop;
                }
                size_t first = std::min(len, m_Buf.size() - m_Head);
                memcpy(m_Buf.data() + m_Head, data, first);
                if (len > first)
                {
                    memcpy(m_Buf.data(), data + first, len - first);
                }
                m_Head = (m_Head + len) % m_Buf.size();
                m_Count += len;
            }

            size_t Read(uint8_t* outData, size_t len, bool fillSilenceIfEmpty)
            {
                if (!outData || len == 0) return 0;
                std::lock_guard<std::mutex> lock(m_Mutex);
                size_t toRead = std::min(len, m_Count);
                if (toRead > 0)
                {
                    size_t first = std::min(toRead, m_Buf.size() - m_Tail);
                    memcpy(outData, m_Buf.data() + m_Tail, first);
                    if (toRead > first)
                    {
                        memcpy(outData + first, m_Buf.data(), toRead - first);
                    }
                    m_Tail = (m_Tail + toRead) % m_Buf.size();
                    m_Count -= toRead;
                }
                if (toRead < len && fillSilenceIfEmpty)
                {
                    memset(outData + toRead, 0, len - toRead);
                }
                return toRead;
            }

        private:
            std::mutex m_Mutex;
            std::vector<uint8_t> m_Buf;
            size_t m_Head = 0;
            size_t m_Tail = 0;
            size_t m_Count = 0;
        };
    }

    class UsbIpVirtualDevices::VirtualDeviceBase
    {
    public:
        virtual ~VirtualDeviceBase() = default;
        virtual const char* BusId() const = 0;
        virtual uint32_t DevNum() const = 0;
        virtual uint32_t Speed() const = 0;
        virtual uint16_t Vid() const = 0;
        virtual uint16_t Pid() const = 0;
        virtual uint8_t NumInterfaces() const = 0;
        virtual UsbipInterfaceWire GetInterfaceSummary(uint8_t idx) const = 0;
        virtual void ServeConnection(SOCKET sock, std::atomic<bool>& serverStop) = 0;

        void FillWireDevice(UsbipUsbDeviceWire& u) const
        {
            memset(&u, 0, sizeof(u));
            snprintf(u.path, sizeof(u.path), "/sys/devices/platform/osmium/%s", BusId());
            snprintf(u.busid, sizeof(u.busid), "%s", BusId());
            u.busnum = Swap32(1);
            u.devnum = Swap32(DevNum());
            u.speed = Swap32(Speed());
            u.idVendor = Swap16(Vid());
            u.idProduct = Swap16(Pid());
            u.bcdDevice = Swap16(0x0100);
            u.bDeviceClass = 0;
            u.bDeviceSubClass = 0;
            u.bDeviceProtocol = 0;
            u.bConfigurationValue = 1;
            u.bNumConfigurations = 1;
            u.bNumInterfaces = NumInterfaces();
        }
    };

    class UsbIpVirtualDevices::HidMouseDevice final : public VirtualDeviceBase
    {
    public:
        HidMouseDevice()
        {
            m_HidReportDesc = {
                0x05, 0x01,
                0x09, 0x02,
                0xA1, 0x01,
                0x09, 0x01,
                0xA1, 0x00,
                0x05, 0x09,
                0x19, 0x01,
                0x29, 0x05,
                0x15, 0x00,
                0x25, 0x01,
                0x95, 0x05,
                0x75, 0x01,
                0x81, 0x02,
                0x95, 0x01,
                0x75, 0x03,
                0x81, 0x03,
                0x05, 0x01,
                0x09, 0x30,
                0x09, 0x31,
                0x16, 0x01, 0x80,
                0x26, 0xFF, 0x7F,
                0x75, 0x10,
                0x95, 0x02,
                0x81, 0x06,
                0x09, 0x38,
                0x15, 0x81,
                0x25, 0x7F,
                0x75, 0x08,
                0x95, 0x01,
                0x81, 0x06,
                0x05, 0x0C,
                0x0A, 0x38, 0x02,
                0x15, 0x81,
                0x25, 0x7F,
                0x75, 0x08,
                0x95, 0x01,
                0x81, 0x06,
                0xC0,
                0xC0
            };

            const uint16_t hidLen = static_cast<uint16_t>(m_HidReportDesc.size());
            m_DeviceDesc = {
                18, 0x01, 0x00, 0x02,
                0x00, 0x00, 0x00, 64,
                0xED, 0xFE, 0x01, 0x10,
                0x00, 0x01, 1, 2, 3, 1
            };

            m_ConfigDesc = {
                9, 0x02, 34, 0, 1, 1, 0, 0xA0, 50,
                9, 0x04, 0, 0, 1, 0x03, 0x01, 0x02, 0,
                9, 0x21, 0x11, 0x01, 0x00, 1, 0x22,
                static_cast<uint8_t>(hidLen & 0xFF), static_cast<uint8_t>((hidLen >> 8) & 0xFF),
                7, 0x05, 0x81, 0x03, 16, 0, 1
            };

            m_Str0 = {4, 0x03, 0x09, 0x04};
            m_Str1 = MakeUsbStringDesc(u"Osmium Virtual Hardware");
            m_Str2 = MakeUsbStringDesc(u"Osmium Virtual HID Mouse");
            m_Str3 = MakeUsbStringDesc(u"OSMMOUSE001");
        }

        const char* BusId() const override { return "1-1"; }
        uint32_t DevNum() const override { return 1; }
        uint32_t Speed() const override { return kUsbSpeedFull; }
        uint16_t Vid() const override { return 0xFEED; }
        uint16_t Pid() const override { return 0x1001; }
        uint8_t NumInterfaces() const override { return 1; }

        UsbipInterfaceWire GetInterfaceSummary(uint8_t) const override
        {
            return {0x03, 0x01, 0x02, 0};
        }

        void EnqueueReport(const VirtualMouseReport& rpt)
        {
            std::lock_guard<std::mutex> lock(m_QueueMutex);
            if (m_Reports.size() > 128) m_Reports.pop_front();
            m_Reports.push_back(rpt);
            m_QueueCv.notify_all();
        }

        void SetReadyFlag(std::atomic<bool>* flag) { m_ReadyFlag = flag; }

        void ServeConnection(SOCKET sock, std::atomic<bool>& serverStop) override
        {
            if (m_ReadyFlag) m_ReadyFlag->store(false);
            std::mutex writeMutex;
            std::atomic<bool> connStop{false};
            std::mutex pendingMutex;
            std::deque<UsbipHeaderWire> pendingIntIn;

            auto sendReply = [&](const UsbipHeaderWire& req, int32_t status, const uint8_t* payload,
                                 uint32_t payloadLen) -> bool
            {
                UsbipHeaderWire rep{};
                rep.command = Swap32(kRetSubmit);
                rep.seqnum = Swap32(req.seqnum);
                rep.devid = Swap32(req.devid);
                rep.direction = Swap32(req.direction);
                rep.ep = Swap32(req.ep);
                rep.ret_submit.status = Swap32(static_cast<uint32_t>(status));
                rep.ret_submit.actual_length = Swap32(payloadLen);
                rep.ret_submit.start_frame = Swap32(0);
                rep.ret_submit.number_of_packets = Swap32(0xFFFFFFFFU);
                rep.ret_submit.error_count = Swap32(0);

                std::lock_guard<std::mutex> wl(writeMutex);
                if (!SendExact(sock, &rep, sizeof(rep))) return false;
                if (payloadLen > 0 && payload && !SendExact(sock, payload, payloadLen)) return false;
                return true;
            };

            std::thread intWorker([&]()
            {
                while (!serverStop.load() && !connStop.load())
                {
                    VirtualMouseReport rpt{};
                    UsbipHeaderWire req{};
                    bool haveBoth = false;
                    {
                        std::unique_lock<std::mutex> ql(m_QueueMutex);
                        m_QueueCv.wait_for(ql, std::chrono::milliseconds(20), [&]()
                        {
                            std::lock_guard<std::mutex> pl(pendingMutex);
                            return serverStop.load() || connStop.load() || (!m_Reports.empty() && !pendingIntIn.
                                empty());
                        });
                        if (serverStop.load() || connStop.load()) break;
                        std::lock_guard<std::mutex> pl(pendingMutex);
                        if (!m_Reports.empty() && !pendingIntIn.empty())
                        {
                            rpt = m_Reports.front();
                            m_Reports.pop_front();
                            req = pendingIntIn.front();
                            pendingIntIn.pop_front();
                            haveBoth = true;
                        }
                    }
                    if (haveBoth)
                    {
                        uint8_t wireRpt[7]{};
                        wireRpt[0] = rpt.buttons;
                        memcpy(wireRpt + 1, &rpt.dx, 2);
                        memcpy(wireRpt + 3, &rpt.dy, 2);
                        wireRpt[5] = static_cast<uint8_t>(rpt.wheel);
                        wireRpt[6] = static_cast<uint8_t>(rpt.hwheel);
                        uint32_t len = std::min<uint32_t>(sizeof(wireRpt),
                                                          static_cast<uint32_t>(std::max(
                                                              0, req.cmd_submit.transfer_buffer_length)));
                        if (!sendReply(req, 0, wireRpt, len))
                        {
                            connStop.store(true);
                            break;
                        }
                    }
                }
            });

            while (!serverStop.load() && !connStop.load())
            {
                UsbipHeaderWire hdr{};
                if (!RecvExact(sock, &hdr, sizeof(hdr))) break;

                hdr.command = Swap32(hdr.command);
                hdr.seqnum = Swap32(hdr.seqnum);
                hdr.devid = Swap32(hdr.devid);
                hdr.direction = Swap32(hdr.direction);
                hdr.ep = Swap32(hdr.ep);

                if (hdr.command == kCmdSubmit)
                {
                    hdr.cmd_submit.transfer_flags = Swap32(hdr.cmd_submit.transfer_flags);
                    hdr.cmd_submit.transfer_buffer_length = static_cast<int32_t>(Swap32(
                        static_cast<uint32_t>(hdr.cmd_submit.transfer_buffer_length)));
                    hdr.cmd_submit.start_frame = static_cast<int32_t>(Swap32(
                        static_cast<uint32_t>(hdr.cmd_submit.start_frame)));
                    hdr.cmd_submit.number_of_packets = static_cast<int32_t>(Swap32(
                        static_cast<uint32_t>(hdr.cmd_submit.number_of_packets)));
                    hdr.cmd_submit.interval = static_cast<int32_t>(Swap32(
                        static_cast<uint32_t>(hdr.cmd_submit.interval)));

                    std::vector<uint8_t> outPayload;
                    if (hdr.direction == kDirOut && hdr.cmd_submit.transfer_buffer_length > 0)
                    {
                        outPayload.resize(static_cast<size_t>(hdr.cmd_submit.transfer_buffer_length));
                        if (!RecvExact(sock, outPayload.data(), outPayload.size())) break;
                    }

                    if (hdr.ep == 0)
                    {
                        const uint8_t reqType = hdr.cmd_submit.setup[0];
                        const uint8_t req = hdr.cmd_submit.setup[1];
                        const uint16_t wValue = static_cast<uint16_t>(hdr.cmd_submit.setup[2] | (hdr.cmd_submit.setup[3]
                            << 8));
                        const uint16_t wLength = static_cast<uint16_t>(hdr.cmd_submit.setup[6] | (hdr.cmd_submit.setup[
                            7] << 8));

                        std::vector<uint8_t> resp;
                        int32_t status = 0;

                        if ((reqType & 0x60) == 0x00 && req == 0x06)
                        {
                            uint8_t descType = static_cast<uint8_t>(wValue >> 8);
                            uint8_t descIdx = static_cast<uint8_t>(wValue & 0xFF);
                            if (descType == 0x01) resp = m_DeviceDesc;
                            else if (descType == 0x02) resp = m_ConfigDesc;
                            else if (descType == 0x03)
                            {
                                if (descIdx == 0) resp = m_Str0;
                                else if (descIdx == 1) resp = m_Str1;
                                else if (descIdx == 2) resp = m_Str2;
                                else if (descIdx == 3) resp = m_Str3;
                                else status = -32;
                            }
                            else if (descType == 0x22) resp = m_HidReportDesc;
                            else if (descType == 0x21 && m_ConfigDesc.size() >= 27)
                            {
                                resp.assign(m_ConfigDesc.begin() + 18, m_ConfigDesc.begin() + 27);
                            }
                            else
                            {
                                status = -32;
                            }
                        }
                        else if (reqType == 0x80 && req == 0x08)
                        {
                            resp = {1};
                        }
                        else if (reqType == 0x80 && req == 0x00)
                        {
                            resp = {0, 0};
                        }
                        else if (reqType == 0x81 && req == 0x0A)
                        {
                            resp = {0};
                        }
                        else if (reqType == 0xA1 && req == 0x01)
                        {
                            resp.assign(7, 0);
                        }

                        if (resp.size() > wLength) resp.resize(wLength);
                        if (hdr.direction == kDirOut)
                        {
                            sendReply(hdr, status, nullptr, static_cast<uint32_t>(outPayload.size()));
                        }
                        else
                        {
                            sendReply(hdr, status, resp.empty() ? nullptr : resp.data(),
                                      static_cast<uint32_t>(resp.size()));
                        }
                    }
                    else if (hdr.ep == 1 && hdr.direction == kDirIn)
                    {
                        if (m_ReadyFlag) m_ReadyFlag->store(true);
                        {
                            std::lock_guard<std::mutex> pl(pendingMutex);
                            pendingIntIn.push_back(hdr);
                        }
                        m_QueueCv.notify_all();
                    }
                    else
                    {
                        sendReply(hdr, -32, nullptr, 0);
                    }
                }
                else if (hdr.command == kCmdUnlink)
                {
                    uint32_t targetSeq = Swap32(hdr.cmd_unlink.unlink_seqnum);
                    bool found = false;
                    {
                        std::lock_guard<std::mutex> pl(pendingMutex);
                        for (auto it = pendingIntIn.begin(); it != pendingIntIn.end(); ++it)
                        {
                            if (it->seqnum == targetSeq)
                            {
                                pendingIntIn.erase(it);
                                found = true;
                                break;
                            }
                        }
                    }
                    UsbipHeaderWire rep{};
                    rep.command = Swap32(kRetUnlink);
                    rep.seqnum = Swap32(hdr.seqnum);
                    rep.ret_unlink.status = Swap32(static_cast<uint32_t>(found ? -104 : 0));
                    std::lock_guard<std::mutex> wl(writeMutex);
                    if (!SendExact(sock, &rep, sizeof(rep))) break;
                }
                else
                {
                    break;
                }
            }

            if (m_ReadyFlag) m_ReadyFlag->store(false);
            connStop.store(true);
            m_QueueCv.notify_all();
            if (intWorker.joinable()) intWorker.join();
        }

    private:
        std::vector<uint8_t> m_DeviceDesc, m_ConfigDesc, m_HidReportDesc, m_Str0, m_Str1, m_Str2, m_Str3;
        std::atomic<bool>* m_ReadyFlag = nullptr;
        std::mutex m_QueueMutex;
        std::condition_variable m_QueueCv;
        std::deque<VirtualMouseReport> m_Reports;
    };

    class UsbIpVirtualDevices::Uac1AudioBase : public UsbIpVirtualDevices::VirtualDeviceBase
    {
    public:
        Uac1AudioBase(bool isMic, const std::u16string& productName, const char* busId, uint32_t devNum, uint16_t pid)
            : m_IsMic(isMic), m_BusId(busId), m_DevNum(devNum), m_Pid(pid), m_Ring(48000 * 4 * 200 / 1000)
        {
            m_DeviceDesc = {
                18, 0x01, 0x10, 0x01,
                0x00, 0x00, 0x00, 64,
                0xFF, 0xFF, static_cast<uint8_t>(pid & 0xFF), static_cast<uint8_t>(pid >> 8),
                0x01, 0x00, 1, 2, 3, 1
            };

            if (m_IsMic)
            {
                m_ConfigDesc = {
                    9, 0x02, 0, 0, 2, 1, 0, 0x80, 50,
                    9, 0x04, 0, 0, 0, 0x01, 0x01, 0x00, 0,
                    9, 0x24, 0x01, 0x00, 0x01, 40, 0, 1, 1,
                    12, 0x24, 0x02, 1, 0x01, 0x02, 0, 2, 0x03, 0x00, 0, 0,
                    10, 0x24, 0x06, 2, 1, 1, 0x03, 0x00, 0x00, 0,
                    9, 0x24, 0x03, 3, 0x01, 0x01, 0, 2, 0,
                    9, 0x04, 1, 0, 0, 0x01, 0x02, 0x00, 0,
                    9, 0x04, 1, 1, 1, 0x01, 0x02, 0x00, 0,
                    7, 0x24, 0x01, 3, 1, 0x01, 0x00,
                    11, 0x24, 0x02, 1, 2, 2, 16, 1, 0x80, 0xBB, 0x00,
                    9, 0x05, 0x82, 0x0D, 0xC0, 0x00, 1, 0, 0,
                    7, 0x25, 0x01, 0, 0, 0, 0
                };
            }
            else
            {
                m_ConfigDesc = {
                    9, 0x02, 0, 0, 2, 1, 0, 0x80, 50,
                    9, 0x04, 0, 0, 0, 0x01, 0x01, 0x00, 0,
                    9, 0x24, 0x01, 0x00, 0x01, 40, 0, 1, 1,
                    12, 0x24, 0x02, 1, 0x01, 0x01, 0, 2, 0x03, 0x00, 0, 0,
                    10, 0x24, 0x06, 2, 1, 1, 0x03, 0x00, 0x00, 0,
                    9, 0x24, 0x03, 3, 0x01, 0x03, 0, 2, 0,
                    9, 0x04, 1, 0, 0, 0x01, 0x02, 0x00, 0,
                    9, 0x04, 1, 1, 1, 0x01, 0x02, 0x00, 0,
                    7, 0x24, 0x01, 1, 1, 0x01, 0x00,
                    11, 0x24, 0x02, 1, 2, 2, 16, 1, 0x80, 0xBB, 0x00,
                    9, 0x05, 0x01, 0x09, 0xC0, 0x00, 1, 0, 0,
                    7, 0x25, 0x01, 0, 0, 0, 0
                };
            }

            uint16_t tot = static_cast<uint16_t>(m_ConfigDesc.size());
            m_ConfigDesc[2] = static_cast<uint8_t>(tot & 0xFF);
            m_ConfigDesc[3] = static_cast<uint8_t>((tot >> 8) & 0xFF);

            m_Str0 = {4, 0x03, 0x09, 0x04};
            m_Str1 = MakeUsbStringDesc(u"Osmium Virtual Audio");
            m_Str2 = MakeUsbStringDesc(productName);
            m_Str3 = MakeUsbStringDesc(m_IsMic ? u"OSMMIC001" : u"OSMSPK001");
        }

        const char* BusId() const override { return m_BusId.c_str(); }
        uint32_t DevNum() const override { return m_DevNum; }
        uint32_t Speed() const override { return kUsbSpeedFull; }
        uint16_t Vid() const override { return 0xFFFF; }
        uint16_t Pid() const override { return m_Pid; }
        uint8_t NumInterfaces() const override { return 2; }

        UsbipInterfaceWire GetInterfaceSummary(uint8_t idx) const override
        {
            if (idx == 0) return {0x01, 0x01, 0x00, 0};
            return {0x01, 0x02, 0x00, 0};
        }

        void PushPcm(const int16_t* samples, size_t frameCount)
        {
            if (!samples || frameCount == 0) return;
            m_Ring.Write(reinterpret_cast<const uint8_t*>(samples), frameCount * 4);
        }

        size_t PopPcm(int16_t* outSamples, size_t maxFrames)
        {
            if (!outSamples || maxFrames == 0) return 0;
            size_t gotBytes = m_Ring.Read(reinterpret_cast<uint8_t*>(outSamples), maxFrames * 4, false);
            return gotBytes / 4;
        }

        void ServeConnection(SOCKET sock, std::atomic<bool>& serverStop) override
        {
            std::mutex writeMutex;
            std::atomic<bool> connStop{false};
            std::atomic<uint8_t> altSetting{0};
            uint32_t sampleRate = 48000;
            bool muted = false;
            int16_t volume = 0;

            auto nextIsoDeadline = std::chrono::steady_clock::now();

            auto sendReply = [&](const UsbipHeaderWire& req, int32_t status, uint32_t actualLen,
                                 const uint8_t* payload, const std::vector<UsbipIsoPacketWire>& isoPkts) -> bool
            {
                UsbipHeaderWire rep{};
                rep.command = Swap32(kRetSubmit);
                rep.seqnum = Swap32(req.seqnum);
                rep.devid = Swap32(req.devid);
                rep.direction = Swap32(req.direction);
                rep.ep = Swap32(req.ep);
                rep.ret_submit.status = Swap32(static_cast<uint32_t>(status));
                rep.ret_submit.actual_length = Swap32(actualLen);
                rep.ret_submit.start_frame = Swap32(req.cmd_submit.start_frame);
                rep.ret_submit.number_of_packets = Swap32(static_cast<uint32_t>(req.cmd_submit.number_of_packets));
                rep.ret_submit.error_count = Swap32(0);

                std::lock_guard<std::mutex> wl(writeMutex);
                if (!SendExact(sock, &rep, sizeof(rep))) return false;
                if (payload && actualLen > 0 && !SendExact(sock, payload, actualLen)) return false;
                for (const auto& p : isoPkts)
                {
                    UsbipIsoPacketWire wp{};
                    wp.offset = Swap32(p.offset);
                    wp.length = Swap32(p.length);
                    wp.actual_length = Swap32(p.actual_length);
                    wp.status = Swap32(p.status);
                    if (!SendExact(sock, &wp, sizeof(wp))) return false;
                }
                return true;
            };

            while (!serverStop.load() && !connStop.load())
            {
                UsbipHeaderWire hdr{};
                if (!RecvExact(sock, &hdr, sizeof(hdr))) break;

                hdr.command = Swap32(hdr.command);
                hdr.seqnum = Swap32(hdr.seqnum);
                hdr.devid = Swap32(hdr.devid);
                hdr.direction = Swap32(hdr.direction);
                hdr.ep = Swap32(hdr.ep);

                if (hdr.command == kCmdSubmit)
                {
                    hdr.cmd_submit.transfer_flags = Swap32(hdr.cmd_submit.transfer_flags);
                    hdr.cmd_submit.transfer_buffer_length = static_cast<int32_t>(Swap32(
                        static_cast<uint32_t>(hdr.cmd_submit.transfer_buffer_length)));
                    hdr.cmd_submit.start_frame = static_cast<int32_t>(Swap32(
                        static_cast<uint32_t>(hdr.cmd_submit.start_frame)));
                    hdr.cmd_submit.number_of_packets = static_cast<int32_t>(Swap32(
                        static_cast<uint32_t>(hdr.cmd_submit.number_of_packets)));
                    hdr.cmd_submit.interval = static_cast<int32_t>(Swap32(
                        static_cast<uint32_t>(hdr.cmd_submit.interval)));

                    std::vector<uint8_t> outBuf;
                    if (hdr.direction == kDirOut && hdr.cmd_submit.transfer_buffer_length > 0)
                    {
                        outBuf.resize(static_cast<size_t>(hdr.cmd_submit.transfer_buffer_length));
                        if (!RecvExact(sock, outBuf.data(), outBuf.size())) break;
                    }

                    std::vector<UsbipIsoPacketWire> isoPkts;
                    if (hdr.cmd_submit.number_of_packets > 0 && hdr.cmd_submit.number_of_packets <= 1024)
                    {
                        isoPkts.resize(static_cast<size_t>(hdr.cmd_submit.number_of_packets));
                        if (!RecvExact(sock, isoPkts.data(), isoPkts.size() * sizeof(UsbipIsoPacketWire))) break;
                        for (auto& p : isoPkts)
                        {
                            p.offset = Swap32(p.offset);
                            p.length = Swap32(p.length);
                            p.actual_length = Swap32(p.actual_length);
                            p.status = Swap32(p.status);
                        }
                    }

                    if (hdr.ep == 0)
                    {
                        const uint8_t reqType = hdr.cmd_submit.setup[0];
                        const uint8_t req = hdr.cmd_submit.setup[1];
                        const uint16_t wValue = static_cast<uint16_t>(hdr.cmd_submit.setup[2] | (hdr.cmd_submit.setup[3]
                            << 8));
                        const uint16_t wIndex = static_cast<uint16_t>(hdr.cmd_submit.setup[4] | (hdr.cmd_submit.setup[5]
                            << 8));
                        const uint16_t wLength = static_cast<uint16_t>(hdr.cmd_submit.setup[6] | (hdr.cmd_submit.setup[
                            7] << 8));

                        std::vector<uint8_t> resp;
                        int32_t status = 0;

                        if ((reqType & 0x60) == 0x00)
                        {
                            switch (req)
                            {
                            case 0x06:
                                {
                                    uint8_t dt = static_cast<uint8_t>(wValue >> 8);
                                    uint8_t di = static_cast<uint8_t>(wValue & 0xFF);
                                    if (dt == 0x01) resp = m_DeviceDesc;
                                    else if (dt == 0x02) resp = m_ConfigDesc;
                                    else if (dt == 0x03)
                                    {
                                        if (di == 0) resp = m_Str0;
                                        else if (di == 1) resp = m_Str1;
                                        else if (di == 2) resp = m_Str2;
                                        else if (di == 3) resp = m_Str3;
                                        else status = -32;
                                    }
                                    else status = -32;
                                    break;
                                }
                            case 0x08: resp = {1};
                                break;
                            case 0x09: altSetting.store(0);
                                m_Ring.Reset();
                                break;
                            case 0x0A: resp = {(wIndex == 1) ? altSetting.load() : uint8_t(0)};
                                break;
                            case 0x0B:
                                if ((wIndex & 0xFF) == 1)
                                {
                                    altSetting.store(static_cast<uint8_t>(wValue & 0xFF));
                                    if (altSetting.load() == 0) m_Ring.Reset();
                                }
                                break;
                            case 0x00:
                            case 0x0C: resp = {0, 0};
                                break;
                            default: break;
                            }
                        }
                        else
                        {
                            uint8_t rec = reqType & 0x1F;
                            uint8_t sel = static_cast<uint8_t>(wValue >> 8);
                            if (rec == 0x02 && sel == 0x01)
                            {
                                if (req == 0x01 && outBuf.size() >= 3)
                                {
                                    sampleRate = outBuf[0] | (outBuf[1] << 8) | (outBuf[2] << 16);
                                }
                                else if (req >= 0x81 && req <= 0x84)
                                {
                                    resp = {
                                        static_cast<uint8_t>(sampleRate & 0xFF),
                                        static_cast<uint8_t>((sampleRate >> 8) & 0xFF),
                                        static_cast<uint8_t>((sampleRate >> 16) & 0xFF)
                                    };
                                }
                            }
                            else if (rec == 0x01)
                            {
                                if (sel == 0x01)
                                {
                                    if (req == 0x01 && !outBuf.empty()) muted = (outBuf[0] != 0);
                                    else if (req == 0x81) resp = {static_cast<uint8_t>(muted ? 1 : 0)};
                                }
                                else if (sel == 0x02)
                                {
                                    if (req == 0x01 && outBuf.size() >= 2)
                                        volume = static_cast<int16_t>(outBuf[0] | (
                                            outBuf[1] << 8));
                                    else if (req == 0x81)
                                        resp = {
                                            static_cast<uint8_t>(volume & 0xFF),
                                            static_cast<uint8_t>((volume >> 8) & 0xFF)
                                        };
                                    else if (req == 0x82)
                                    {
                                        int16_t v = -60 * 256;
                                        resp = {static_cast<uint8_t>(v & 0xFF), static_cast<uint8_t>((v >> 8) & 0xFF)};
                                    }
                                    else if (req == 0x83) resp = {0, 0};
                                    else if (req == 0x84) resp = {0, 1};
                                }
                            }
                        }

                        if (resp.size() > wLength) resp.resize(wLength);
                        if (hdr.direction == kDirOut)
                        {
                            if (!sendReply(hdr, status, static_cast<uint32_t>(outBuf.size()), nullptr, {})) break;
                        }
                        else
                        {
                            if (!sendReply(hdr, status, static_cast<uint32_t>(resp.size()),
                                           resp.empty() ? nullptr : resp.data(), {}))
                                break;
                        }
                    }
                    else
                    {
                        int numMs = std::max(1, static_cast<int>(isoPkts.size()));
                        auto now = std::chrono::steady_clock::now();
                        if (nextIsoDeadline < now - std::chrono::milliseconds(40))
                        {
                            nextIsoDeadline = now;
                        }
                        nextIsoDeadline += std::chrono::milliseconds(numMs);
                        if (nextIsoDeadline > now)
                        {
                            std::this_thread::sleep_until(nextIsoDeadline);
                        }

                        if (!m_IsMic && hdr.ep == 1 && hdr.direction == kDirOut)
                        {
                            if (altSetting.load() == 1 && !muted && !outBuf.empty())
                            {
                                m_Ring.Write(outBuf.data(), outBuf.size());
                            }
                            uint32_t totalLen = static_cast<uint32_t>(outBuf.size());
                            uint32_t rem = totalLen;
                            for (auto& p : isoPkts)
                            {
                                uint32_t act = std::min(p.length, rem);
                                p.actual_length = act;
                                p.status = 0;
                                rem -= act;
                            }
                            if (!sendReply(hdr, 0, totalLen, nullptr, isoPkts)) break;
                        }
                        else if (m_IsMic && hdr.ep == 2 && hdr.direction == kDirIn)
                        {
                            uint32_t totalBytes = 0;
                            for (const auto& p : isoPkts) totalBytes = std::max(totalBytes, p.offset + p.length);
                            if (totalBytes == 0 && hdr.cmd_submit.transfer_buffer_length > 0)
                            {
                                totalBytes = static_cast<uint32_t>(hdr.cmd_submit.transfer_buffer_length);
                            }
                            std::vector<uint8_t> inData(totalBytes, 0);
                            if (altSetting.load() == 1 && !muted && totalBytes > 0)
                            {
                                m_Ring.Read(inData.data(), inData.size(), true);
                            }
                            for (auto& p : isoPkts)
                            {
                                p.actual_length = p.length;
                                p.status = 0;
                            }
                            if (!sendReply(hdr, 0, totalBytes, inData.empty() ? nullptr : inData.data(),
                                           isoPkts))
                                break;
                        }
                        else
                        {
                            if (!sendReply(hdr, -32, 0, nullptr, isoPkts)) break;
                        }
                    }
                }
                else if (hdr.command == kCmdUnlink)
                {
                    UsbipHeaderWire rep{};
                    rep.command = Swap32(kRetUnlink);
                    rep.seqnum = Swap32(hdr.seqnum);
                    rep.ret_unlink.status = 0;
                    std::lock_guard<std::mutex> wl(writeMutex);
                    if (!SendExact(sock, &rep, sizeof(rep))) break;
                }
                else
                {
                    break;
                }
            }
        }

    private:
        bool m_IsMic = false;
        std::string m_BusId;
        uint32_t m_DevNum = 2;
        uint16_t m_Pid = 0xCA01;
        PcmRingBuffer m_Ring;
        std::vector<uint8_t> m_DeviceDesc, m_ConfigDesc, m_Str0, m_Str1, m_Str2, m_Str3;
    };

    class UsbIpVirtualDevices::UacMicDevice final : public Uac1AudioBase
    {
    public:
        UacMicDevice() : Uac1AudioBase(true, u"Osmium Virtual Microphone", "1-2", 2, 0xCA02)
        {
        }
    };

    class UsbIpVirtualDevices::UacSpeakerDevice final : public Uac1AudioBase
    {
    public:
        UacSpeakerDevice() : Uac1AudioBase(false, u"Osmium Virtual Speaker", "1-3", 3, 0xCA03)
        {
        }
    };

    UsbIpVirtualDevices::~UsbIpVirtualDevices()
    {
        StopAll();
    }

    bool UsbIpVirtualDevices::IsDriverAvailable()
    {
        HANDLE h = OpenVhciHandle();
        if (h != INVALID_HANDLE_VALUE)
        {
            CloseHandle(h);
            return true;
        }
        bool running = false;
        if (DriverSetupHelper::IsDeviceInstalled(kUsbIpUdeHwId, &running) && running)
        {
            return true;
        }
        return false;
    }

    bool UsbIpVirtualDevices::EnsureDriverInstalled()
    {
        if (IsDriverAvailable()) return true;

        std::wstring infPath = DriverSetupHelper::GetDriversBaseDir() + L"\\usbip2_ude\\usbip2_ude.inf";
        std::wstring catPath = DriverSetupHelper::GetDriversBaseDir() + L"\\usbip2_ude\\usbip2_ude.cat";
        if (!DriverSetupHelper::InstallRootDriver(infPath, kUsbIpUdeHwId, catPath))
        {
            return false;
        }
        for (int i = 0; i < 20; ++i)
        {
            Sleep(100);
            if (IsDriverAvailable()) return true;
        }
        return IsDriverAvailable();
    }

    bool UsbIpVirtualDevices::UninstallDriver()
    {
        return DriverSetupHelper::UninstallRootDriver(kUsbIpUdeHwId, L"usbip2_ude.inf") && !IsDriverAvailable();
    }

    bool UsbIpVirtualDevices::StartServer()
    {
        if (m_ServerRunning.load()) return true;

        {
            std::lock_guard<std::mutex> dl(m_DeviceMutex);
            if (!m_MouseDev) m_MouseDev = new HidMouseDevice();
            if (!m_MicDev) m_MicDev = new UacMicDevice();
            if (!m_SpeakerDev) m_SpeakerDev = new UacSpeakerDevice();
        }

        m_ListenSock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (m_ListenSock == INVALID_SOCKET) return false;

        sockaddr_in addr{};
        addr.sin_family = AF_INET;
        inet_pton(AF_INET, "127.0.0.1", &addr.sin_addr);
        addr.sin_port = 0;

        if (bind(m_ListenSock, reinterpret_cast<const sockaddr*>(&addr), sizeof(addr)) == SOCKET_ERROR ||
            listen(m_ListenSock, 8) == SOCKET_ERROR)
        {
            closesocket(m_ListenSock);
            m_ListenSock = INVALID_SOCKET;
            return false;
        }

        int len = sizeof(addr);
        getsockname(m_ListenSock, reinterpret_cast<sockaddr*>(&addr), &len);
        m_ListenPort = ntohs(addr.sin_port);
        m_ServerRunning.store(true);
        m_AcceptThread = std::thread(&UsbIpVirtualDevices::AcceptLoop, this);
        OSM_LOG_INFO("UsbIpVirtualDevices server listening on 127.0.0.1:%u", m_ListenPort);
        return true;
    }

    void UsbIpVirtualDevices::StopAll()
    {
        SetVirtualMouseEnabled(false);
        SetVirtualMicEnabled(false);
        SetVirtualSpeakerEnabled(false);
        RestoreDefaultPlaybackDevice();
        DetachAllOsmiumPorts();

        m_ServerRunning.store(false);
        if (m_ListenSock != INVALID_SOCKET)
        {
            closesocket(m_ListenSock);
            m_ListenSock = INVALID_SOCKET;
        }
        if (m_AcceptThread.joinable()) m_AcceptThread.join();

        {
            std::lock_guard<std::mutex> tl(m_ConnThreadsMutex);
            for (auto& t : m_ConnThreads)
            {
                if (t.joinable()) t.join();
            }
            m_ConnThreads.clear();
        }

        std::lock_guard<std::mutex> dl(m_DeviceMutex);
        delete m_MouseDev;
        m_MouseDev = nullptr;
        delete m_MicDev;
        m_MicDev = nullptr;
        delete m_SpeakerDev;
        m_SpeakerDev = nullptr;
    }

    void UsbIpVirtualDevices::AcceptLoop()
    {
        while (m_ServerRunning.load())
        {
            SOCKET c = accept(m_ListenSock, nullptr, nullptr);
            if (c == INVALID_SOCKET)
            {
                if (!m_ServerRunning.load()) break;
                Sleep(10);
                continue;
            }
            int nodelay = 1;
            setsockopt(c, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&nodelay), sizeof(nodelay));

            std::lock_guard<std::mutex> tl(m_ConnThreadsMutex);
            m_ConnThreads.emplace_back(&UsbIpVirtualDevices::HandleClientSocket, this, c);
        }
    }

    void UsbIpVirtualDevices::HandleClientSocket(SOCKET clientSock)
    {
        while (m_ServerRunning.load())
        {
            UsbipOpCommon op{};
            if (!RecvExact(clientSock, &op, sizeof(op))) break;
            op.version = Swap16(op.version);
            op.code = Swap16(op.code);
            op.status = Swap32(op.status);

            if (op.code == kOpReqDevlist)
            {
                uint32_t reserved = 0;
                if (!RecvExact(clientSock, &reserved, 4)) break;

                std::lock_guard<std::mutex> dl(m_DeviceMutex);
                VirtualDeviceBase* devs[] = {m_MouseDev, m_MicDev, m_SpeakerDev};
                std::vector<VirtualDeviceBase*> active;
                for (auto* d : devs) if (d) active.push_back(d);

                UsbipOpCommon rep{Swap16(kUsbipVersion), Swap16(kOpRepDevlist), Swap32(0)};
                uint32_t ndev = Swap32(static_cast<uint32_t>(active.size()));
                SendExact(clientSock, &rep, sizeof(rep));
                SendExact(clientSock, &ndev, 4);
                for (auto* d : active)
                {
                    UsbipUsbDeviceWire wd{};
                    d->FillWireDevice(wd);
                    SendExact(clientSock, &wd, sizeof(wd));
                    for (uint8_t idx = 0; idx < d->NumInterfaces(); ++idx)
                    {
                        auto inf = d->GetInterfaceSummary(idx);
                        SendExact(clientSock, &inf, sizeof(inf));
                    }
                }
            }
            else if (op.code == kOpReqImport)
            {
                char busId[32]{};
                if (!RecvExact(clientSock, busId, sizeof(busId))) break;
                busId[31] = '\0';

                VirtualDeviceBase* chosen = nullptr;
                {
                    std::lock_guard<std::mutex> dl(m_DeviceMutex);
                    if (m_MouseDev && strcmp(busId, m_MouseDev->BusId()) == 0) chosen = m_MouseDev;
                    else if (m_MicDev && strcmp(busId, m_MicDev->BusId()) == 0) chosen = m_MicDev;
                    else if (m_SpeakerDev && strcmp(busId, m_SpeakerDev->BusId()) == 0) chosen = m_SpeakerDev;
                }

                if (!chosen)
                {
                    UsbipOpCommon rep{Swap16(kUsbipVersion), Swap16(kOpRepImport), Swap32(4)};
                    SendExact(clientSock, &rep, sizeof(rep));
                    break;
                }

                UsbipOpCommon rep{Swap16(kUsbipVersion), Swap16(kOpRepImport), Swap32(0)};
                UsbipUsbDeviceWire wd{};
                chosen->FillWireDevice(wd);
                if (!SendExact(clientSock, &rep, sizeof(rep)) || !SendExact(clientSock, &wd, sizeof(wd))) break;

                OSM_LOG_INFO("USB/IP virtual device attached cleanly: %s", chosen->BusId());
                chosen->ServeConnection(clientSock, m_ServerRunning);
                break;
            }
            else
            {
                break;
            }
        }
        closesocket(clientSock);
    }

    bool UsbIpVirtualDevices::AttachBusId(const char* busId, bool lowLatency, int& outPort)
    {
        outPort = 0;
        if (!EnsureDriverInstalled()) return false;
        if (!StartServer()) return false;

        HANDLE vhci = OpenVhciHandle();
        if (vhci == INVALID_HANDLE_VALUE) return false;

        UsbipIoctlPluginHardware req{};
        req.size = sizeof(req);
        snprintf(req.config.location.host, sizeof(req.config.location.host), "127.0.0.1");
        snprintf(req.config.location.service, sizeof(req.config.location.service), "%u", m_ListenPort);
        snprintf(req.config.location.busid, sizeof(req.config.location.busid), "%s", busId);
        req.config.wsk_events = lowLatency;

        DWORD bytesRet = 0;
        constexpr DWORD outLen = offsetof(UsbipIoctlPluginHardware, port) + sizeof(req.port);
        BOOL ok = DeviceIoControl(vhci, kIoctlPluginHardwareOnce, &req, sizeof(req), &req, outLen, &bytesRet, nullptr);
        CloseHandle(vhci);

        if (ok && req.port > 0)
        {
            outPort = req.port;
            return true;
        }
        return false;
    }

    void UsbIpVirtualDevices::DetachPort(int& ioPort)
    {
        if (ioPort <= 0) return;
        HANDLE vhci = OpenVhciHandle();
        if (vhci != INVALID_HANDLE_VALUE)
        {
            UsbipIoctlPlugoutHardware req{};
            req.size = sizeof(req);
            req.port = ioPort;
            DWORD br = 0;
            DeviceIoControl(vhci, kIoctlPlugoutHardware, &req, sizeof(req), nullptr, 0, &br, nullptr);
            CloseHandle(vhci);
        }
        ioPort = 0;
    }

    void UsbIpVirtualDevices::DetachAllOsmiumPorts()
    {
        DetachPort(m_MousePort);
        DetachPort(m_MicPort);
        DetachPort(m_SpeakerPort);
    }

    bool UsbIpVirtualDevices::SetVirtualMouseEnabled(bool enabled)
    {
        if (enabled == m_MouseActive.load()) return true;
        if (!enabled)
        {
            m_MouseHidReady.store(false);
            if (m_MouseDev && m_MouseButtons.exchange(0) != 0)
            {
                VirtualMouseReport rel{};
                rel.buttons = 0;
                m_MouseDev->EnqueueReport(rel);
                Sleep(15);
            }
            DetachPort(m_MousePort);
            m_MouseActive.store(false);
            return true;
        }
        m_MouseHidReady.store(false);
        if (m_MouseDev) m_MouseDev->SetReadyFlag(&m_MouseHidReady);
        m_MouseButtons.store(0);
        int port = 0;
        if (!AttachBusId("1-1", true, port))
        {
            return false;
        }
        m_MousePort = port;
        m_MouseActive.store(true);
        return true;
    }

    void UsbIpVirtualDevices::PushMouseDelta(int16_t dx, int16_t dy)
    {
        if (!m_MouseActive.load() || !m_MouseDev) return;
        VirtualMouseReport rpt{};
        rpt.buttons = m_MouseButtons.load();
        rpt.dx = dx;
        rpt.dy = dy;
        m_MouseDev->EnqueueReport(rpt);
    }

    void UsbIpVirtualDevices::PushMouseButton(uint8_t buttonMaskBit, bool isDown)
    {
        uint8_t cur = m_MouseButtons.load();
        while (!m_MouseButtons.compare_exchange_weak(
            cur, isDown ? static_cast<uint8_t>(cur | buttonMaskBit) : static_cast<uint8_t>(cur & ~buttonMaskBit)))
        {
        }
        if (!m_MouseActive.load() || !m_MouseDev) return;
        VirtualMouseReport rpt{};
        rpt.buttons = m_MouseButtons.load();
        m_MouseDev->EnqueueReport(rpt);
    }

    void UsbIpVirtualDevices::PushMouseWheel(int8_t wheel, int8_t hwheel)
    {
        if (!m_MouseActive.load() || !m_MouseDev) return;
        VirtualMouseReport rpt{};
        rpt.buttons = m_MouseButtons.load();
        rpt.wheel = wheel;
        rpt.hwheel = hwheel;
        m_MouseDev->EnqueueReport(rpt);
    }

    bool UsbIpVirtualDevices::SetVirtualMicEnabled(bool enabled)
    {
        if (enabled == m_MicActive.load()) return true;
        if (!enabled)
        {
            DetachPort(m_MicPort);
            m_MicActive.store(false);
            return true;
        }
        int port = 0;
        if (!AttachBusId("1-2", false, port))
        {
            return false;
        }
        m_MicPort = port;
        m_MicActive.store(true);
        return true;
    }

    void UsbIpVirtualDevices::PushMicPcm48kStereo(const int16_t* samples, size_t frameCount)
    {
        if (m_MicActive.load() && m_MicDev)
        {
            m_MicDev->PushPcm(samples, frameCount);
        }
    }

    std::wstring UsbIpVirtualDevices::FindVirtualAudioEndpointId(const wchar_t* friendlyMatch, bool isCapture)
    {
        HRESULT hrCom = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        std::wstring result;
        {
            ComPtr<IMMDeviceEnumerator> en;
            if (SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&en))))
            {
                ComPtr<IMMDeviceCollection> col;
                if (SUCCEEDED(en->EnumAudioEndpoints(isCapture ? eCapture : eRender, DEVICE_STATE_ACTIVE, &col)))
                {
                    UINT count = 0;
                    col->GetCount(&count);
                    for (UINT i = 0; i < count; ++i)
                    {
                        ComPtr<IMMDevice> dev;
                        if (FAILED(col->Item(i, &dev))) continue;
                        ComPtr<IPropertyStore> ps;
                        if (FAILED(dev->OpenPropertyStore(STGM_READ, &ps))) continue;
                        PROPVARIANT pv{};
                        PropVariantInit(&pv);
                        if (SUCCEEDED(ps->GetValue(PKEY_Device_FriendlyName, &pv)) && pv.vt == VT_LPWSTR && pv.pwszVal)
                        {
                            if (wcsstr(pv.pwszVal, friendlyMatch) != nullptr)
                            {
                                LPWSTR id = nullptr;
                                if (SUCCEEDED(dev->GetId(&id)) && id)
                                {
                                    result = id;
                                    CoTaskMemFree(id);
                                }
                            }
                        }
                        PropVariantClear(&pv);
                        if (!result.empty()) break;
                    }
                }
            }
        }
        if (SUCCEEDED(hrCom)) CoUninitialize();
        return result;
    }

    bool UsbIpVirtualDevices::SetDefaultAudioEndpoint(const std::wstring& deviceId)
    {
        if (deviceId.empty()) return false;
        HRESULT hrCom = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        bool ok = false;
        {
            ComPtr<IPolicyConfigShim> policy;
            if (SUCCEEDED(
                CoCreateInstance(kClsidPolicyConfigClient, nullptr, CLSCTX_ALL, kIidPolicyConfig, reinterpret_cast<void*
                    *>(policy.GetAddressOf()))))
            {
                HRESULT h1 = policy->SetDefaultEndpoint(deviceId.c_str(), eConsoleRole);
                HRESULT h2 = policy->SetDefaultEndpoint(deviceId.c_str(), eMultimediaRole);
                HRESULT h3 = policy->SetDefaultEndpoint(deviceId.c_str(), eCommunicationsRole);
                ok = SUCCEEDED(h1) || SUCCEEDED(h2) || SUCCEEDED(h3);
            }
        }
        if (SUCCEEDED(hrCom)) CoUninitialize();
        return ok;
    }

    bool UsbIpVirtualDevices::SwitchDefaultPlaybackToVirtualSpeaker()
    {
        std::lock_guard<std::mutex> lock(m_AudioPolicyMutex);
        HRESULT hrCom = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (m_OriginalDefaultRenderId.empty())
        {
            ComPtr<IMMDeviceEnumerator> en;
            ComPtr<IMMDevice> def;
            if (SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&en))) &&
                SUCCEEDED(en->GetDefaultAudioEndpoint(eRender, eConsole, &def)))
            {
                LPWSTR id = nullptr;
                if (SUCCEEDED(def->GetId(&id)) && id)
                {
                    m_OriginalDefaultRenderId = id;
                    CoTaskMemFree(id);
                }
            }
        }
        if (SUCCEEDED(hrCom)) CoUninitialize();

        std::wstring virtId;
        for (int i = 0; i < 25; ++i)
        {
            virtId = FindVirtualAudioEndpointId(L"Osmium Virtual Speaker", false);
            if (!virtId.empty()) break;
            Sleep(120);
        }
        if (virtId.empty())
        {
            return false;
        }

        if (m_OriginalDefaultRenderId == virtId)
        {
            m_OriginalDefaultRenderId.clear();
        }
        if (SetDefaultAudioEndpoint(virtId))
        {
            m_DefaultRenderSwitched.store(true);
            OSM_LOG_INFO("Switched host default playback endpoint to Osmium Virtual Speaker");
            return true;
        }
        return false;
    }

    void UsbIpVirtualDevices::RestoreDefaultPlaybackDevice()
    {
        std::lock_guard<std::mutex> lock(m_AudioPolicyMutex);
        if (m_DefaultRenderSwitched.exchange(false) && !m_OriginalDefaultRenderId.empty())
        {
            SetDefaultAudioEndpoint(m_OriginalDefaultRenderId);
            OSM_LOG_INFO("Restored original host default playback endpoint");
        }
        m_OriginalDefaultRenderId.clear();
    }

    bool UsbIpVirtualDevices::SetVirtualSpeakerEnabled(bool enabled)
    {
        if (enabled == m_SpeakerActive.load()) return true;
        if (!enabled)
        {
            RestoreDefaultPlaybackDevice();
            DetachPort(m_SpeakerPort);
            m_SpeakerActive.store(false);
            return true;
        }

        int port = 0;
        if (!AttachBusId("1-3", false, port))
        {
            return false;
        }
        m_SpeakerPort = port;
        m_SpeakerActive.store(true);
        SwitchDefaultPlaybackToVirtualSpeaker();
        return true;
    }

    size_t UsbIpVirtualDevices::PopSpeakerPcm48kStereo(int16_t* outSamples, size_t maxFrames)
    {
        if (!m_SpeakerActive.load() || !m_SpeakerDev) return 0;
        return m_SpeakerDev->PopPcm(outSamples, maxFrames);
    }
}
