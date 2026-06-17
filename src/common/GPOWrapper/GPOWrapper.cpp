#include "pch.h"
#include "GPOWrapper.h"
#include "GPOWrapper.g.cpp"

namespace winrt::PowerToys::GPOWrapper::implementation
{
    GpoRuleConfigured GPOWrapper::GetConfiguredAwakeEnabledValue()
    {
        return static_cast<GpoRuleConfigured>(powertoys_gpo::getConfiguredAwakeEnabledValue());
    }
    GpoRuleConfigured GPOWrapper::GetConfiguredLightSwitchEnabledValue()
    {
        return static_cast<GpoRuleConfigured>(powertoys_gpo::getConfiguredLightSwitchEnabledValue());
    }
    GpoRuleConfigured GPOWrapper::GetDisableNewUpdateToastValue()
    {
        return static_cast<GpoRuleConfigured>(powertoys_gpo::getDisableNewUpdateToastValue());
    }
    GpoRuleConfigured GPOWrapper::GetDisableAutomaticUpdateDownloadValue()
    {
        return static_cast<GpoRuleConfigured>(powertoys_gpo::getDisableAutomaticUpdateDownloadValue());
    }
    GpoRuleConfigured GPOWrapper::GetDisableShowWhatsNewAfterUpdatesValue()
    {
        return static_cast<GpoRuleConfigured>(powertoys_gpo::getDisableShowWhatsNewAfterUpdatesValue());
    }
    GpoRuleConfigured GPOWrapper::GetAllowExperimentationValue()
    {
        return static_cast<GpoRuleConfigured>(powertoys_gpo::getAllowExperimentationValue());
    }
    GpoRuleConfigured GPOWrapper::GetAllowDataDiagnosticsValue()
    {
        return static_cast<GpoRuleConfigured>(powertoys_gpo::getAllowDataDiagnosticsValue());
    }
    GpoRuleConfigured GPOWrapper::GetConfiguredRunAtStartupValue()
    {
        return static_cast<GpoRuleConfigured>(powertoys_gpo::getConfiguredRunAtStartupValue());
    }
}
