#pragma once

#include <Windows.h>
#include <string>

namespace powertoys_gpo
{
    enum gpo_rule_configured_t
    {
        gpo_rule_configured_wrong_value = -3, // The policy is set to an unrecognized value
        gpo_rule_configured_unavailable = -2, // Couldn't access registry
        gpo_rule_configured_not_configured = -1, // Policy is not configured
        gpo_rule_configured_disabled = 0, // Policy is disabled
        gpo_rule_configured_enabled = 1, // Policy is enabled
    };

    const std::wstring POLICIES_PATH = L"SOFTWARE\\Policies\\PowerToys";
    const HKEY POLICIES_SCOPE_MACHINE = HKEY_LOCAL_MACHINE;
    const HKEY POLICIES_SCOPE_USER = HKEY_CURRENT_USER;

    const std::wstring POLICY_CONFIGURE_ENABLED_GLOBAL_ALL_UTILITIES = L"ConfigureGlobalUtilityEnabledState";
    const std::wstring POLICY_CONFIGURE_ENABLED_AWAKE = L"ConfigureEnabledUtilityAwake";
    const std::wstring POLICY_CONFIGURE_ENABLED_LIGHT_SWITCH = L"ConfigureEnabledUtilityLightSwitch";
    const std::wstring POLICY_CONFIGURE_ENABLED_POWER_DISPLAY = L"ConfigureEnabledUtilityPowerDisplay";

    const std::wstring POLICY_DISABLE_AUTOMATIC_UPDATE_DOWNLOAD = L"AutomaticUpdateDownloadDisabled";
    const std::wstring POLICY_DISABLE_NEW_UPDATE_TOAST = L"DisableNewUpdateAvailableToast";
    const std::wstring POLICY_DISABLE_SHOW_WHATS_NEW_AFTER_UPDATES = L"DoNotShowWhatsNewAfterUpdates";
    const std::wstring POLICY_ALLOW_EXPERIMENTATION = L"AllowExperimentation";
    const std::wstring POLICY_ALLOW_DATA_DIAGNOSTICS = L"AllowDataDiagnostics";
    const std::wstring POLICY_CONFIGURE_RUN_AT_STARTUP = L"ConfigureRunAtStartup";

    inline gpo_rule_configured_t getConfiguredValue(const std::wstring& registry_value_name)
    {
        HKEY key{};
        DWORD value = 0xFFFFFFFE;
        DWORD valueSize = sizeof(value);

        bool machine_key_found = true;
        if (auto res = RegOpenKeyExW(POLICIES_SCOPE_MACHINE, POLICIES_PATH.c_str(), 0, KEY_READ, &key); res != ERROR_SUCCESS)
        {
            machine_key_found = false;
        }

        if (machine_key_found)
        {
            auto res = RegQueryValueExW(key, registry_value_name.c_str(), nullptr, nullptr, reinterpret_cast<LPBYTE>(&value), &valueSize);
            RegCloseKey(key);

            if (res != ERROR_SUCCESS)
            {
                machine_key_found = false;
            }
        }

        if (!machine_key_found)
        {
            if (auto res = RegOpenKeyExW(POLICIES_SCOPE_USER, POLICIES_PATH.c_str(), 0, KEY_READ, &key); res != ERROR_SUCCESS)
            {
                if (res == ERROR_FILE_NOT_FOUND)
                {
                    return gpo_rule_configured_not_configured;
                }

                return gpo_rule_configured_unavailable;
            }

            auto res = RegQueryValueExW(key, registry_value_name.c_str(), nullptr, nullptr, reinterpret_cast<LPBYTE>(&value), &valueSize);
            RegCloseKey(key);

            if (res != ERROR_SUCCESS)
            {
                return gpo_rule_configured_not_configured;
            }
        }

        switch (value)
        {
        case 0:
            return gpo_rule_configured_disabled;
        case 1:
            return gpo_rule_configured_enabled;
        default:
            return gpo_rule_configured_wrong_value;
        }
    }

    inline gpo_rule_configured_t getUtilityEnabledValue(const std::wstring& utility_name)
    {
        auto individual_value = getConfiguredValue(utility_name);

        if (individual_value == gpo_rule_configured_disabled || individual_value == gpo_rule_configured_enabled)
        {
            return individual_value;
        }

        return getConfiguredValue(POLICY_CONFIGURE_ENABLED_GLOBAL_ALL_UTILITIES);
    }

    inline gpo_rule_configured_t getConfiguredAwakeEnabledValue()
    {
        return getUtilityEnabledValue(POLICY_CONFIGURE_ENABLED_AWAKE);
    }

    inline gpo_rule_configured_t getConfiguredLightSwitchEnabledValue()
    {
        return getUtilityEnabledValue(POLICY_CONFIGURE_ENABLED_LIGHT_SWITCH);
    }

    inline gpo_rule_configured_t getConfiguredPowerDisplayEnabledValue()
    {
        return getUtilityEnabledValue(POLICY_CONFIGURE_ENABLED_POWER_DISPLAY);
    }

    inline gpo_rule_configured_t getDisableAutomaticUpdateDownloadValue()
    {
        return getConfiguredValue(POLICY_DISABLE_AUTOMATIC_UPDATE_DOWNLOAD);
    }

    inline gpo_rule_configured_t getDisableNewUpdateToastValue()
    {
        return getConfiguredValue(POLICY_DISABLE_NEW_UPDATE_TOAST);
    }

    inline gpo_rule_configured_t getDisableShowWhatsNewAfterUpdatesValue()
    {
        return getConfiguredValue(POLICY_DISABLE_SHOW_WHATS_NEW_AFTER_UPDATES);
    }

    inline gpo_rule_configured_t getAllowExperimentationValue()
    {
        return getConfiguredValue(POLICY_ALLOW_EXPERIMENTATION);
    }

    inline gpo_rule_configured_t getAllowDataDiagnosticsValue()
    {
        return getConfiguredValue(POLICY_ALLOW_DATA_DIAGNOSTICS);
    }

    inline gpo_rule_configured_t getConfiguredRunAtStartupValue()
    {
        return getConfiguredValue(POLICY_CONFIGURE_RUN_AT_STARTUP);
    }
}
