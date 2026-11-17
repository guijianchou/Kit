#pragma once

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Data.Json.h>

#include <optional>
#include <fstream>
#include <atomic>
#include <algorithm>

namespace json
{
    using namespace winrt::Windows::Data::Json;

    inline std::optional<JsonObject> from_file(std::wstring_view file_name)
    {
        try
        {
            std::ifstream file(file_name.data(), std::ios::binary);
            if (file.is_open())
            {
                using isbi = std::istreambuf_iterator<char>;
                std::string obj_str{ isbi{ file }, isbi{} };
                return JsonValue::Parse(winrt::to_hstring(obj_str)).GetObjectW();
            }
            return std::nullopt;
        }
        catch (...)
        {
            return std::nullopt;
        }
    }

    inline void to_file(std::wstring_view file_name, const JsonObject& obj)
    {
        const std::wstring destination(file_name);
        const std::string contents = winrt::to_string(obj.Stringify());
        static std::atomic<unsigned long long> sequence{ 0 };
        const std::wstring temporary = destination + L"." + std::to_wstring(GetCurrentProcessId()) + L"." +
                                       std::to_wstring(GetTickCount64()) + L"." + std::to_wstring(sequence.fetch_add(1)) + L".tmp";
        struct TemporaryFile
        {
            const std::wstring& path;
            HANDLE handle = INVALID_HANDLE_VALUE;
            bool created = false;
            ~TemporaryFile()
            {
                if (handle != INVALID_HANDLE_VALUE) CloseHandle(handle);
                if (created) DeleteFileW(path.c_str());
            }
        } file{ temporary };
        file.handle = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file.handle == INVALID_HANDLE_VALUE) winrt::throw_last_error();
        file.created = true;
        size_t offset = 0;
        while (offset < contents.size())
        {
            const DWORD chunk = static_cast<DWORD>((std::min)(contents.size() - offset, size_t{ 1024 * 1024 }));
            DWORD written = 0;
            if (!WriteFile(file.handle, contents.data() + offset, chunk, &written, nullptr)) winrt::throw_last_error();
            if (written == 0) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_WRITE_FAULT));
            offset += written;
        }
        if (!FlushFileBuffers(file.handle)) winrt::throw_last_error();
        CloseHandle(file.handle);
        file.handle = INVALID_HANDLE_VALUE;
        if (!MoveFileExW(temporary.c_str(), destination.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
            winrt::throw_last_error();
        file.created = false;
    }

    inline bool has(
        const json::JsonObject& o,
        std::wstring_view name,
        const json::JsonValueType type = JsonValueType::Object)
    {
        return o.HasKey(name) && o.GetNamedValue(name).ValueType() == type;
    }

    template<typename T>
    inline std::enable_if_t<std::is_arithmetic_v<T>, JsonValue> value(const T arithmetic)
    {
        return json::JsonValue::CreateNumberValue(arithmetic);
    }

    template<typename T>
    inline std::enable_if_t<!std::is_arithmetic_v<T>, JsonValue> value(T s)
    {
        return json::JsonValue::CreateStringValue(s);
    }

    inline JsonValue value(const bool boolean)
    {
        return json::JsonValue::CreateBooleanValue(boolean);
    }

    inline JsonValue value(JsonObject value)
    {
        return value.as<JsonValue>();
    }

    inline JsonValue value(JsonValue value)
    {
        return value; // identity function overload for convenience
    }

    template<typename T, typename D = std::optional<T>>
        requires std::constructible_from<std::optional<T>, D>
    void get(const json::JsonObject& o, const wchar_t* name, T& destination, D default_value = std::nullopt)
    {
        try
        {
            if constexpr (std::is_same_v<T, bool>)
            {
                destination = o.GetNamedBoolean(name);
            }
            else if constexpr (std::is_arithmetic_v<T>)
            {
                destination = static_cast<T>(o.GetNamedNumber(name));
            }
            else if constexpr (std::is_same_v<T, std::wstring>)
            {
                destination = o.GetNamedString(name);
            }
            else if constexpr (std::is_same_v<T, json::JsonObject>)
            {
                destination = o.GetNamedObject(name);
            }
            else
            {
                static_assert(std::bool_constant<std::is_same_v<T, T&>>::value, "Unsupported type");
            }
        }
        catch (...)
        {
            std::optional<T> maybe_default{ std::move(default_value) };
            if (maybe_default.has_value())
                destination = std::move(*maybe_default);
        }
    }

}
