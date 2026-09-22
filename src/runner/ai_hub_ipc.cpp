#include "pch.h"
#include "ai_hub_ipc.h"

#include <common/interop/two_way_pipe_message_ipc.h>
#include <common/utils/json.h>
#include <memory>
#include <mutex>
#include <unordered_map>

extern TwoWayPipeMessageIPC* current_settings_ipc;
extern std::mutex ipc_mutex;

namespace
{
    constexpr size_t maximum_request_length = 524288;
    constexpr size_t maximum_response_length = 1048576;

    struct PendingRequest
    {
        wil::unique_handle completed{ CreateEventW(nullptr, TRUE, FALSE, nullptr) };
        std::wstring response;
        HRESULT result = HRESULT_FROM_WIN32(ERROR_NOT_READY);
    };

    std::mutex pending_mutex;
    std::unordered_map<std::wstring, std::shared_ptr<PendingRequest>> pending_requests;

    bool send_request(const std::wstring& message)
    {
        std::lock_guard lock(ipc_mutex);
        if (!current_settings_ipc)
        {
            return false;
        }

        current_settings_ipc->send(message);
        return true;
    }

    void send_cancel(const std::wstring& request_id)
    {
        json::JsonObject cancellation;
        cancellation.SetNamedValue(L"target", json::JsonValue::CreateStringValue(L"AiHub"));
        cancellation.SetNamedValue(L"action", json::JsonValue::CreateStringValue(L"ai_task_cancel"));
        cancellation.SetNamedValue(L"requestId", json::JsonValue::CreateStringValue(request_id));
        send_request(cancellation.Stringify().c_str());
    }
}

// An optional export preserves the existing PowertoyModuleIface vtable and DLL ABI.
extern "C" __declspec(dllexport) HRESULT WINAPI KitAiHubRequest(PCWSTR request_json, PWSTR* response_json, DWORD timeout_milliseconds, HANDLE cancellation_event)
{
    if (!request_json || !response_json || timeout_milliseconds == 0 || timeout_milliseconds > 3610000)
    {
        return E_INVALIDARG;
    }

    *response_json = nullptr;
    if (wcsnlen_s(request_json, maximum_request_length + 1) > maximum_request_length)
    {
        return HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);
    }

    const HWND tray = FindWindowW(L"KitTrayIconWindow", nullptr);
    if (tray && GetWindowThreadProcessId(tray, nullptr) == GetCurrentThreadId())
    {
        return HRESULT_FROM_WIN32(ERROR_POSSIBLE_DEADLOCK);
    }

    std::wstring request_id;
    try
    {
        json::JsonObject request = json::JsonObject::Parse(request_json);
        const auto action = request.GetNamedString(L"action", L"");
        if (request.GetNamedString(L"target", L"") != L"AiHub" ||
            (action != L"ai_task_request" && action != L"get_status" && action != L"self_test"))
        {
            return E_INVALIDARG;
        }

        GUID guid;
        RETURN_IF_FAILED(CoCreateGuid(&guid));
        wchar_t guid_text[39]{};
        StringFromGUID2(guid, guid_text, ARRAYSIZE(guid_text));
        request_id.assign(guid_text + 1, 36);
        for (auto& character : request_id)
        {
            if (character >= L'A' && character <= L'F')
            {
                character = static_cast<wchar_t>(character + (L'a' - L'A'));
            }
        }
        request.SetNamedValue(L"requestId", json::JsonValue::CreateStringValue(request_id));
        request.SetNamedValue(L"version", json::JsonValue::CreateNumberValue(1));
        const std::wstring serialized_request = request.Stringify().c_str();
        if (serialized_request.size() > maximum_request_length)
        {
            return HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);
        }

        auto pending = std::make_shared<PendingRequest>();
        if (!pending->completed)
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        {
            std::lock_guard lock(pending_mutex);
            if (pending_requests.size() >= 16)
            {
                return HRESULT_FROM_WIN32(ERROR_BUSY);
            }

            pending_requests.emplace(request_id, pending);
        }

        if (!send_request(serialized_request))
        {
            std::lock_guard lock(pending_mutex);
            pending_requests.erase(request_id);
            return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        }

        HANDLE events[] = { pending->completed.get(), cancellation_event };
        const DWORD wait_result = WaitForMultipleObjects(cancellation_event ? 2 : 1, events, FALSE, timeout_milliseconds);
        HRESULT result;
        {
            std::lock_guard lock(pending_mutex);
            pending_requests.erase(request_id);
            result = pending->result;
            if (wait_result == WAIT_OBJECT_0 && SUCCEEDED(result))
            {
                const size_t bytes = (pending->response.size() + 1) * sizeof(wchar_t);
                *response_json = static_cast<PWSTR>(CoTaskMemAlloc(bytes));
                if (!*response_json)
                {
                    return E_OUTOFMEMORY;
                }

                memcpy(*response_json, pending->response.c_str(), bytes);
            }
        }

        if (wait_result != WAIT_OBJECT_0)
        {
            const DWORD error = wait_result == WAIT_TIMEOUT ? ERROR_TIMEOUT
                : wait_result == WAIT_OBJECT_0 + 1 ? ERROR_CANCELLED : GetLastError();
            send_cancel(request_id);
            return HRESULT_FROM_WIN32(error);
        }

        return result;
    }
    catch (...)
    {
        if (!request_id.empty())
        {
            std::lock_guard lock(pending_mutex);
            pending_requests.erase(request_id);
        }

        return E_FAIL;
    }
}

bool try_complete_ai_hub_response(const std::wstring& message)
{
    if (message.size() > maximum_response_length || message.find(L"AiHub") == std::wstring::npos)
    {
        return false;
    }

    try
    {
        const auto response = json::JsonObject::Parse(message);
        if (response.GetNamedString(L"target", L"") != L"AiHub" || response.GetNamedString(L"action", L"") != L"ai_task_response")
        {
            return false;
        }

        const std::wstring request_id = response.GetNamedString(L"requestId", L"").c_str();
        std::lock_guard lock(pending_mutex);
        const auto request = pending_requests.find(request_id);
        if (request != pending_requests.end())
        {
            request->second->response = message;
            request->second->result = S_OK;
            SetEvent(request->second->completed.get());
        }

        return true;
    }
    catch (...)
    {
        return false;
    }
}

void cancel_ai_hub_requests()
{
    std::lock_guard lock(pending_mutex);
    for (auto& [id, request] : pending_requests)
    {
        request->result = HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE);
        SetEvent(request->completed.get());
    }
}
