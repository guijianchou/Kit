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
        // Helper to check if result is a valid gpo_rule_configured_t value
        static constexpr bool IsValidGpoResult(gpo_rule_configured_t result)
        {
            return result == gpo_rule_configured_wrong_value ||
                   result == gpo_rule_configured_unavailable ||
                   result == gpo_rule_configured_not_configured ||
                   result == gpo_rule_configured_disabled ||
                   result == gpo_rule_configured_enabled;
        }

        // gpo_rule_configured_t enum tests
        TEST_METHOD(GpoRuleConfigured_EnumValues_AreDistinct)
        {
            Assert::AreNotEqual(static_cast<int>(gpo_rule_configured_not_configured),
                               static_cast<int>(gpo_rule_configured_enabled));
            Assert::AreNotEqual(static_cast<int>(gpo_rule_configured_enabled),
                               static_cast<int>(gpo_rule_configured_disabled));
            Assert::AreNotEqual(static_cast<int>(gpo_rule_configured_not_configured),
                               static_cast<int>(gpo_rule_configured_disabled));
        }

        // getConfiguredValue tests
        TEST_METHOD(GetConfiguredValue_NonExistentKey_ReturnsNotConfigured)
        {
            auto result = getConfiguredValue(L"NonExistentPolicyValue12345");
            Assert::IsTrue(result == gpo_rule_configured_not_configured ||
                          result == gpo_rule_configured_unavailable);
        }

        TEST_METHOD(GetAllowExperimentationValue_ReturnsValidState)
        {
            auto result = getAllowExperimentationValue();
            Assert::IsTrue(IsValidGpoResult(result));
        }

        TEST_METHOD(GetAllowDataDiagnosticsValue_ReturnsValidState)
        {
            auto result = getAllowDataDiagnosticsValue();
            Assert::IsTrue(IsValidGpoResult(result));
        }

        TEST_METHOD(GetConfiguredAwakeEnabledValue_ReturnsValidState)
        {
            auto result = getConfiguredAwakeEnabledValue();
            Assert::IsTrue(IsValidGpoResult(result));
        }

        TEST_METHOD(GetConfiguredLightSwitchEnabledValue_ReturnsValidState)
        {
            auto result = getConfiguredLightSwitchEnabledValue();
            Assert::IsTrue(IsValidGpoResult(result));
        }

        TEST_METHOD(GetConfiguredRunAtStartupValue_ReturnsValidState)
        {
            auto result = getConfiguredRunAtStartupValue();
            Assert::IsTrue(IsValidGpoResult(result));
        }

        TEST_METHOD(GetDisableAutomaticUpdateDownloadValue_ReturnsValidState)
        {
            auto result = getDisableAutomaticUpdateDownloadValue();
            Assert::IsTrue(IsValidGpoResult(result));
        }

        TEST_METHOD(GetDisableNewUpdateToastValue_ReturnsValidState)
        {
            auto result = getDisableNewUpdateToastValue();
            Assert::IsTrue(IsValidGpoResult(result));
        }

        TEST_METHOD(GetDisableShowWhatsNewAfterUpdatesValue_ReturnsValidState)
        {
            auto result = getDisableShowWhatsNewAfterUpdatesValue();
            Assert::IsTrue(IsValidGpoResult(result));
        }

        // All GPO functions should not crash
        TEST_METHOD(AllGpoFunctions_DoNotCrash)
        {
            getAllowExperimentationValue();
            getAllowDataDiagnosticsValue();
            getConfiguredAwakeEnabledValue();
            getConfiguredLightSwitchEnabledValue();
            getConfiguredRunAtStartupValue();
            getDisableAutomaticUpdateDownloadValue();
            getDisableNewUpdateToastValue();
            getDisableShowWhatsNewAfterUpdatesValue();

            Assert::IsTrue(true);
        }
    };
}
