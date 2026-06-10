#pragma once

#include <Windows.h>

void PeriodicUpdateWorker();
void CheckForUpdatesCallback();

namespace cmdArg
{
    // Legacy updater arguments retained only for the standalone upstream updater source.
    const inline wchar_t* UPDATE_NOW_LAUNCH_STAGE1 = L"-update_now";
    const inline wchar_t* UPDATE_NOW_LAUNCH_STAGE2 = L"-update_now_stage_2";
    const inline wchar_t* UPDATE_STAGE2_RESTART_PT = L"restart";
    const inline wchar_t* UPDATE_STAGE2_DONT_START_PT = L"dont_start";
    const inline wchar_t* UPDATE_REPORT_SUCCESS = L"-report_update_success";
}

SHELLEXECUTEINFOW LaunchPowerToysUpdate(const wchar_t* cmdline);
