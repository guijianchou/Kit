#pragma once

#include <Windows.h>
#include <wil/resource.h>
#include <condition_variable>
#include <functional>
#include <mutex>
#include <system_error>
#include <thread>
#include <utility>

namespace QuickAccessHost::detail
{
    enum class Request { None, Start, Show, Stop };

    // One owned executor serializes process and IPC lifetime. The callback seam
    // lets behavior tests block startup/shutdown without launching a UI process.
    class LifecycleWorker
    {
    public:
        struct Callbacks
        {
            std::function<void()> start;
            std::function<void()> show; // Signals only; must not wait for process startup/exit.
            std::function<void(HANDLE)> stop;
            std::function<void()> failed;
        };

        explicit LifecycleWorker(Callbacks callbacks) : callbacks(std::move(callbacks)) { }
        ~LifecycleWorker() { shutdown(false); }
        LifecycleWorker(const LifecycleWorker&) = delete;
        LifecycleWorker& operator=(const LifecycleWorker&) = delete;

        void request(Request command)
        {
            std::scoped_lock lock(request_mutex);
            if (closing) return;
            if (!worker.joinable())
            {
                fast_shutdown_event.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
                if (!fast_shutdown_event)
                    throw std::system_error(GetLastError(), std::system_category());
                worker = std::thread([this] { run(); });
            }
            // Coalesce pending intent; never interrupt in-flight process/IPC work.
            pending = command;
            ready.notify_one();
        }

        void shutdown(bool wait_for_process) noexcept
        {
            {
                std::scoped_lock lock(request_mutex);
                closing = true;
                pending = Request::None;
                if (!wait_for_process && fast_shutdown_event)
                    SetEvent(fast_shutdown_event.get());
                ready.notify_one();
            }
            // Joining never holds the request/process locks needed by callbacks.
            // Concurrent final stops serialize their join operation here.
            std::scoped_lock join_lock(join_mutex);
            if (worker.joinable()) worker.join();
        }

    private:
        Callbacks callbacks;
        std::mutex request_mutex;
        std::mutex join_mutex;
        std::condition_variable ready;
        Request pending = Request::None;
        bool closing = false;
        wil::unique_handle fast_shutdown_event;
        std::thread worker;

        void failed() noexcept
        {
            try { if (callbacks.failed) callbacks.failed(); }
            catch (...) { }
        }

        void run() noexcept
        {
            for (;;)
            {
                Request command;
                {
                    std::unique_lock lock(request_mutex);
                    ready.wait(lock, [this] { return closing || pending != Request::None; });
                    if (closing) break;
                    command = pending;
                    pending = Request::None;
                }
                try
                {
                    if (command == Request::Stop)
                    {
                        callbacks.stop(fast_shutdown_event.get());
                    }
                    else
                    {
                        callbacks.start();
                        if (command == Request::Show)
                        {
                            std::scoped_lock request_lock(request_mutex);
                            if (!closing && pending != Request::Stop) callbacks.show();
                        }
                    }
                }
                catch (...) { failed(); }
            }
            try { callbacks.stop(fast_shutdown_event.get()); }
            catch (...) { failed(); }
        }
    };
}

namespace QuickAccessHost
{
    // Queues startup on the owned lifecycle worker.
    void start();
    // Queues a launch/show request without blocking the caller on process startup.
    void show();
    // Queues shutdown without waiting for child process or IPC cleanup.
    void request_stop();
    // Final shutdown: rejects further requests and joins the lifecycle worker.
    // Session shutdown can skip child grace periods while still joining the worker.
    void stop(bool wait_for_process = true);
    bool is_running();
}
