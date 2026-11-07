// Exercises the production lifecycle method bodies with inert worker callbacks.
// No module DLL, worker process, settings, credentials, or named events are used.
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdlib>
#include <future>
#include <iostream>
#include <mutex>
#include <thread>

using namespace std::chrono_literals;

void Check(bool condition, const char* message)
{
    if (!condition)
    {
        std::cerr << message << std::endl;
        // A failed lifetime assertion may leave a deliberately blocked thread.
        std::_Exit(1);
    }
}

struct StartupGate
{
    std::mutex mutex;
    std::condition_variable changed;
    bool entered = false;
    bool released = false;

    void Wait()
    {
        std::unique_lock lock(mutex);
        entered = true;
        changed.notify_all();
        changed.wait(lock, [this] { return released; });
    }

    void WaitUntilEntered()
    {
        std::unique_lock lock(mutex);
        Check(changed.wait_for(lock, 5s, [this] { return entered; }), "Startup thread did not reach its gate.");
    }

    void Release()
    {
        std::lock_guard lock(mutex);
        released = true;
        changed.notify_all();
    }
};

StartupGate* startupGate = nullptr;
std::atomic<int> starts = 0;
std::atomic<int> stops = 0;
std::atomic<int> closes = 0;
std::atomic<bool> workerRunning = false;

void WaitAtStartupGate()
{
    if (startupGate)
        startupGate->Wait();
}

struct Trace
{
    static void EnableAIHub(bool) {}
};

struct Logger
{
    static void info(const wchar_t*) {}
};

struct LifecycleInterface
{
    virtual ~LifecycleInterface() = default;
    virtual void destroy() = 0;
    virtual void enable() = 0;
    virtual void disable() = 0;
    virtual bool is_enabled() = 0;
};

class AIHubModule : public LifecycleInterface
{
    bool m_enabled = false;
    std::mutex m_lifecycle_mutex;
    std::thread m_startup_thread;

    void start_worker_if_needed()
    {
        Check(!workerRunning.exchange(true), "Worker started twice without stopping.");
        ++starts;
    }

    void stop_worker_if_running()
    {
        if (workerRunning.exchange(false))
            ++stops;
    }

    void close_worker_handle() { ++closes; }

public:
    // Generated from dllmain.cpp on every invocation, never a copied implementation.
#include "AIHubLifecycleMethods.generated.h"
};

void CheckPendingStartup(bool destroy)
{
    StartupGate gate;
    startupGate = &gate;
    auto* module = new AIHubModule();
    const int previousStarts = starts.load();
    const int previousStops = stops.load();
    const int previousCloses = closes.load();
    module->enable();
    gate.WaitUntilEntered();
    Check(module->is_enabled(), "Asynchronous enable did not publish enabled state.");
    module->enable();

    std::promise<void> called;
    auto entered = called.get_future();
    auto completion = std::async(std::launch::async, [&] {
        called.set_value();
        if (destroy)
            module->destroy();
        else
            module->disable();
    });
    entered.wait();
    Check(completion.wait_for(200ms) == std::future_status::timeout,
          "Disable/destroy returned while a startup thread still owned this.");
    gate.Release();
    Check(completion.wait_for(5s) == std::future_status::ready, "Disable/destroy deadlocked with startup.");
    completion.get();
    Check(starts == previousStarts + 1 && stops == previousStops + 1 && !workerRunning,
          "Startup and shutdown did not finish exactly once.");
    if (!destroy)
    {
        Check(!module->is_enabled(), "Disable left the module enabled.");
        module->disable();
        module->destroy();
    }
    Check(closes == previousCloses + 1, "Destroy did not close the module handle.");
    startupGate = nullptr;
}

int main()
{
    std::mutex timeoutMutex;
    std::condition_variable_any timeoutSignal;
    std::jthread watchdog([&](std::stop_token token) {
        std::unique_lock lock(timeoutMutex);
        timeoutSignal.wait_for(lock, token, 15s, [] { return false; });
        Check(token.stop_requested(), "Lifecycle smoke exceeded its 15 second deadline.");
    });
    CheckPendingStartup(false);
    CheckPendingStartup(true);
    auto* module = new AIHubModule();
    for (int iteration = 0; iteration < 200; ++iteration)
    {
        module->enable();
        module->disable();
    }
    module->destroy();
    Check(starts == 202 && stops == 202 && !workerRunning, "Repeated lifecycle left a worker running.");
    std::cout << "PASS: pending-start disable, pending-start destroy, repeated enable, and 200 restart cycles.\n";
}
