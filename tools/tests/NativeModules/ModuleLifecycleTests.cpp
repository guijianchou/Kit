#include "../../../src/runner/module_lifecycle.h"
#include <cassert>
#include <future>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <vector>

using namespace std::chrono_literals;

namespace
{
    void require(bool value, const char* message)
    {
        if (!value) throw std::runtime_error(message);
    }

    template<class Predicate>
    void until(Predicate condition)
    {
        const auto deadline = std::chrono::steady_clock::now() + 3s;
        while (!condition())
        {
            if (std::chrono::steady_clock::now() >= deadline) throw std::runtime_error("Timed out");
            std::this_thread::sleep_for(1ms);
        }
    }

    void nonblocking_state_and_serialized_config()
    {
        std::atomic<bool> alive = false;
        std::promise<void> entered;
        std::promise<void> release;
        auto released = release.get_future().share();
        std::atomic<int> configured = 0;
        ModuleLifecycle lifecycle({ {}, {},
            [&] { entered.set_value(); released.wait(); alive = true; },
            [&] { alive = false; }, [&] { return alive.load(); }, {}, {} }, 10ms, 10ms);
        require(lifecycle.request(true), "Enable rejected");
        require(entered.get_future().wait_for(3s) == std::future_status::ready, "Enable did not start");
        auto read = std::async(std::launch::async, [&] {
            require(lifecycle.desired(), "Intent lost during startup");
            require(!lifecycle.running(), "Startup falsely reported running");
            return lifecycle.submit([&] { require(alive, "Config overlapped startup"); ++configured; });
        });
        bool responsive = read.wait_for(100ms) == std::future_status::ready;
        release.set_value();
        require(responsive && read.get(), "UI operations blocked on lifecycle work");
        until([&] { return configured == 1; });
        require(lifecycle.request(false), "Disable rejected");
        until([&] { return !alive; });
    }

    void rapid_toggle_coalesces_and_shutdown_drains()
    {
        std::atomic<bool> alive = false;
        std::atomic<int> starts = 0;
        std::atomic<int> writes = 0;
        std::promise<void> entered;
        std::promise<void> release;
        auto released = release.get_future().share();
        auto lifecycle = std::make_unique<ModuleLifecycle>(ModuleLifecycle::Callbacks{ {}, {},
            [&] { ++starts; alive = true; }, [&] { alive = false; },
            [&] { return alive.load(); }, {}, {} }, 10ms, 10ms);
        lifecycle->submit([&] { entered.set_value(); released.wait(); });
        require(entered.get_future().wait_for(3s) == std::future_status::ready, "Barrier did not start");
        lifecycle->request(true);
        lifecycle->submit([&] { ++writes; });
        lifecycle->request(false);
        require(!lifecycle->desired(), "Disable not immediately reflected");
        auto shutdown = std::async(std::launch::async, [&] { lifecycle.reset(); });
        bool joined = shutdown.wait_for(50ms) != std::future_status::ready;
        release.set_value();
        require(joined, "Shutdown failed to join in-flight work");
        shutdown.get();
        require(starts == 0 && writes == 1 && !alive, "Shutdown lost writes or revived disabled module");
    }

    void crash_recovers_but_user_disable_stops_retry()
    {
        std::atomic<bool> alive = false;
        std::atomic<int> starts = 0;
        ModuleLifecycle lifecycle({ {}, {}, [&] { ++starts; alive = true; },
            [&] { alive = false; }, [&] { return alive.load(); }, {}, {} }, 5ms, 10ms);
        lifecycle.request(true);
        until([&] { return starts == 1 && lifecycle.running(); });
        alive = false;
        until([&] { return starts >= 2 && lifecycle.running(); });
        lifecycle.request(false);
        until([&] { return !lifecycle.running() && !alive; });
        const int stoppedStarts = starts;
        std::this_thread::sleep_for(80ms);
        require(starts == stoppedStarts, "Disabled module restarted");
    }

    void repeated_failures_back_off_and_exceptions_do_not_break_queue()
    {
        std::atomic<int> attempts = 0;
        std::atomic<int> failures = 0;
        std::atomic<int> writes = 0;
        ModuleLifecycle lifecycle({ {}, {}, [&] { ++attempts; throw std::runtime_error("synthetic"); },
            [] {}, [] { return false; }, {}, [&] { ++failures; } }, 2ms, 20ms);
        lifecycle.request(true);
        std::this_thread::sleep_for(150ms);
        require(attempts >= 2 && attempts <= 5, "Retries did not back off");
        lifecycle.submit([&] { ++writes; });
        until([&] { return writes == 1; });
        require(failures > 0, "Failures unobserved");
        lifecycle.shutdown();
        require(!lifecycle.submit([] {}), "Shutdown accepted work");
    }
}

int main()
{
    try
    {
        nonblocking_state_and_serialized_config();
        rapid_toggle_coalesces_and_shutdown_drains();
        crash_recovers_but_user_disable_stops_retry();
        repeated_failures_back_off_and_exceptions_do_not_break_queue();
        std::cout << "PASS: ModuleLifecycle behavioral checks (4)\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL: " << error.what() << '\n';
        return 1;
    }
}
