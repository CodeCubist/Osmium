#include "HostTextFocus.h"

#include <ole2.h>
#include <UIAutomation.h>

#include <algorithm>
#include <cmath>

#pragma comment(lib, "oleaut32.lib")

namespace osmium
{
    namespace
    {
        constexpr DWORD kPollMs = 150;
        constexpr uint64_t kHeartbeatMs = 1000;
        // A field this tall (a whole editor pane) says nothing about where
        // the caret is, so no rect is reported for it.
        constexpr float kMaxFieldHeight = 0.34f;

        template <typename T>
        void SafeRelease(T*& p)
        {
            if (p)
            {
                p->Release();
                p = nullptr;
            }
        }

        // The first box of a text range, in physical screen pixels.
        bool RangeRect(IUIAutomationTextRange* range, RECT& out)
        {
            SAFEARRAY* boxes = nullptr;
            if (FAILED(range->GetBoundingRectangles(&boxes)) || !boxes) return false;
            bool ok = false;
            LONG lo = 0;
            LONG hi = -1;
            SafeArrayGetLBound(boxes, 1, &lo);
            SafeArrayGetUBound(boxes, 1, &hi);
            double* d = nullptr;
            if (hi - lo + 1 >= 4 && SUCCEEDED(SafeArrayAccessData(boxes, reinterpret_cast<void**>(&d))))
            {
                // left, top, width, height; a collapsed caret may be 0 wide.
                if (d[3] > 0.0)
                {
                    out.left = static_cast<LONG>(std::floor(d[0]));
                    out.top = static_cast<LONG>(std::floor(d[1]));
                    out.right = static_cast<LONG>(std::ceil(d[0] + std::max(1.0, d[2])));
                    out.bottom = static_cast<LONG>(std::ceil(d[1] + d[3]));
                    ok = true;
                }
                SafeArrayUnaccessData(boxes);
            }
            SafeArrayDestroy(boxes);
            return ok;
        }

        IUIAutomationTextRange* CaretRange(IUIAutomationElement* el)
        {
            IUIAutomationTextPattern2* text = nullptr;
            if (FAILED(el->GetCurrentPatternAs(UIA_TextPattern2Id, IID_PPV_ARGS(&text))) || !text) return nullptr;
            BOOL caretActive = FALSE;
            IUIAutomationTextRange* range = nullptr;
            if (FAILED(text->GetCaretRange(&caretActive, &range))) range = nullptr;
            text->Release();
            return range;
        }

        bool CaretRect(IUIAutomationElement* el, RECT& out)
        {
            IUIAutomationTextRange* range = CaretRange(el);
            if (!range) return false;
            bool ok = RangeRect(range, out);
            // Some providers give a collapsed range no box; measure the
            // character at the caret instead.
            if (!ok && SUCCEEDED(range->ExpandToEnclosingUnit(TextUnit_Character)))
            {
                ok = RangeRect(range, out);
            }
            range->Release();
            return ok;
        }

        // -1 when the control exposes no text, else whether its text (at the
        // caret, or over the document) is read-only.
        int TextReadOnly(IUIAutomationElement* el)
        {
            IUIAutomationTextRange* range = CaretRange(el);
            if (!range)
            {
                IUIAutomationTextPattern* text = nullptr;
                if (FAILED(el->GetCurrentPatternAs(UIA_TextPatternId, IID_PPV_ARGS(&text))) || !text) return -1;
                if (FAILED(text->get_DocumentRange(&range))) range = nullptr;
                text->Release();
                if (!range) return -1;
            }
            int readOnly = -1;
            VARIANT v;
            VariantInit(&v);
            if (SUCCEEDED(range->GetAttributeValue(UIA_IsReadOnlyAttributeId, &v)) && v.vt == VT_BOOL)
            {
                readOnly = (v.boolVal != VARIANT_FALSE) ? 1 : 0;
            }
            VariantClear(&v);
            range->Release();
            return readOnly;
        }

        bool TakesText(IUIAutomationElement* el)
        {
            CONTROLTYPEID type = 0;
            if (FAILED(el->get_CurrentControlType(&type))) return false;
            if (type != UIA_EditControlTypeId && type != UIA_DocumentControlTypeId &&
                type != UIA_ComboBoxControlTypeId)
            {
                return false;
            }
            BOOL enabled = TRUE;
            if (SUCCEEDED(el->get_CurrentIsEnabled(&enabled)) && !enabled) return false;

            IUIAutomationValuePattern* value = nullptr;
            if (SUCCEEDED(el->GetCurrentPatternAs(UIA_ValuePatternId, IID_PPV_ARGS(&value))) && value)
            {
                BOOL readOnly = TRUE;
                HRESULT hr = value->get_CurrentIsReadOnly(&readOnly);
                value->Release();
                if (SUCCEEDED(hr)) return !readOnly;
            }
            // A drop-down without a value pattern only picks from a list.
            if (type == UIA_ComboBoxControlTypeId) return false;
            const int readOnly = TextReadOnly(el);
            if (readOnly >= 0) return readOnly == 0;
            return type == UIA_EditControlTypeId;
        }

        // The classic Win32 caret of the foreground thread, for windows UI
        // Automation can't see (an elevated app, say).
        bool Win32Caret(RECT& out, bool& hasRect, HWND& focus)
        {
            hasRect = false;
            focus = nullptr;
            HWND fg = GetForegroundWindow();
            if (!fg) return false;
            GUITHREADINFO gti{};
            gti.cbSize = sizeof(gti);
            if (!GetGUIThreadInfo(GetWindowThreadProcessId(fg, nullptr), &gti)) return false;
            focus = gti.hwndFocus;
            if (!gti.hwndCaret) return false;
            // Caret coordinates are physical only for per-monitor aware windows.
            const DPI_AWARENESS awareness =
                GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(gti.hwndCaret));
            if (awareness == DPI_AWARENESS_PER_MONITOR_AWARE)
            {
                POINT tl{gti.rcCaret.left, gti.rcCaret.top};
                POINT br{gti.rcCaret.right, gti.rcCaret.bottom};
                if (ClientToScreen(gti.hwndCaret, &tl) && ClientToScreen(gti.hwndCaret, &br) && br.y > tl.y)
                {
                    out = RECT{tl.x, tl.y, std::max(br.x, tl.x + 1), br.y};
                    hasRect = true;
                }
            }
            return true;
        }
    }

    HostTextFocus::~HostTextFocus()
    {
        Stop();
    }

    void HostTextFocus::Start(Sink sink)
    {
        std::lock_guard<std::mutex> control(m_ControlMutex);
        if (m_Thread.joinable()) return;
        m_Sink = std::move(sink);
        {
            std::lock_guard<std::mutex> lock(m_Mutex);
            m_Stop = false;
        }
        m_Thread = std::thread(&HostTextFocus::Run, this);
    }

    void HostTextFocus::RequestStop()
    {
        {
            std::lock_guard<std::mutex> lock(m_Mutex);
            m_Stop = true;
        }
        m_Cv.notify_all();
    }

    void HostTextFocus::Stop()
    {
        RequestStop();
        std::lock_guard<std::mutex> control(m_ControlMutex);
        if (m_Thread.joinable())
        {
            if (m_Thread.get_id() == std::this_thread::get_id()) m_Thread.detach();
            else m_Thread.join();
        }
    }

    void HostTextFocus::SetDisplayRect(RECT rect)
    {
        std::lock_guard<std::mutex> lock(m_Mutex);
        m_DisplayRect = rect;
    }

    void HostTextFocus::Run()
    {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        const HRESULT coHr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);

        IUIAutomation* uia = nullptr;
        if (FAILED(CoCreateInstance(__uuidof(CUIAutomation8), nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&uia))))
        {
            uia = nullptr;
            if (FAILED(CoCreateInstance(__uuidof(CUIAutomation), nullptr, CLSCTX_INPROC_SERVER,
                IID_PPV_ARGS(&uia))))
            {
                uia = nullptr;
            }
        }
        if (uia)
        {
            IUIAutomation2* uia2 = nullptr;
            if (SUCCEEDED(uia->QueryInterface(IID_PPV_ARGS(&uia2))) && uia2)
            {
                // A hung app answers late, and a late answer is worth less
                // than the next poll.
                uia2->put_ConnectionTimeout(1000);
                uia2->put_TransactionTimeout(1000);
                uia2->Release();
            }
        }
        else
        {
            OSM_LOG_WARN("Text focus: UI Automation unavailable, using the Win32 caret only");
        }

        IUIAutomationElement* lastFocus = nullptr;
        HWND lastHwnd = nullptr;
        uint16_t serial = 0;
        State sent{};
        bool haveSent = false;
        uint64_t lastSendMs = 0;

        for (;;)
        {
            RECT display{};
            {
                std::lock_guard<std::mutex> lock(m_Mutex);
                if (m_Stop) break;
                display = m_DisplayRect;
            }

            State next{};
            RECT box{};
            bool hasBox = false;
            bool fieldBox = false;

            IUIAutomationElement* focus = nullptr;
            if (uia && SUCCEEDED(uia->GetFocusedElement(&focus)) && focus)
            {
                BOOL same = FALSE;
                if (!lastFocus || FAILED(uia->CompareElements(focus, lastFocus, &same)) || !same)
                {
                    ++serial;
                    SafeRelease(lastFocus);
                    lastFocus = focus;
                    lastFocus->AddRef();
                }
                lastHwnd = nullptr;
                next.active = TakesText(focus);
                if (next.active)
                {
                    hasBox = CaretRect(focus, box);
                    RECT field{};
                    if (!hasBox && SUCCEEDED(focus->get_CurrentBoundingRectangle(&field)) &&
                        field.bottom > field.top)
                    {
                        box = field;
                        hasBox = true;
                        fieldBox = true;
                    }
                }
                focus->Release();
            }
            else
            {
                SafeRelease(lastFocus);
                HWND hwnd = nullptr;
                next.active = Win32Caret(box, hasBox, hwnd);
                if (hwnd != lastHwnd)
                {
                    ++serial;
                    lastHwnd = hwnd;
                }
            }

            const float dw = static_cast<float>(display.right - display.left);
            const float dh = static_cast<float>(display.bottom - display.top);
            RECT visible{};
            if (next.active && hasBox && dw > 0.0f && dh > 0.0f && IntersectRect(&visible, &box, &display))
            {
                next.x = static_cast<float>(box.left - display.left) / dw;
                next.y = static_cast<float>(box.top - display.top) / dh;
                next.w = static_cast<float>(box.right - box.left) / dw;
                next.h = static_cast<float>(box.bottom - box.top) / dh;
                next.hasRect = !(fieldBox && next.h > kMaxFieldHeight);
                if (!next.hasRect) next.x = next.y = next.w = next.h = 0.0f;
            }
            next.serial = serial;

            const uint64_t now = NowMillis();
            const bool changed = !haveSent || next.active != sent.active || next.serial != sent.serial ||
                next.hasRect != sent.hasRect || std::fabs(next.y - sent.y) > 0.01f ||
                std::fabs(next.h - sent.h) > 0.01f || std::fabs(next.x - sent.x) > 0.05f;
            if (changed || now - lastSendMs >= kHeartbeatMs)
            {
                if (m_Sink) m_Sink(next);
                sent = next;
                haveSent = true;
                lastSendMs = now;
            }

            std::unique_lock<std::mutex> lock(m_Mutex);
            m_Cv.wait_for(lock, std::chrono::milliseconds(kPollMs), [this] { return m_Stop; });
        }

        SafeRelease(lastFocus);
        SafeRelease(uia);
        if (SUCCEEDED(coHr)) CoUninitialize();
    }
}
