#include "pch.h"
#include "auto_start_helper.h"

#include <Lmcons.h>

#include <comdef.h>
#include <taskschd.h>
#include <common/logger/logger.h>

// Helper macros from wix.
#define ExitOnFailure(x, s, ...) \
    if (FAILED(x))               \
    {                            \
        Logger::error(s, ##__VA_ARGS__); \
        goto LExit;              \
    }
#define ExitWithLastError(x, s, ...)       \
    {                                      \
        DWORD util_err = ::GetLastError(); \
        x = HRESULT_FROM_WIN32(util_err);  \
        if (!FAILED(x))                    \
        {                                  \
            x = E_FAIL;                    \
        }                                  \
        Logger::error(s, ##__VA_ARGS__);   \
        goto LExit;                        \
    }
#define ExitFunction() \
    {                  \
        goto LExit;    \
    }

const DWORD USERNAME_DOMAIN_LEN = DNLEN + UNLEN + 2; // Domain Name + '\' + User Name + '\0'
const DWORD USERNAME_LEN = UNLEN + 1; // User Name + '\0'
const wchar_t KIT_TASK_SCHEDULER_FOLDER[] = L"\\Kit";

std::wstring get_auto_start_task_name_for_this_user()
{
    WCHAR username[USERNAME_LEN];
    if (!GetEnvironmentVariable(L"USERNAME", username, USERNAME_LEN))
    {
        return {};
    }

    std::wstring task_name = L"Autorun for ";
    task_name += username;
    return task_name;
}

constexpr bool is_missing_task_scheduler_item(HRESULT hr)
{
    return hr == HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) || hr == HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND);
}

struct AutoStartTaskSettings
{
    std::wstring executablePath;
    std::wstring arguments;
    TASK_RUNLEVEL_TYPE runLevel = TASK_RUNLEVEL_LUA;
    bool enabled = false;
};

static HRESULT read_kit_auto_start_task(IRegisteredTask* task, AutoStartTaskSettings& settings)
{
    winrt::com_ptr<ITaskDefinition> task_definition;
    winrt::com_ptr<IActionCollection> action_collection;
    winrt::com_ptr<IAction> action;
    winrt::com_ptr<IExecAction> exec_action;
    winrt::com_ptr<IPrincipal> principal;
    wil::unique_bstr action_path;
    wil::unique_bstr arguments;
    LONG action_count = 0;
    VARIANT_BOOL enabled = VARIANT_FALSE;

    HRESULT hr = task->get_Definition(task_definition.put());
    if (FAILED(hr))
    {
        return hr;
    }
    hr = task_definition->get_Actions(action_collection.put());
    if (FAILED(hr))
    {
        return hr;
    }
    hr = action_collection->get_Count(&action_count);
    if (FAILED(hr))
    {
        return hr;
    }
    if (action_count != 1)
    {
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }
    hr = action_collection->get_Item(1, action.put());
    if (FAILED(hr))
    {
        return hr;
    }
    hr = action->QueryInterface(IID_PPV_ARGS(exec_action.put()));
    if (FAILED(hr))
    {
        return hr;
    }
    hr = exec_action->get_Path(action_path.put());
    if (FAILED(hr))
    {
        return hr;
    }
    if (!action_path || _wcsicmp(PathFindFileNameW(action_path.get()), L"Kit.exe") != 0)
    {
        // A name collision must not make Kit replace or delete another program's task.
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    }
    hr = exec_action->get_Arguments(arguments.put());
    if (FAILED(hr))
    {
        return hr;
    }
    hr = task_definition->get_Principal(principal.put());
    if (FAILED(hr))
    {
        return hr;
    }
    hr = principal->get_RunLevel(&settings.runLevel);
    if (FAILED(hr))
    {
        return hr;
    }
    hr = task->get_Enabled(&enabled);
    if (FAILED(hr))
    {
        return hr;
    }
    settings.executablePath = action_path.get();
    settings.arguments = arguments ? arguments.get() : L"";
    settings.enabled = enabled == VARIANT_TRUE;
    return S_OK;
}

bool delete_auto_start_task_for_this_user()
{
    HRESULT hr = S_OK;
    std::wstring task_name = get_auto_start_task_name_for_this_user();

    ITaskService* pService = NULL;
    ITaskFolder* pTaskFolder = NULL;
    IRegisteredTask* pExistingRegisteredTask = NULL;

    if (task_name.empty())
    {
        ExitWithLastError(hr, "Getting username failed: {:x}", hr);
    }

    hr = CoCreateInstance(CLSID_TaskScheduler,
                          NULL,
                          CLSCTX_INPROC_SERVER,
                          IID_ITaskService,
                          reinterpret_cast<void**>(&pService));
    ExitOnFailure(hr, "Failed to create an instance of ITaskService: {:x}", hr);

    hr = pService->Connect(_variant_t(), _variant_t(), _variant_t(), _variant_t());
    ExitOnFailure(hr, "ITaskService::Connect failed: {:x}", hr);

    hr = pService->GetFolder(_bstr_t(KIT_TASK_SCHEDULER_FOLDER), &pTaskFolder);
    if (FAILED(hr))
    {
        if (is_missing_task_scheduler_item(hr))
        {
            hr = S_OK;
        }
        ExitFunction();
    }

    hr = pTaskFolder->GetTask(_bstr_t(task_name.c_str()), &pExistingRegisteredTask);
    if (SUCCEEDED(hr))
    {
        AutoStartTaskSettings existing;
        hr = read_kit_auto_start_task(pExistingRegisteredTask, existing);
        ExitOnFailure(hr, "Refusing to delete an unreadable or unrelated Kit startup task: {:x}", hr);
        hr = pTaskFolder->DeleteTask(_bstr_t(task_name.c_str()), 0);
    }
    else if (is_missing_task_scheduler_item(hr))
    {
        hr = S_OK;
    }

LExit:
    if (pExistingRegisteredTask)
        pExistingRegisteredTask->Release();
    if (pService)
        pService->Release();
    if (pTaskFolder)
        pTaskFolder->Release();

    return (SUCCEEDED(hr));
}

bool create_auto_start_task_for_this_user(bool runElevated)
{
    HRESULT hr = S_OK;

    WCHAR username_domain[USERNAME_DOMAIN_LEN];
    WCHAR username[USERNAME_LEN];

    std::wstring wstrTaskName;

    ITaskService* pService = NULL;
    ITaskFolder* pTaskFolder = NULL;
    ITaskDefinition* pTask = NULL;
    IRegistrationInfo* pRegInfo = NULL;
    ITaskSettings* pSettings = NULL;
    ITriggerCollection* pTriggerCollection = NULL;
    IRegisteredTask* pRegisteredTask = NULL;

    // ------------------------------------------------------
    // Get the Domain/Username for the trigger.
    if (!GetEnvironmentVariable(L"USERNAME", username, USERNAME_LEN))
    {
        ExitWithLastError(hr, "Getting username failed: {:x}", hr);
    }
    if (!GetEnvironmentVariable(L"USERDOMAIN", username_domain, USERNAME_DOMAIN_LEN))
    {
        ExitWithLastError(hr, "Getting the user's domain failed: {:x}", hr);
    }
    wcscat_s(username_domain, L"\\");
    wcscat_s(username_domain, username);

    // Task Name.
    wstrTaskName = L"Autorun for ";
    wstrTaskName += username;

    // Get the executable path passed to the custom action.
    WCHAR wszExecutablePath[MAX_PATH];
    {
        const DWORD path_length = GetModuleFileNameW(nullptr, wszExecutablePath, MAX_PATH);
        if (path_length == 0 || path_length >= MAX_PATH)
        {
            ExitWithLastError(hr, "Cannot obtain the Kit startup executable path: {:x}", hr);
        }
    }

    // ------------------------------------------------------
    // Create an instance of the Task Service.
    hr = CoCreateInstance(CLSID_TaskScheduler,
                          NULL,
                          CLSCTX_INPROC_SERVER,
                          IID_ITaskService,
                          reinterpret_cast<void**>(&pService));
    ExitOnFailure(hr, "Failed to create an instance of ITaskService: {:x}", hr);

    // Connect to the task service.
    hr = pService->Connect(_variant_t(), _variant_t(), _variant_t(), _variant_t());
    ExitOnFailure(hr, "ITaskService::Connect failed: {:x}", hr);

    // ------------------------------------------------------
    // Get the Kit task folder. Creates it if it doesn't exist.
    hr = pService->GetFolder(_bstr_t(KIT_TASK_SCHEDULER_FOLDER), &pTaskFolder);
    if (FAILED(hr))
    {
        if (!is_missing_task_scheduler_item(hr))
        {
            ExitOnFailure(hr, "Cannot access Kit task folder: {:x}", hr);
        }
        // Folder doesn't exist. Get the Root folder and create the Kit subfolder.
        ITaskFolder* pRootFolder = NULL;
        hr = pService->GetFolder(_bstr_t(L"\\"), &pRootFolder);
        ExitOnFailure(hr, "Cannot get Root Folder pointer: {:x}", hr);
        hr = pRootFolder->CreateFolder(_bstr_t(KIT_TASK_SCHEDULER_FOLDER), _variant_t(L""), &pTaskFolder);
        pRootFolder->Release();
        ExitOnFailure(hr, "Cannot create Kit task folder: {:x}", hr);
    }

    // Read before writing. An unchanged task must not be re-registered at every launch.
    {
        winrt::com_ptr<IRegisteredTask> existing_task;
        hr = pTaskFolder->GetTask(_bstr_t(wstrTaskName.c_str()), existing_task.put());
        if (SUCCEEDED(hr))
        {
            AutoStartTaskSettings existing;
            hr = read_kit_auto_start_task(existing_task.get(), existing);
            ExitOnFailure(hr, "Refusing to replace an unreadable or unrelated Kit startup task: {:x}", hr);
            const auto desired_run_level = runElevated ? TASK_RUNLEVEL_HIGHEST : TASK_RUNLEVEL_LUA;
            if (_wcsicmp(existing.executablePath.c_str(), wszExecutablePath) == 0 &&
                existing.arguments == L"--autorun" && existing.runLevel == desired_run_level)
            {
                if (!existing.enabled)
                {
                    hr = existing_task->put_Enabled(VARIANT_TRUE);
                    ExitOnFailure(hr, "Cannot enable Kit startup task: {:x}", hr);
                }
                ExitFunction();
            }
        }
        else if (!is_missing_task_scheduler_item(hr))
        {
            ExitOnFailure(hr, "Cannot read Kit startup task: {:x}", hr);
        }
    }

    // Create the task builder object to create the task.
    hr = pService->NewTask(0, &pTask);
    ExitOnFailure(hr, "Failed to create a task definition: {:x}", hr);

    // ------------------------------------------------------
    // Get the registration info for setting the identification.
    hr = pTask->get_RegistrationInfo(&pRegInfo);
    ExitOnFailure(hr, "Cannot get identification pointer: {:x}", hr);
    hr = pRegInfo->put_Author(_bstr_t(username_domain));
    ExitOnFailure(hr, "Cannot put identification info: {:x}", hr);

    // ------------------------------------------------------
    // Create the settings for the task
    hr = pTask->get_Settings(&pSettings);
    ExitOnFailure(hr, "Cannot get settings pointer: {:x}", hr);

    hr = pSettings->put_StartWhenAvailable(VARIANT_FALSE);
    ExitOnFailure(hr, "Cannot put_StartWhenAvailable setting info: {:x}", hr);
    hr = pSettings->put_StopIfGoingOnBatteries(VARIANT_FALSE);
    ExitOnFailure(hr, "Cannot put_StopIfGoingOnBatteries setting info: {:x}", hr);
    hr = pSettings->put_ExecutionTimeLimit(_bstr_t(L"PT0S")); //Unlimited
    ExitOnFailure(hr, "Cannot put_ExecutionTimeLimit setting info: {:x}", hr);
    hr = pSettings->put_DisallowStartIfOnBatteries(VARIANT_FALSE);
    ExitOnFailure(hr, "Cannot put_DisallowStartIfOnBatteries setting info: {:x}", hr);
    hr = pSettings->put_Priority(4);
    ExitOnFailure(hr, "Cannot put_Priority setting info : {:x}", hr);

    // ------------------------------------------------------
    // Get the trigger collection to insert the logon trigger.
    hr = pTask->get_Triggers(&pTriggerCollection);
    ExitOnFailure(hr, "Cannot get trigger collection: {:x}", hr);

    // Add the logon trigger to the task.
    {
        ITrigger* pTrigger = NULL;
        ILogonTrigger* pLogonTrigger = NULL;
        hr = pTriggerCollection->Create(TASK_TRIGGER_LOGON, &pTrigger);
        ExitOnFailure(hr, "Cannot create the trigger: {:x}", hr);

        hr = pTrigger->QueryInterface(
            IID_ILogonTrigger, reinterpret_cast<void**>(&pLogonTrigger));
        pTrigger->Release();
        ExitOnFailure(hr, "QueryInterface call failed for ILogonTrigger: {:x}", hr);

        hr = pLogonTrigger->put_Id(_bstr_t(L"Trigger1"));

        // Timing issues may make explorer not be started when the task runs.
        // Add a little delay to mitigate this.
        hr = pLogonTrigger->put_Delay(_bstr_t(L"PT03S"));

        // Define the user. The task will execute when the user logs on.
        // The specified user must be a user on this computer.
        hr = pLogonTrigger->put_UserId(_bstr_t(username_domain));
        pLogonTrigger->Release();
        ExitOnFailure(hr, "Cannot add user ID to logon trigger: {:x}", hr);
    }

    // ------------------------------------------------------
    // Add an Action to the task. This task will execute the path passed to this custom action.
    {
        IActionCollection* pActionCollection = NULL;
        IAction* pAction = NULL;
        IExecAction* pExecAction = NULL;

        // Get the task action collection pointer.
        hr = pTask->get_Actions(&pActionCollection);
        ExitOnFailure(hr, "Cannot get Task collection pointer: {:x}", hr);

        // Create the action, specifying that it is an executable action.
        hr = pActionCollection->Create(TASK_ACTION_EXEC, &pAction);
        pActionCollection->Release();
        ExitOnFailure(hr, "Cannot create the action: {:x}", hr);

        // QI for the executable task pointer.
        hr = pAction->QueryInterface(
            IID_IExecAction, reinterpret_cast<void**>(&pExecAction));
        pAction->Release();
        ExitOnFailure(hr, "QueryInterface call failed for IExecAction: {:x}", hr);

        // Set the path of the executable to Kit.
        hr = pExecAction->put_Path(_bstr_t(wszExecutablePath));
        ExitOnFailure(hr, "Cannot set path of executable: {:x}", hr);

        hr = pExecAction->put_Arguments(_bstr_t(L"--autorun"));
        pExecAction->Release();
        ExitOnFailure(hr, "Cannot set arguments of executable: {:x}", hr);
    }

    // ------------------------------------------------------
    // Create the principal for the task
    {
        IPrincipal* pPrincipal = NULL;
        hr = pTask->get_Principal(&pPrincipal);
        ExitOnFailure(hr, "Cannot get principal pointer: {:x}", hr);

        // Set up principal information:
        hr = pPrincipal->put_Id(_bstr_t(L"Principal1"));

        hr = pPrincipal->put_UserId(_bstr_t(username_domain));

        hr = pPrincipal->put_LogonType(TASK_LOGON_INTERACTIVE_TOKEN);

        if (runElevated)
        {
            hr = pPrincipal->put_RunLevel(_TASK_RUNLEVEL::TASK_RUNLEVEL_HIGHEST);
        }
        else
        {
            hr = pPrincipal->put_RunLevel(_TASK_RUNLEVEL::TASK_RUNLEVEL_LUA);
        }
        pPrincipal->Release();
        ExitOnFailure(hr, "Cannot put principal run level: {:x}", hr);
    }
    // ------------------------------------------------------
    //  Save the task in the Kit folder.
    {
        _variant_t SDDL_FULL_ACCESS_FOR_EVERYONE = L"D:(A;;FA;;;WD)";
        hr = pTaskFolder->RegisterTaskDefinition(
            _bstr_t(wstrTaskName.c_str()),
            pTask,
            TASK_CREATE_OR_UPDATE,
            _variant_t(username_domain),
            _variant_t(),
            TASK_LOGON_INTERACTIVE_TOKEN,
            SDDL_FULL_ACCESS_FOR_EVERYONE,
            &pRegisteredTask);
        ExitOnFailure(hr, "Error saving the Task : {:x}", hr);
    }

LExit:
    if (pService)
        pService->Release();
    if (pTaskFolder)
        pTaskFolder->Release();
    if (pTask)
        pTask->Release();
    if (pRegInfo)
        pRegInfo->Release();
    if (pSettings)
        pSettings->Release();
    if (pTriggerCollection)
        pTriggerCollection->Release();
    if (pRegisteredTask)
        pRegisteredTask->Release();

    return (SUCCEEDED(hr));
}

bool is_auto_start_task_active_for_this_user()
{
    HRESULT hr = S_OK;

    WCHAR username[USERNAME_LEN];
    std::wstring wstrTaskName;

    ITaskService* pService = NULL;
    ITaskFolder* pTaskFolder = NULL;

    // ------------------------------------------------------
    // Get the Username for the task.
    if (!GetEnvironmentVariable(L"USERNAME", username, USERNAME_LEN))
    {
        ExitWithLastError(hr, "Getting username failed: {:x}", hr);
    }

    // Task Name.
    wstrTaskName = L"Autorun for ";
    wstrTaskName += username;

    // ------------------------------------------------------
    // Create an instance of the Task Service.
    hr = CoCreateInstance(CLSID_TaskScheduler,
                          NULL,
                          CLSCTX_INPROC_SERVER,
                          IID_ITaskService,
                          reinterpret_cast<void**>(&pService));
    ExitOnFailure(hr, "Failed to create an instance of ITaskService: {:x}", hr);

    // Connect to the task service.
    hr = pService->Connect(_variant_t(), _variant_t(), _variant_t(), _variant_t());
    ExitOnFailure(hr, "ITaskService::Connect failed: {:x}", hr);

    // ------------------------------------------------------
    // Get the Kit task folder.
    hr = pService->GetFolder(_bstr_t(KIT_TASK_SCHEDULER_FOLDER), &pTaskFolder);
    if (FAILED(hr))
    {
        if (is_missing_task_scheduler_item(hr))
        {
            hr = S_FALSE;
            ExitFunction();
        }
        ExitOnFailure(hr, "ITaskFolder doesn't exist: {:x}", hr);
    }

    // ------------------------------------------------------
    // Only report a task belonging to Kit as an active startup entry.
    {
        winrt::com_ptr<IRegisteredTask> existing_task;
        hr = pTaskFolder->GetTask(_bstr_t(wstrTaskName.c_str()), existing_task.put());
        if (SUCCEEDED(hr))
        {
            AutoStartTaskSettings existing;
            hr = read_kit_auto_start_task(existing_task.get(), existing);
            if (SUCCEEDED(hr))
            {
                hr = existing.enabled ? S_OK : S_FALSE;
                ExitFunction();
            }
        }
        else if (is_missing_task_scheduler_item(hr))
        {
            hr = S_FALSE;
            ExitFunction();
        }
    }

LExit:
    if (pService)
        pService->Release();
    if (pTaskFolder)
        pTaskFolder->Release();

    return hr == S_OK;
}
