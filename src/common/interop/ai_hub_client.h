#pragma once

#include <Windows.h>
#include <objbase.h>
#include <string>

namespace kit::ai
{
    // Call from a plugin worker thread. Kit Settings must be running as the AI host.
    // The request uses AiHub IPC version 1; the Runner assigns its correlation ID.
    inline HRESULT request(const std::wstring& requestJson, std::wstring& responseJson, DWORD timeoutMilliseconds = 610000, HANDLE cancellationEvent = nullptr)
    {
        using RequestFunction = HRESULT(WINAPI*)(PCWSTR, PWSTR*, DWORD, HANDLE);
        const auto function = reinterpret_cast<RequestFunction>(GetProcAddress(GetModuleHandleW(nullptr), "KitAiHubRequest"));
        if (!function)
        {
            return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
        }

        PWSTR response = nullptr;
        const HRESULT result = function(requestJson.c_str(), &response, timeoutMilliseconds, cancellationEvent);
        if (response)
        {
            responseJson.assign(response);
            CoTaskMemFree(response);
        }

        return result;
    }
}
