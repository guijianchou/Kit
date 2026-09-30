#include <Windows.h>
#include <string>
#include <chrono>
#include <condition_variable>
#include <iostream>
#include <mutex>
#include <vector>
#include "../../../src/common/interop/two_way_pipe_message_ipc.h"

int main(int argc, char**)
{
    const bool delayedServer = argc > 1;
    const auto prefix = L"\\\\.\\pipe\\KitPipeSmoke-" + std::to_wstring(GetCurrentProcessId());
    std::mutex mutex;
    std::condition_variable received;
    std::vector<int> messages;
    bool corrupted = false;
    const auto payload = [](int index) {
        return std::to_wstring(index) + L":" + std::wstring(index % 10 == 0 ? 2048 : 1, L'\u4e2d');
    };
    TwoWayPipeMessageIPC server(prefix + L"-in", prefix + L"-out", [&](const std::wstring& message) {
        std::lock_guard lock(mutex);
        const int index = std::stoi(message);
        corrupted |= message != payload(index);
        messages.push_back(index);
        received.notify_one();
    });
    TwoWayPipeMessageIPC client(prefix + L"-out", prefix + L"-in", nullptr);
    if (!delayedServer)
    {
        server.start(nullptr);
    }
    client.start(nullptr);
    for (int i = 0; !delayedServer && i < 100 && !WaitNamedPipeW((prefix + L"-in").c_str(), 100); ++i)
    {
        Sleep(10);
    }

    constexpr int count = 1000;
    for (int i = 0; i < count; ++i)
    {
        client.send(payload(i));
    }
    if (delayedServer)
    {
        Sleep(250);
        server.start(nullptr);
    }
    std::unique_lock lock(mutex);
    received.wait_for(lock, std::chrono::seconds(45), [&] { return messages.size() == count; });
    int reordered = 0;
    for (size_t i = 1; i < messages.size(); ++i)
    {
        reordered += messages[i] < messages[i - 1];
    }
    std::cout << "DelayedServer=" << delayedServer << " Sent=" << count << " Received=" << messages.size()
              << " Reordered=" << reordered << " Corrupted=" << corrupted << std::endl;
    const int result = messages.size() == count && reordered == 0 && !corrupted ? 0 : 1;
    lock.unlock();
    client.end();
    server.end();
    return result;
}
