#include <CppUnitTest.h>
#include <Windows.h>
#include <wil/resource.h>
#include <atomic>
#include <chrono>
#include "../../../runner/quick_access_host.h"

using namespace Microsoft::VisualStudio::CppUnitTestFramework;
using QuickAccessHost::detail::LifecycleWorker;
using QuickAccessHost::detail::Request;

namespace CommonInteropNativeTests
{
    TEST_CLASS(QuickAccessLifecycleTests)
    {
        static wil::unique_handle Event()
        {
            wil::unique_handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            Assert::IsTrue(static_cast<bool>(event));
            return event;
        }

        static void Wait(HANDLE event)
        {
            Assert::AreEqual(static_cast<DWORD>(WAIT_OBJECT_0), WaitForSingleObject(event, 5'000));
        }

    public:
        TEST_METHOD(StopRequestDoesNotWaitForStartupAndSuppressesStaleShow)
        {
            const auto started = Event();
            const auto release_start = Event();
            const auto stopped = Event();
            std::atomic_int shows = 0;
            LifecycleWorker worker({
                [&] {
                    SetEvent(started.get());
                    WaitForSingleObject(release_start.get(), 5'000);
                },
                [&] { ++shows; },
                [&](HANDLE) { SetEvent(stopped.get()); },
                {}
            });
            const auto release_on_failure = wil::scope_exit([&] { SetEvent(release_start.get()); });
            worker.request(Request::Show);
            Wait(started.get());
            const auto began = std::chrono::steady_clock::now();
            worker.request(Request::Stop);
            const auto elapsed = std::chrono::steady_clock::now() - began;
            SetEvent(release_start.get());
            Wait(stopped.get());
            worker.shutdown(true);
            Assert::IsTrue(elapsed < std::chrono::seconds(1), L"Stop request blocked on child startup");
            Assert::AreEqual(0, shows.load());
        }

        TEST_METHOD(ShowQueuedDuringStopRunsAfterStopOnSameWorker)
        {
            const auto stopping = Event();
            const auto release_stop = Event();
            const auto shown = Event();
            std::atomic_int starts = 0;
            std::atomic_int stops = 0;
            std::atomic<DWORD> owner = 0;
            std::atomic_bool serialized = true;
            LifecycleWorker worker({
                [&] {
                    serialized = serialized.load() && owner.load() == GetCurrentThreadId() && stops.load() == 1;
                    ++starts;
                },
                [&] { SetEvent(shown.get()); },
                [&](HANDLE) {
                    if (++stops == 1)
                    {
                        owner = GetCurrentThreadId();
                        SetEvent(stopping.get());
                        WaitForSingleObject(release_stop.get(), 5'000);
                    }
                },
                {}
            });
            const auto release_on_failure = wil::scope_exit([&] { SetEvent(release_stop.get()); });
            worker.request(Request::Stop);
            Wait(stopping.get());
            worker.request(Request::Show);
            Assert::AreEqual(0, starts.load());
            SetEvent(release_stop.get());
            Wait(shown.get());
            worker.shutdown(true);
            Assert::IsTrue(serialized.load());
            Assert::AreEqual(1, starts.load());
            Assert::AreEqual(2, stops.load(), L"Final shutdown must run after the queued show");
        }

        TEST_METHOD(FinalShutdownCancelsWaitAndDiscardsPendingShow)
        {
            const auto stopping = Event();
            std::atomic_int starts = 0;
            std::atomic_int shows = 0;
            std::atomic_bool canceled = false;
            LifecycleWorker worker({
                [&] { ++starts; },
                [&] { ++shows; },
                [&](HANDLE cancel) {
                    SetEvent(stopping.get());
                    canceled = WaitForSingleObject(cancel, 5'000) == WAIT_OBJECT_0;
                },
                {}
            });
            worker.request(Request::Stop);
            Wait(stopping.get());
            worker.request(Request::Show);
            const auto began = std::chrono::steady_clock::now();
            worker.shutdown(false);
            const auto elapsed = std::chrono::steady_clock::now() - began;
            worker.request(Request::Show);
            worker.shutdown(true);
            Assert::IsTrue(elapsed < std::chrono::seconds(1), L"Final shutdown did not interrupt the grace-period wait");
            Assert::IsTrue(canceled.load());
            Assert::AreEqual(0, starts.load());
            Assert::AreEqual(0, shows.load());
        }
    };
}
