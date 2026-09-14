// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Awake.ModuleServices.UnitTests;

[TestClass]
public sealed class AwakeProcessIdentityTests
{
    [TestMethod]
    public void IsAwakeProcessRunning_RecognizesTheRequestedExecutable()
    {
        using var currentProcess = Process.GetCurrentProcess();
        string executablePath = currentProcess.MainModule!.FileName;

        Assert.IsTrue(AwakeService.IsAwakeProcessRunning(executablePath));
    }

    [TestMethod]
    public void IsAwakeProcessRunning_RejectsTheSameNameFromAnotherInstallation()
    {
        using var currentProcess = Process.GetCurrentProcess();
        string executablePath = currentProcess.MainModule!.FileName;
        string otherInstallationPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), Path.GetFileName(executablePath));

        Assert.IsFalse(AwakeService.IsAwakeProcessRunning(otherInstallationPath));
    }
}
