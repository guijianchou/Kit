// Exercises unchanged production lifecycle method bodies through Runner's queue.
// Worker callbacks/handles are inert: no DLL, worker, settings or named events.
#include "../../../src/runner/module_lifecycle.h"
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdlib>
#include <future>
#include <iostream>
#include <memory>
#include <mutex>
#include <stdexcept>
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

template<class Predicate>
void Until(Predicate condition)
{
    const auto deadline = std::chrono::steady_clock::now() + 5s;
    while (!condition())
    {
        Check(std::chrono::steady_clock::now() < deadline, "Lifecycle condition timed out.");
        std::this_thread::sleep_for(1ms);
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
        Check(changed.wait_for(lock, 5s, [this] { return entered; }), "Worker startup did not reach its gate.");
    }

    void Release()
    {
        std::lock_guard lock(mutex);
        released = true;
        changed.notify_all();
    }
};

StartupGate* startupGate = nullptr;
std::atomic<int> attempts = 0;
std::atomic<int> starts = 0;
std::atomic<int> stops = 0;
std::atomic<int> closes = 0;
std::atomic<bool> workerRunning = false;
std::atomic<bool> startFails = false;

using HANDLE = const std::atomic<bool>*;
constexpr unsigned long WAIT_TIMEOUT = 258;
unsigned long WaitForSingleObject(HANDLE handle, unsigned long)
{
    return handle && handle->load() ? WAIT_TIMEOUT : 0;
}
bool CloseHandle(HANDLE) { return true; }

struct Trace
{
    static void EnableAIHub(bool) {}
};

struct Logger
{
    static void info(const wchar_t*) {}
    static void error(const wchar_t*) {}
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
    HANDLE m_worker_process = nullptr;
    HANDLE m_exit_event = nullptr;

    void start_worker_if_needed()
    {
        ++attempts;
        if (startupGate) startupGate->Wait();
        if (startFails) throw std::runtime_error("Synthetic worker launch failure");
        Check(!workerRunning.exchange(true), "Worker started twice without stopping.");
        m_worker_process = &workerRunning;
        ++starts;
    }

    void stop_worker_if_running()
    {
        if (workerRunning.exchange(false)) ++stops;
    }

    void close_worker_handle() { m_worker_process = nullptr; ++closes; }

public:
    // Generated from dllmain.cpp on every invocation, never a copied implementation.
#include "AIHubLifecycleMethods.generated.h"
};

struct QueuedModule
{
    AIHubModule* module = new AIHubModule();
    std::unique_ptr<ModuleLifecycle> lifecycle = std::make_unique<ModuleLifecycle>(
        ModuleLifecycle::Callbacks{ {}, {}, [this] { module->enable(); },
            [this] { module->disable(); }, [this] { return module->is_enabled(); }, {}, {} },
        2ms, 10ms);

    ~QueuedModule()
    {
        // Same ownership boundary as KitModule::State: join before destroy/unload.
        lifecycle.reset();
        module->destroy();
    }
};

void CheckPendingStartup(bool destroy)
{
    StartupGate gate;
    startupGate = &gate;
    auto owner = std::make_unique<QueuedModule>();
    const int previousStarts = starts.load();
    const int previousStops = stops.load();
    const int previousCloses = closes.load();
    Check(owner->lifecycle->request(true), "Enable request rejected.");
    gate.WaitUntilEntered();

    auto ui = std::async(std::launch::async, [&] {
        Check(owner->lifecycle->desired(), "Accepted enable intent was lost.");
        Check(!owner->lifecycle->running(), "Pending launch falsely reported running.");
        Check(owner->lifecycle->request(true), "Repeated enable request rejected.");
        return owner->lifecycle->request(false);
    });
    Check(ui.wait_for(200ms) == std::future_status::ready && ui.get(),
          "UI state read or disable request blocked on module startup.");
    Check(stops == previousStops, "Disable overlapped in-flight startup.");

    std::future<void> completion;
    if (destroy)
    {
        completion = std::async(std::launch::async, [&] { owner.reset(); });
        Check(completion.wait_for(200ms) == std::future_status::timeout,
              "Destroy returned while startup still owned the module.");
    }
    gate.Release();
    if (destroy)
    {
        Check(completion.wait_for(5s) == std::future_status::ready, "Destroy deadlocked with startup.");
        completion.get();
    }
    else
    {
        Until([&] { return stops == previousStops + 1 && !owner->lifecycle->running(); });
        Check(!owner->module->is_enabled(), "Disable left the module enabled.");
        owner.reset();
    }
    Check(starts == previousStarts + 1 && stops == previousStops + 1 && !workerRunning,
          "Startup and shutdown did not finish exactly once.");
    Check(closes == previousCloses + 1, "Destroy did not close the module handle.");
    startupGate = nullptr;
}

void CheckLaunchFailureAndCrashRecovery()
{
    auto owner = std::make_unique<QueuedModule>();
    startFails = true;
    const int previousAttempts = attempts.load();
    owner->lifecycle->request(true);
    Until([&] { return attempts > previousAttempts; });
    Check(!owner->lifecycle->running() && !owner->module->is_enabled(),
          "A failed process launch falsely reported running.");
    owner->lifecycle->request(false);
    startFails = false;
    owner->lifecycle->request(true);
    Until([&] { return owner->lifecycle->running(); });
    const int previousStarts = starts.load();
    owner->lifecycle->submit([&] {
        workerRunning = false;
        Check(!owner->module->is_enabled(), "A signaled worker handle remained enabled.");
    });
    Until([&] { return starts > previousStarts && owner->lifecycle->running(); });
    owner.reset();
    Check(!workerRunning, "Recovery left a worker alive after shutdown.");
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
    CheckLaunchFailureAndCrashRecovery();
    auto owner = std::make_unique<QueuedModule>();
    const int previousStarts = starts.load();
    const int previousStops = stops.load();
    for (int iteration = 0; iteration < 200; ++iteration)
    {
        owner->lifecycle->request(true);
        Until([&] { return owner->lifecycle->running(); });
        owner->lifecycle->request(false);
        Until([&] { return !owner->lifecycle->running() && !workerRunning; });
    }
    owner.reset();
    Check(starts == previousStarts + 200 && stops == previousStops + 200 && !workerRunning,
          "Repeated lifecycle lost a transition or left a worker running.");
    std::cout << "PASS: queued startup, responsive disable, joined destroy, failed launch, crash recovery and 200 restart cycles.\n";
}
