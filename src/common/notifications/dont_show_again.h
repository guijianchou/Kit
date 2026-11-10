#pragma once

#include "../utils/timeutil.h"

namespace notifications
{
    const inline wchar_t ElevatedDontShowAgainRegistryPath[] = LR"(SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DontShowMeThisDialogAgain\{e16ea82f-6d94-4f30-bb02-d6d911588afd})";
    const inline int64_t ElevatedDisableIntervalInDays = 30;

    bool disable_toast(const wchar_t* registry_path);
    bool is_toast_disabled(const wchar_t* registry_path, const int64_t disable_interval_in_days);
}
