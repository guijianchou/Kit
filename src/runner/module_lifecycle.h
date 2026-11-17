#pragma once
#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <deque>
#include <functional>
#include <mutex>
#include <thread>

// Each module has one owned executor. No callback runs while the queue mutex is held.
// UI reads desired()/running(); module code only executes on this worker.
class ModuleLifecycle
{
public:
    struct Callbacks
    {
        std::function<void()> initialize;
        std::function<void()> uninitialize;
        std::function<void()> enable;
        std::function<void()> disable;
        std::function<bool()> running;
        std::function<void(bool)> refresh;
        std::function<void()> failed;
    };

    explicit ModuleLifecycle(Callbacks callbacks,
                             std::chrono::milliseconds poll = std::chrono::seconds(1),
                             std::chrono::milliseconds retry = std::chrono::seconds(1)) :
        callbacks(std::move(callbacks)), pollInterval(poll), initialRetry(retry), retryDelay(retry),
        worker([this] { run(); })
    {
    }

    ~ModuleLifecycle() { shutdown(); }
    ModuleLifecycle(const ModuleLifecycle&) = delete;
    ModuleLifecycle& operator=(const ModuleLifecycle&) = delete;

    bool desired() const noexcept { return wanted.load(); }
    bool running() const noexcept { return observed.load(); }

    bool request(bool enabled)
    {
        std::lock_guard lock(gate);
        if (stopping)
            return false;
        // Only the latest pending enable/disable matters. Config/actions retain FIFO order.
        std::erase_if(commands, [](const Command& command) { return command.reconcile; });
        if (commands.size() >= maximumPendingCommands)
            return false;
        commands.push_back({ true, {} });
        wanted.store(enabled);
        wake.notify_one();
        return true;
    }

    bool submit(std::function<void()> action)
    {
        std::lock_guard lock(gate);
        if (stopping || commands.size() >= maximumPendingCommands)
            return false;
        commands.push_back({ false, std::move(action) });
        wake.notify_one();
        return true;
    }

    void begin_shutdown() noexcept
    {
        std::lock_guard lock(gate);
        stopping = true;
        wanted.store(false);
        wake.notify_one();
    }

    void shutdown() noexcept
    {
        begin_shutdown();
        // Destruction is an explicit drain barrier; no detached DLL code survives it.
        if (worker.joinable())
            worker.join();
    }

private:
    using Clock = std::chrono::steady_clock;
    struct Command
    {
        bool reconcile;
        std::function<void()> action;
    };
    static constexpr size_t maximumPendingCommands = 256;
    Callbacks callbacks;
    const std::chrono::milliseconds pollInterval;
    const std::chrono::milliseconds initialRetry;
    std::chrono::milliseconds retryDelay;
    std::mutex gate;
    std::condition_variable wake;
    std::deque<Command> commands;
    std::atomic<bool> wanted{ false };
    std::atomic<bool> observed{ false };
    bool stopping = false;
    Clock::time_point retryAt{};
    Clock::time_point stableSince{};
    std::thread worker;

    void failed() noexcept
    {
        try { if (callbacks.failed) callbacks.failed(); }
        catch (...) { }
    }

    void refresh(bool configurationChanged) noexcept
    {
        try
        {
            observed.store(callbacks.running());
            if (callbacks.refresh) callbacks.refresh(configurationChanged);
        }
        catch (...)
        {
            observed.store(false);
            failed();
        }
    }

    bool reconcile(bool explicitRequest)
    {
        if (!wanted.load())
        {
            callbacks.disable();
            observed.store(false);
            retryDelay = initialRetry;
            stableSince = {};
            return true;
        }

        const auto now = Clock::now();
        if (callbacks.running())
        {
            observed.store(true);
            if (stableSince == Clock::time_point{}) stableSince = now;
            if (now - stableSince >= std::chrono::seconds(30)) retryDelay = initialRetry;
            return false;
        }
        observed.store(false);
        stableSince = {};
        if (explicitRequest) retryAt = now;
        if (now < retryAt || !wanted.load()) return false;

        // Schedule the next attempt before entering module code, including if it throws.
        retryAt = now + retryDelay;
        retryDelay = (std::min)(retryDelay * 2, std::chrono::milliseconds(60000));
        callbacks.enable();
        observed.store(callbacks.running());
        if (observed.load()) stableSince = now;
        return true;
    }

    void run() noexcept
    {
        bool initialized = false;
        try
        {
            if (callbacks.initialize) callbacks.initialize();
            initialized = true;
            auto nextPoll = Clock::now() + pollInterval;
            for (;;)
            {
                Command command{};
                bool hasCommand = false;
                {
                    std::unique_lock lock(gate);
                    wake.wait_until(lock, nextPoll, [this] { return stopping || !commands.empty(); });
                    if (!commands.empty())
                    {
                        command = std::move(commands.front());
                        commands.pop_front();
                        hasCommand = true;
                    }
                    else if (stopping)
                    {
                        break;
                    }
                }
                bool configurationChanged = hasCommand;
                try
                {
                    if (hasCommand)
                    {
                        if (command.reconcile) reconcile(true);
                        else command.action();
                    }
                    else if (wanted.load())
                    {
                        configurationChanged = reconcile(false);
                    }
                }
                catch (...) { failed(); }
                refresh(configurationChanged);
                nextPoll = Clock::now() + pollInterval;
            }
            try { callbacks.disable(); }
            catch (...) { failed(); }
            observed.store(false);
        }
        catch (...)
        {
            wanted.store(false);
            observed.store(false);
            {
                std::lock_guard lock(gate);
                stopping = true;
                commands.clear();
            }
            failed();
        }
        if (initialized && callbacks.uninitialize)
        {
            try { callbacks.uninitialize(); }
            catch (...) { }
        }
    }
};
