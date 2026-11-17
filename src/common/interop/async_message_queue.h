#pragma once
#include <queue>
#include <thread>
#include <mutex>
#include <condition_variable>
#include <string>
#include <utility>

class AsyncMessageQueue
{
private:
    std::mutex queue_mutex;
    std::queue<std::wstring> message_queue;
    std::condition_variable message_ready;
    bool interrupted = false;

    //Disable copy
    AsyncMessageQueue(const AsyncMessageQueue&);
    AsyncMessageQueue& operator=(const AsyncMessageQueue&);

public:
    AsyncMessageQueue()
    {
    }
    void queue_message(std::wstring message)
    {
        {
            std::scoped_lock lock(this->queue_mutex);
            if (interrupted) return;
            this->message_queue.push(std::move(message));
        }
        this->message_ready.notify_one();
    }
    std::wstring pop_message()
    {
        std::unique_lock<std::mutex> lock(this->queue_mutex);
        while (message_queue.empty() && !this->interrupted)
        {
            this->message_ready.wait(lock);
        }
        if (this->interrupted)
        {
            //Just returns an empty string if the queue was interrupted.
            return std::wstring(L"");
        }
        std::wstring message = std::move(this->message_queue.front());
        this->message_queue.pop();
        return message;
    }
    void interrupt()
    {
        {
            std::scoped_lock lock(this->queue_mutex);
            this->interrupted = true;
        }
        this->message_ready.notify_all();
    }
};
