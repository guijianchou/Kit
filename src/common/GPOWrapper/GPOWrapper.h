#pragma once
#include "GPOWrapper.g.h"
#include <common/utils/gpo.h>

namespace winrt::Kit::GPOWrapper::implementation
{
    struct GPOWrapper : GPOWrapperT<GPOWrapper>
    {
        GPOWrapper() = default;
        static GpoRuleConfigured GetConfiguredAwakeEnabledValue();
        static GpoRuleConfigured GetConfiguredLightSwitchEnabledValue();
        static GpoRuleConfigured GetDisableNewUpdateToastValue();
        static GpoRuleConfigured GetDisableAutomaticUpdateDownloadValue();
        static GpoRuleConfigured GetDisableShowWhatsNewAfterUpdatesValue();
        static GpoRuleConfigured GetAllowExperimentationValue();
        static GpoRuleConfigured GetAllowDataDiagnosticsValue();
        static GpoRuleConfigured GetConfiguredRunAtStartupValue();
    };
}

namespace winrt::Kit::GPOWrapper::factory_implementation
{
    struct GPOWrapper : GPOWrapperT<GPOWrapper, implementation::GPOWrapper>
    {
    };
}
