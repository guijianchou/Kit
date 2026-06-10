// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.PowerToys.Monitor.Tests;

[TestClass]
public sealed class MonitorCategoryResolverTests
{
    [TestMethod]
    public void ResolveCategoryUsesSmartRuleBeforeExtension()
    {
        MonitorSettings settings = MonitorSettings.CreateDefault();

        string category = MonitorCategoryResolver.ResolveCategory("screenshot.exe", settings);

        Assert.AreEqual("Pictures", category);
    }

    [TestMethod]
    public void ResolveCategoryFallsBackToExtensionAndOthers()
    {
        MonitorSettings settings = MonitorSettings.CreateDefault();

        Assert.AreEqual("Documents", MonitorCategoryResolver.ResolveCategory("notes.pdf", settings));
        Assert.AreEqual("Others", MonitorCategoryResolver.ResolveCategory("unknown.custom", settings));
    }
}
