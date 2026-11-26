// The wrapper extracts record_helper_failure and RunPortableUpdate unchanged
// from the current Runner source. Only their OS, clock, and persistence seams
// are replaced: no updater process or user UpdateState.json is ever accessed.
#define NOMINMAX
#include <windows.h>
#include <crtdbg.h>
#include <ctime>
#include <cwchar>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iomanip>
#include <initializer_list>
#include <iostream>
#include <iterator>
#include <optional>
#include <stdexcept>
#include <string>
#include <utility>
#include "common/updating/updateState.h"

namespace fixture
{
    constexpr std::time_t Clock = 1700000000;
    const std::wstring Runner = L"C:\\Portable Kit\\Kit.exe";
    std::filesystem::path stateFile;
    DWORD createError = ERROR_SUCCESS;
    DWORD helperExit = 0;
    DWORD waitResult = WAIT_OBJECT_0;
    bool exitCodeReadable = true;
    int createCalls = 0;
    int waitCalls = 0;
    int exitCalls = 0;
    int storeCalls = 0;
    int badgeCalls = 0;
    int apartmentEntries = 0;
    int apartmentExits = 0;
    int handleCloses = 0;
    int passed = 0;
    std::wstring expectedAction;
    std::function<void()> helperWrite;

    void Require(bool condition, const char* message)
    {
        if (!condition) throw std::runtime_error(message);
    }

    // This adapter writes and rereads the real UpdateState fields on disk, using
    // an intentionally small fixture format. It does not test production JSON
    // serialization, locking, or the Settings file watcher.
    void WriteState(const UpdateState& state)
    {
        std::wofstream output(stateFile, std::ios::trunc);
        Require(output.is_open(), "Could not open isolated state file for writing.");
        output << static_cast<int>(state.state) << L'\n'
               << state.githubUpdateLastCheckedDate.has_value() << L' '
               << state.githubUpdateLastCheckedDate.value_or(0) << L'\n'
               << std::quoted(state.errorCode) << L'\n'
               << std::quoted(state.releasePageUrl) << L'\n'
               << std::quoted(state.downloadedInstallerFilename) << L'\n';
        output.close();
        Require(!output.fail(), "Could not persist isolated update state.");
    }

    std::string StateBytes()
    {
        std::ifstream input(stateFile, std::ios::binary);
        Require(input.is_open(), "Could not read state fixture bytes.");
        return { std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>() };
    }

    void Reset(const char* name, UpdateState seed = {})
    {
        stateFile = std::filesystem::current_path() / "fixtures" / (std::string(name) + ".state");
        std::filesystem::create_directories(stateFile.parent_path());
        WriteState(seed);
        createError = ERROR_SUCCESS;
        helperExit = 0;
        waitResult = WAIT_OBJECT_0;
        exitCodeReadable = true;
        createCalls = waitCalls = exitCalls = storeCalls = badgeCalls = 0;
        apartmentEntries = apartmentExits = handleCloses = 0;
        helperWrite = {};
        std::cout << "CASE " << name << std::endl;
    }

    UpdateState Seed(UpdateState::State status = UpdateState::upToDate)
    {
        UpdateState state;
        state.state = status;
        state.githubUpdateLastCheckedDate = Clock;
        state.releasePageUrl = L"https://github.com/guijianchou/Kit/releases/tag/9.0.0";
        state.downloadedInstallerFilename = L"Kit-9.0.0-x64.zip";
        return state;
    }

    void Passed()
    {
        ++passed;
        std::cout << "PASS " << stateFile.stem().string() << std::endl;
    }
}

UpdateState UpdateState::read()
{
    std::wifstream input(fixture::stateFile);
    fixture::Require(input.is_open(), "Could not open isolated update state.");
    UpdateState state;
    int status = 0;
    bool hasDate = false;
    std::time_t date = 0;
    input >> status >> hasDate >> date >> std::quoted(state.errorCode)
          >> std::quoted(state.releasePageUrl) >> std::quoted(state.downloadedInstallerFilename);
    fixture::Require(!input.fail(), "Isolated state did not survive disk round trip.");
    state.state = static_cast<State>(status);
    if (hasDate) state.githubUpdateLastCheckedDate = date;
    return state;
}

void UpdateState::store(std::function<void(UpdateState&)> modifier)
{
    ++fixture::storeCalls;
    auto state = read();
    modifier(state);
    fixture::WriteState(state);
}

struct Logger
{
    template<typename... T> static void error(T&&...) {}
};

namespace timeutil
{
    std::time_t now() { return fixture::Clock; }
}

namespace winrt
{
    enum class apartment_type { multi_threaded };
    void init_apartment(apartment_type) { ++fixture::apartmentEntries; }
    void uninit_apartment() { ++fixture::apartmentExits; }
}

namespace wil
{
    template<typename Callback> struct scope_exit
    {
        Callback callback;
        ~scope_exit() { callback(); }
    };
    template<typename Callback> scope_exit(Callback) -> scope_exit<Callback>;

    struct unique_handle
    {
        HANDLE value;
        HANDLE get() const { return value; }
        ~unique_handle() { if (value) ++fixture::handleCloses; }
    };
}

void update_badge() { ++fixture::badgeCalls; }

DWORD MockGetModuleFileNameW(HMODULE module, LPWSTR buffer, DWORD size)
{
    fixture::Require(module == nullptr && size > fixture::Runner.size(), "Unexpected Runner path request.");
    wcscpy_s(buffer, size, fixture::Runner.c_str());
    return static_cast<DWORD>(fixture::Runner.size());
}

DWORD MockGetCurrentProcessId() { return 42; }

BOOL MockCreateProcessW(LPCWSTR application, LPWSTR command, LPSECURITY_ATTRIBUTES processSecurity,
                       LPSECURITY_ATTRIBUTES threadSecurity, BOOL inherit, DWORD flags, LPVOID environment,
                       LPCWSTR workingDirectory, LPSTARTUPINFOW startup, LPPROCESS_INFORMATION process)
{
    ++fixture::createCalls;
    const auto root = std::filesystem::path(fixture::Runner).parent_path();
    const auto helper = root / L"Updater" / L"Kit.Updater.exe";
    const auto expected = L"\"" + helper.wstring() + L"\" " + fixture::expectedAction + L" \"" + root.wstring() + L"\" 42";
    fixture::Require(application == helper.wstring() && command == expected, "Updater path/action/quoting changed.");
    fixture::Require(workingDirectory == root.wstring(), "Updater working directory changed.");
    fixture::Require(!processSecurity && !threadSecurity && !inherit && !environment && flags == CREATE_NO_WINDOW,
                     "Unexpected updater process creation flags.");
    fixture::Require(startup && startup->cb == sizeof(STARTUPINFOW) && process, "Invalid process startup structures.");
    if (fixture::createError)
    {
        SetLastError(fixture::createError);
        return FALSE;
    }
    process->hThread = reinterpret_cast<HANDLE>(static_cast<UINT_PTR>(1));
    process->hProcess = reinterpret_cast<HANDLE>(static_cast<UINT_PTR>(2));
    return TRUE;
}

DWORD MockWaitForSingleObject(HANDLE process, DWORD timeout)
{
    ++fixture::waitCalls;
    fixture::Require(process == reinterpret_cast<HANDLE>(static_cast<UINT_PTR>(2)) && timeout == INFINITE,
                     "Unexpected updater wait request.");
    if (fixture::helperWrite) fixture::helperWrite();
    if (fixture::waitResult != WAIT_OBJECT_0) SetLastError(ERROR_INVALID_HANDLE);
    return fixture::waitResult;
}

BOOL MockGetExitCodeProcess(HANDLE process, LPDWORD exitCode)
{
    ++fixture::exitCalls;
    fixture::Require(process == reinterpret_cast<HANDLE>(static_cast<UINT_PTR>(2)) && exitCode,
                     "Unexpected updater exit-code request.");
    if (!fixture::exitCodeReadable)
    {
        SetLastError(ERROR_INVALID_HANDLE);
        return FALSE;
    }
    *exitCode = fixture::helperExit;
    return TRUE;
}

// Redirect every process-related API in the extracted production functions.
// A regression cannot accidentally start a real executable through this seam.
#define GetModuleFileNameW MockGetModuleFileNameW
#define GetCurrentProcessId MockGetCurrentProcessId
#define CreateProcessW MockCreateProcessW
#define WaitForSingleObject MockWaitForSingleObject
#define GetExitCodeProcess MockGetExitCodeProcess
#include "PortableUpdateFailure.production.inc"
#undef GetModuleFileNameW
#undef GetCurrentProcessId
#undef CreateProcessW
#undef WaitForSingleObject
#undef GetExitCodeProcess

namespace fixture
{
    void Run(const wchar_t* action)
    {
        expectedAction = action;
        const int previousCreates = createCalls;
        const int previousCloses = handleCloses;
        RunPortableUpdate(action);
        Require(createCalls == previousCreates + 1, "Expected exactly one helper launch attempt.");
        Require(apartmentEntries == apartmentExits, "COM apartment was not balanced on return.");
        Require(handleCloses == previousCloses + (createError ? 0 : 2), "Child handles were not released.");
        if (createError) Require(waitCalls == 0 && exitCalls == 0, "Failed launch entered wait/exit path.");
    }

    void ExpectFailure(const wchar_t* code, UpdateState::State status, std::time_t timestamp = Clock + 1)
    {
        const auto actual = UpdateState::read();
        Require(actual.errorCode == code, "Wrong failure code persisted.");
        Require(actual.state == status, "Wrong failure state persisted.");
        Require(actual.githubUpdateLastCheckedDate == timestamp, "Failure timestamp did not advance.");
        Require(storeCalls > 0 && badgeCalls > 0, "Failure did not persist state and refresh the badge.");
    }
}

int main()
{
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE);
    _CrtSetReportFile(_CRT_ASSERT, _CRTDBG_FILE_STDERR);
    using namespace fixture;
    try
    {
        for (const DWORD code : { 5UL, 1UL, 3UL })
        {
            Reset(("check-exit-" + std::to_string(code)).c_str(), Seed());
            helperExit = code;
            Run(L"check");
            ExpectFailure(code == 5 ? L"accessDenied" : code == 3 ? L"helperBusy" : L"helperFailed", UpdateState::networkError);
            Passed();
        }

        Reset("download-exit-1", Seed(UpdateState::readyToDownload));
        helperExit = 1;
        Run(L"download");
        ExpectFailure(L"helperFailed", UpdateState::errorDownloading);
        Passed();

        auto verified = Seed(UpdateState::readyToInstall);
        Reset("check-preserves-verified-package", verified);
        helperExit = 5;
        Run(L"check");
        ExpectFailure(L"accessDenied", UpdateState::readyToInstall);
        Require(UpdateState::read().downloadedInstallerFilename == verified.downloadedInstallerFilename &&
                UpdateState::read().releasePageUrl == verified.releasePageUrl, "Check failure discarded the verified package.");
        Passed();

        auto busy = Seed(UpdateState::downloading);
        busy.errorCode = L"existing-detail";
        Reset("status-busy-preserves-state", busy);
        const auto busyBytes = StateBytes();
        helperExit = 3;
        Run(L"status");
        Require(StateBytes() == busyBytes && storeCalls == 0 && badgeCalls == 0, "Nonblocking status probe overwrote active update state.");
        Passed();

        Reset("helper-specific-error-preserved", Seed());
        helperExit = 1;
        helperWrite = [] {
            auto state = UpdateState::read();
            state.state = UpdateState::networkError;
            state.errorCode = L"rateLimited";
            state.githubUpdateLastCheckedDate = Clock + 1;
            WriteState(state);
        };
        Run(L"check");
        ExpectFailure(L"rateLimited", UpdateState::networkError);
        Passed();

        auto stale = Seed(UpdateState::networkError);
        stale.errorCode = L"rateLimited";
        Reset("stale-error-does-not-hide-new-failure", stale);
        helperExit = 1;
        Run(L"check");
        ExpectFailure(L"helperFailed", UpdateState::networkError);
        Passed();

        Reset("same-second-repeated-manual-check", Seed());
        helperExit = 5;
        const auto firstBaseline = UpdateState::read().githubUpdateLastCheckedDate;
        Run(L"check");
        const auto secondBaseline = UpdateState::read().githubUpdateLastCheckedDate;
        Require(secondBaseline > firstBaseline, "First same-second check cannot satisfy Settings freshness comparison.");
        Run(L"check");
        ExpectFailure(L"accessDenied", UpdateState::networkError, Clock + 2);
        Require(UpdateState::read().githubUpdateLastCheckedDate > secondBaseline, "Second same-second check cannot complete in Settings.");
        Passed();

        for (const DWORD code : { ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND, ERROR_ACCESS_DENIED, ERROR_ELEVATION_REQUIRED })
        {
            Reset(("create-process-error-" + std::to_string(code)).c_str(), Seed());
            createError = code;
            Run(L"check");
            ExpectFailure(code == ERROR_ACCESS_DENIED ? L"accessDenied" :
                          code == ERROR_FILE_NOT_FOUND || code == ERROR_PATH_NOT_FOUND ? L"helperMissing" : L"helperFailed",
                          UpdateState::networkError);
            Passed();
        }

        Reset("wait-failed", Seed());
        waitResult = WAIT_FAILED;
        Run(L"check");
        ExpectFailure(L"helperFailed", UpdateState::networkError);
        Require(exitCalls == 0, "Failed wait should not read the process exit code.");
        Passed();

        Reset("exit-code-unreadable", Seed());
        exitCodeReadable = false;
        Run(L"check");
        ExpectFailure(L"helperFailed", UpdateState::networkError);
        Passed();

        Reset("success-does-not-rewrite-state", verified);
        const auto successBytes = StateBytes();
        Run(L"check");
        Require(StateBytes() == successBytes && storeCalls == 0 && badgeCalls == 1, "Successful helper result changed persisted state.");
        Passed();

        Reset("first-check-without-timestamp");
        createError = ERROR_FILE_NOT_FOUND;
        Run(L"check");
        ExpectFailure(L"helperMissing", UpdateState::networkError, Clock);
        Passed();

        std::cout << "PASS: " << passed << " isolated Runner updater cases; original functions, disk fixtures, mocked Win32/clock; no Kit process or user state." << std::endl;
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL " << stateFile.stem().string() << ": " << error.what() << std::endl;
        return 1;
    }
}
