#include <CppUnitTest.h>
#include <Windows.h>
#include <objbase.h>
#include <winrt/Windows.Data.Json.h>
#include <wil/resource.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <mutex>
#include <stdexcept>
#include <string>
#include <system_error>
#include <utility>
#include <vector>

#include "../two_way_pipe_message_ipc.h"

using namespace Microsoft::VisualStudio::CppUnitTestFramework;

namespace CommonInteropNativeTests
{
    namespace
    {
        constexpr DWORD PipeClientAccess = FILE_READ_DATA | FILE_READ_ATTRIBUTES | READ_CONTROL |
                                           FILE_WRITE_DATA | FILE_WRITE_ATTRIBUTES | SYNCHRONIZE;

        std::wstring UniquePipeName()
        {
            static std::atomic_ulong sequence = 0;
            return L"\\\\.\\pipe\\KitIpcTest_" + std::to_wstring(GetCurrentProcessId()) + L"_" +
                   std::to_wstring(GetTickCount64()) + L"_" + std::to_wstring(++sequence);
        }

        wil::unique_handle ConnectClient(const std::wstring& name)
        {
            const ULONGLONG deadline = GetTickCount64() + 2'000;
            do
            {
                HANDLE client = CreateFileW(name.c_str(), PipeClientAccess, 0, nullptr, OPEN_EXISTING,
                                            SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
                if (client != INVALID_HANDLE_VALUE)
                {
                    return wil::unique_handle(client);
                }
                const DWORD error = GetLastError();
                if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PIPE_BUSY)
                {
                    break;
                }
                WaitNamedPipeW(name.c_str(), 50);
            } while (GetTickCount64() < deadline);
            return {};
        }

        bool WriteMessage(const std::wstring& name, const std::wstring& message, bool terminate = false)
        {
            const auto client = ConnectClient(name);
            if (!client)
            {
                return false;
            }
            const DWORD bytes = static_cast<DWORD>((message.size() + (terminate ? 1 : 0)) * sizeof(wchar_t));
            DWORD written = 0;
            return WriteFile(client.get(), message.c_str(), bytes, &written, nullptr) && written == bytes;
        }

        wil::unique_handle NewEvent()
        {
            wil::unique_handle event(CreateEventW(nullptr, FALSE, FALSE, nullptr));
            Assert::IsTrue(static_cast<bool>(event), L"CreateEvent failed");
            return event;
        }

        void WaitForEvent(HANDLE event, DWORD timeout = 5'000)
        {
            Assert::AreEqual(static_cast<DWORD>(WAIT_OBJECT_0), WaitForSingleObject(event, timeout));
        }

        interop_auth::CallerPolicy SelfCallerPolicy()
        {
            wchar_t executable[MAX_PATH * 2]{};
            const DWORD length = GetModuleFileNameW(nullptr, executable, ARRAYSIZE(executable));
            Assert::IsTrue(length != 0 && length < ARRAYSIZE(executable));
            const std::wstring path(executable, length);
            const size_t separator = path.find_last_of(L"\\/");
            Assert::IsTrue(separator != std::wstring::npos);

            interop_auth::CallerPolicy policy;
            policy.enabled = true;
            policy.expectedClientPid = GetCurrentProcessId();
            policy.expectedDirectory = path.substr(0, separator);
            policy.allowedBasenames = { path.substr(separator + 1) };
            policy.expectedVersion = 0;
            policy.requireMicrosoftSignature = false;
            return policy;
        }
    }

    TEST_CLASS(TwoWayPipeMessageIPCTests)
    {
    public:
        TEST_METHOD_CLEANUP(ResetHooks)
        {
            two_way_pipe_message_ipc_test::ResetFaultInjection();
        }

        TEST_METHOD(OccupiedNameFailsSynchronouslyAndPartialStartupShutsDown)
        {
            const std::wstring name = UniquePipeName();
            wil::unique_handle occupied(CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
                                                         PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
                                                         1, 1024, 1024, 0, nullptr));
            Assert::IsTrue(occupied.get() != INVALID_HANDLE_VALUE, L"Could not occupy pipe name");
            TwoWayPipeMessageIPC server(name, UniquePipeName(), nullptr);
            Assert::ExpectException<std::runtime_error>([&] { server.start(nullptr); });
            server.end();
            server.end();

            // Fail after both queue threads started: startup must join them before throwing.
            two_way_pipe_message_ipc_test::FailThreadStartAfter(2);
            TwoWayPipeMessageIPC partial(UniquePipeName(), UniquePipeName(), nullptr);
            Assert::ExpectException<std::system_error>([&] { partial.start(nullptr); });
            partial.end();
        }

        TEST_METHOD(MaximumPayloadAcceptedAndMalformedMessagesDoNotStopDispatch)
        {
            constexpr size_t maximum = 1024 * 1024;
            const auto dispatched = NewEvent();
            const auto handled = NewEvent();
            two_way_pipe_message_ipc_test::SetHandlerCompletionEvents(handled.get(), nullptr);
            std::mutex received_mutex;
            std::vector<std::wstring> received;
            const std::wstring name = UniquePipeName();
            TwoWayPipeMessageIPC server(name, UniquePipeName(), [&](const std::wstring& message) {
                {
                    std::scoped_lock lock(received_mutex);
                    received.push_back(message);
                }
                SetEvent(dispatched.get());
            });
            server.start(nullptr);

            const std::wstring payload(maximum, L'x');
            Assert::IsTrue(WriteMessage(name, payload));
            WaitForEvent(handled.get());
            WaitForEvent(dispatched.get());
            Assert::IsTrue(WriteMessage(name, payload, true));
            WaitForEvent(handled.get());
            WaitForEvent(dispatched.get());

            // A completed wire message one character over the cap is rejected.
            WriteMessage(name, std::wstring(maximum + 1, L'x'));
            WaitForEvent(handled.get());

            // UTF-16 must contain complete code units; a zero payload must not
            // enter the queue because an empty string is its shutdown sentinel.
            {
                const auto client = ConnectClient(name);
                Assert::IsTrue(static_cast<bool>(client));
                constexpr BYTE odd_message[] = { 'x', 0, 'y' };
                DWORD written = 0;
                Assert::IsTrue(WriteFile(client.get(), odd_message, sizeof(odd_message), &written, nullptr) != FALSE);
            }
            WaitForEvent(handled.get());
            Assert::IsTrue(WriteMessage(name, L"", true));
            WaitForEvent(handled.get());

            Assert::IsTrue(WriteMessage(name, L"healthy"));
            WaitForEvent(handled.get());
            WaitForEvent(dispatched.get());
            server.end();
            std::scoped_lock lock(received_mutex);
            Assert::AreEqual(size_t{ 3 }, received.size());
            Assert::IsTrue(received[0] == payload && received[1] == payload);
            Assert::AreEqual(std::wstring(L"healthy"), received[2]);
        }

        TEST_METHOD(CallbackHasMtaAndThrowingCallbackDoesNotKillConsumer)
        {
            const auto first_dispatched = NewEvent();
            const auto json_dispatched = NewEvent();
            std::atomic_bool apartment_is_mta = false;
            std::atomic_bool parsed_json = false;
            const std::wstring name = UniquePipeName();
            TwoWayPipeMessageIPC server(name, UniquePipeName(), [&](const std::wstring& message) {
                if (message == L"throw")
                {
                    SetEvent(first_dispatched.get());
                    throw std::runtime_error("callback failure");
                }
                APTTYPE apartment{};
                APTTYPEQUALIFIER qualifier{};
                apartment_is_mta = SUCCEEDED(CoGetApartmentType(&apartment, &qualifier)) && apartment == APTTYPE_MTA;
                try
                {
                    const auto json = winrt::Windows::Data::Json::JsonObject::Parse(message);
                    parsed_json = json.GetNamedBoolean(L"ok");
                }
                catch (...)
                {
                }
                SetEvent(json_dispatched.get());
            });
            server.start(nullptr);
            Assert::IsTrue(WriteMessage(name, L"throw"));
            WaitForEvent(first_dispatched.get());
            Assert::IsTrue(WriteMessage(name, L"{\"ok\":true}"));
            WaitForEvent(json_dispatched.get());
            Assert::IsTrue(apartment_is_mta.load(), L"Callback thread has no MTA");
            Assert::IsTrue(parsed_json.load(), L"WinRT JSON parsing failed in callback");
        }

        TEST_METHOD(FullIdleConnectionsTimeOutAndFirstNewClientDispatches)
        {
            const auto dispatched = NewEvent();
            std::atomic_int count = 0;
            const std::wstring name = UniquePipeName();
            TwoWayPipeMessageIPC server(name, UniquePipeName(), [&](const std::wstring&) {
                ++count;
                SetEvent(dispatched.get());
            });
            server.start(nullptr);
            std::vector<wil::unique_handle> idle_clients;
            for (size_t i = 0; i < 16; ++i)
            {
                auto client = ConnectClient(name);
                Assert::IsTrue(static_cast<bool>(client));
                idle_clients.emplace_back(std::move(client));
            }

            const ULONGLONG deadline = GetTickCount64() + 8'000;
            bool all_disconnected = false;
            while (!all_disconnected && GetTickCount64() < deadline)
            {
                all_disconnected = true;
                for (const auto& client : idle_clients)
                {
                    DWORD available = 0;
                    if (PeekNamedPipe(client.get(), nullptr, 0, nullptr, &available, nullptr))
                    {
                        all_disconnected = false;
                        break;
                    }
                }
                if (!all_disconnected) Sleep(10);
            }
            Assert::IsTrue(all_disconnected, L"Idle connections were not disconnected after timeout");
            Assert::AreEqual(0, count.load());

            Assert::IsTrue(WriteMessage(name, L"after-timeout"));
            WaitForEvent(dispatched.get());
            server.end();
            Assert::AreEqual(1, count.load());
        }

        TEST_METHOD(CallerPidMismatchRejectsAndMatchingProcessDispatches)
        {
            const auto rejected = NewEvent();
            const auto accepted = NewEvent();
            std::atomic_bool mismatch_reason = false;
            std::atomic_int rejected_dispatch_count = 0;
            std::atomic_int accepted_dispatch_count = 0;
            const std::wstring rejected_name = UniquePipeName();
            auto rejected_policy = SelfCallerPolicy();
            rejected_policy.expectedClientPid = GetCurrentProcessId() + 1;
            rejected_policy.logReject = [&](const interop_auth::AuthResult& result) {
                mismatch_reason = result.pid == GetCurrentProcessId() && std::wstring(result.reasonCode) == L"pid-mismatch";
                SetEvent(rejected.get());
            };
            TwoWayPipeMessageIPC rejected_server(rejected_name, UniquePipeName(), [&](const std::wstring&) {
                ++rejected_dispatch_count;
            });
            rejected_server.start(nullptr, rejected_policy);
            // The server may reject the process before the client finishes writing.
            WriteMessage(rejected_name, L"must-not-dispatch");
            WaitForEvent(rejected.get());
            rejected_server.end();
            Assert::IsTrue(mismatch_reason.load());
            Assert::AreEqual(0, rejected_dispatch_count.load());

            const std::wstring accepted_name = UniquePipeName();
            TwoWayPipeMessageIPC accepted_server(accepted_name, UniquePipeName(), [&](const std::wstring&) {
                ++accepted_dispatch_count;
                SetEvent(accepted.get());
            });
            accepted_server.start(nullptr, SelfCallerPolicy());
            Assert::IsTrue(WriteMessage(accepted_name, L"accepted"));
            WaitForEvent(accepted.get());
            // A second connection also exercises the authenticated process cache.
            Assert::IsTrue(WriteMessage(accepted_name, L"accepted-again"));
            WaitForEvent(accepted.get());
            accepted_server.end();
            Assert::AreEqual(2, accepted_dispatch_count.load());
        }

        TEST_METHOD(FullHandlerCapacityBackpressuresBurstWithoutDroppingOrReordering)
        {
            constexpr int message_count = 1000;
            const auto write_pending = NewEvent();
            two_way_pipe_message_ipc_test::SetOutputWritePendingEvent(write_pending.get());
            std::mutex gate;
            std::condition_variable delivered;
            std::vector<std::wstring> received;
            const std::wstring input_name = UniquePipeName();
            const auto payload = [](int index) {
                return std::to_wstring(index) + L":" + std::wstring(index % 10 == 0 ? 2048 : 1, L'\u4e2d');
            };
            TwoWayPipeMessageIPC server(input_name, UniquePipeName(), [&](const std::wstring& message) {
                std::scoped_lock lock(gate);
                received.push_back(message);
                delivered.notify_one();
            });
            server.start(nullptr);
            std::vector<wil::unique_handle> idle_clients;
            for (size_t i = 0; i < 16; ++i)
            {
                auto client = ConnectClient(input_name);
                Assert::IsTrue(static_cast<bool>(client));
                idle_clients.emplace_back(std::move(client));
            }

            TwoWayPipeMessageIPC sender(UniquePipeName(), input_name, nullptr);
            sender.start(nullptr);
            for (int i = 0; i < message_count; ++i) sender.send(payload(i));
            // The first large write is held behind the full handler capacity.
            WaitForEvent(write_pending.get());
            idle_clients.clear();
            {
                std::unique_lock lock(gate);
                Assert::IsTrue(delivered.wait_for(lock, std::chrono::seconds(45), [&] {
                    return received.size() == message_count;
                }), L"Burst lost messages while the server was at capacity");
                for (int i = 0; i < message_count; ++i)
                    Assert::AreEqual(payload(i), received[static_cast<size_t>(i)]);
            }
            sender.end();
            server.end();
        }

        TEST_METHOD(SenderRetainsMessagesUntilDelayedListenerStarts)
        {
            constexpr int message_count = 64;
            const auto waiting_for_listener = NewEvent();
            two_way_pipe_message_ipc_test::SetWaitNamedPipeEnteredEvent(waiting_for_listener.get());
            std::mutex gate;
            std::condition_variable delivered;
            std::vector<std::wstring> received;
            const std::wstring input_name = UniquePipeName();
            TwoWayPipeMessageIPC server(input_name, UniquePipeName(), [&](const std::wstring& message) {
                std::scoped_lock lock(gate);
                received.push_back(message);
                delivered.notify_one();
            });
            TwoWayPipeMessageIPC sender(UniquePipeName(), input_name, nullptr);
            sender.start(nullptr);
            for (int i = 0; i < message_count; ++i) sender.send(std::to_wstring(i));
            WaitForEvent(waiting_for_listener.get());
            server.start(nullptr);
            {
                std::unique_lock lock(gate);
                Assert::IsTrue(delivered.wait_for(lock, std::chrono::seconds(15), [&] {
                    return received.size() == message_count;
                }), L"Messages queued before the listener existed were dropped");
                for (int i = 0; i < message_count; ++i)
                    Assert::AreEqual(std::to_wstring(i), received[static_cast<size_t>(i)]);
            }
            sender.end();
            server.end();
        }

        TEST_METHOD(AcceptOrderWaitsForEarlierReadAndRejectedSlotUnblocksLaterMessage)
        {
            const auto handled = NewEvent();
            two_way_pipe_message_ipc_test::SetHandlerCompletionEvents(handled.get(), nullptr);
            std::mutex gate;
            std::condition_variable delivered;
            std::vector<std::wstring> received;
            const std::wstring input_name = UniquePipeName();
            TwoWayPipeMessageIPC server(input_name, UniquePipeName(), [&](const std::wstring& message) {
                std::scoped_lock lock(gate);
                received.push_back(message);
                delivered.notify_one();
            });
            server.start(nullptr);
            TwoWayPipeMessageIPC sender(UniquePipeName(), input_name, nullptr);
            sender.start(nullptr);

            const auto first = ConnectClient(input_name);
            Assert::IsTrue(static_cast<bool>(first));
            sender.send(L"second");
            WaitForEvent(handled.get());
            {
                std::scoped_lock lock(gate);
                Assert::IsTrue(received.empty(), L"Later connection overtook the earlier unread connection");
            }
            constexpr wchar_t first_payload[] = L"first";
            DWORD written = 0;
            Assert::IsTrue(WriteFile(first.get(), first_payload, sizeof(first_payload), &written, nullptr) != FALSE);
            WaitForEvent(handled.get());
            {
                std::unique_lock lock(gate);
                Assert::IsTrue(delivered.wait_for(lock, std::chrono::seconds(5), [&] { return received.size() == 2; }));
                Assert::AreEqual(std::wstring(L"first"), received[0]);
                Assert::AreEqual(std::wstring(L"second"), received[1]);
            }

            const auto malformed = ConnectClient(input_name);
            Assert::IsTrue(static_cast<bool>(malformed));
            sender.send(L"after-rejection");
            WaitForEvent(handled.get());
            constexpr BYTE odd_payload[] = { 1 };
            Assert::IsTrue(WriteFile(malformed.get(), odd_payload, sizeof(odd_payload), &written, nullptr) != FALSE);
            WaitForEvent(handled.get());
            {
                std::unique_lock lock(gate);
                Assert::IsTrue(delivered.wait_for(lock, std::chrono::seconds(5), [&] { return received.size() == 3; }));
                Assert::AreEqual(std::wstring(L"after-rejection"), received[2]);
            }
            sender.end();
            server.end();
        }

        TEST_METHOD(ShutdownCancelsSenderWaitingForServerDisconnect)
        {
            const std::wstring pipe_name = UniquePipeName();
            const auto connected = NewEvent();
            wil::unique_handle server(CreateNamedPipeW(pipe_name.c_str(),
                PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
                PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT, 1, 1024, 1024, 0, nullptr));
            Assert::IsTrue(server.get() != INVALID_HANDLE_VALUE);
            OVERLAPPED connect{};
            connect.hEvent = connected.get();
            DWORD transferred = 0;
            const auto cancel_connect = wil::scope_exit([&] {
                CancelIoEx(server.get(), &connect);
                GetOverlappedResult(server.get(), &connect, &transferred, TRUE);
            });
            const BOOL connected_now = ConnectNamedPipe(server.get(), &connect);
            const DWORD connect_error = GetLastError();
            Assert::IsFalse(connected_now != FALSE);
            Assert::AreEqual(static_cast<DWORD>(ERROR_IO_PENDING), connect_error);

            TwoWayPipeMessageIPC sender(UniquePipeName(), pipe_name, nullptr);
            sender.start(nullptr);
            sender.send(L"held-open");
            WaitForEvent(connected.get());
            Assert::IsTrue(GetOverlappedResult(server.get(), &connect, &transferred, FALSE) != FALSE);
            DWORD available = 0;
            const ULONGLONG deadline = GetTickCount64() + 2'000;
            while (available == 0 && GetTickCount64() < deadline)
            {
                Assert::IsTrue(PeekNamedPipe(server.get(), nullptr, 0, nullptr, &available, nullptr) != FALSE);
                if (available == 0) Sleep(10);
            }
            Assert::IsTrue(available != 0);
            // This server deliberately keeps the connection open after the write.
            const auto began = std::chrono::steady_clock::now();
            sender.end();
            Assert::IsTrue(std::chrono::steady_clock::now() - began < std::chrono::seconds(1),
                           L"Shutdown waited for the disconnect deadline instead of canceling I/O");
        }
    };
}
