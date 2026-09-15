// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO.Abstractions;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Interfaces;
using Moq;

namespace Kit.Settings.UI.UnitTests.Mocks
{
    internal static class ISettingsUtilsMocks
    {
        // Stubs out empty values for imageresizersettings and general settings as needed by the imageresizer view model
        internal static Mock<SettingsUtils> GetStubSettingsUtils<T>()
            where T : ISettingsConfig, new()
        {
            var settingsUtils = new Mock<SettingsUtils>(new FileSystem(), null);
            settingsUtils
                .Setup(x => x.GetSettingsOrDefault<T>(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new T());

            settingsUtils
                .Setup(x => x.GetSettings<T>(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new T());

            return settingsUtils;
        }
    }
}
