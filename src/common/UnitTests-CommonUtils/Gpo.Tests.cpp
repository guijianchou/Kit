#include "pch.h"
#include "TestHelpers.h"
#include <gpo.h>

using namespace Microsoft::VisualStudio::CppUnitTestFramework;
using namespace powertoys_gpo;

namespace UnitTestsCommonUtils
{
    TEST_CLASS(GpoTests)
    {
    public:
        TEST_METHOD(GpoRuleConfigured_EnumValues_PreserveCompatibility)
        {
            Assert::AreEqual(-3, static_cast<int>(gpo_rule_configured_wrong_value));
            Assert::AreEqual(-2, static_cast<int>(gpo_rule_configured_unavailable));
            Assert::AreEqual(-1, static_cast<int>(gpo_rule_configured_not_configured));
            Assert::AreEqual(0, static_cast<int>(gpo_rule_configured_disabled));
            Assert::AreEqual(1, static_cast<int>(gpo_rule_configured_enabled));
        }

        TEST_METHOD(GetConfiguredValue_AlwaysReturnsNotConfiguredForKit)
        {
            for (const auto* policyName : { L"", L"NonExistentPolicyValue12345", L"ConfigureEnabledUtilityAwake", L"ConfigureEnabledUtilityLightSwitch", L"ConfigureRunAtStartup" })
            {
                Assert::AreEqual(-1, static_cast<int>(getConfiguredValue(policyName)));
                Assert::AreEqual(-1, static_cast<int>(getUtilityEnabledValue(policyName)));
            }
        }

        TEST_METHOD(CompatibilityPolicyReaders_AllReturnNotConfigured)
        {
            for (const auto readPolicy : { getAllowExperimentationValue,
                                           getAllowDataDiagnosticsValue,
                                           getConfiguredAwakeEnabledValue,
                                           getConfiguredLightSwitchEnabledValue,
                                           getConfiguredRunAtStartupValue,
                                           getDisableAutomaticUpdateDownloadValue,
                                           getDisableNewUpdateToastValue,
                                           getDisableShowWhatsNewAfterUpdatesValue })
            {
                Assert::AreEqual(-1, static_cast<int>(readPolicy()));
            }
        }
    };
}
