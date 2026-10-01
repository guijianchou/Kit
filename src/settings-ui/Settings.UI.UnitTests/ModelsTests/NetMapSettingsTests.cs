// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Helpers;
using ManagedCommon;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ModelsTests;

[TestClass]
public class NetMapSettingsTests
{
    [TestMethod]
    public void EnabledDefaultsOffAndOmittedRunnerKeyDoesNotResetIt()
    {
        var enabled = new EnabledModules();
        Assert.IsFalse(enabled.NetMap);
        enabled.NetMap = true;
        var partial = JsonSerializer.Deserialize("{\"UDPtest\":false}", SettingsSerializationContext.Default.EnabledModules);
        enabled.MergeFrom(partial);
        Assert.IsTrue(enabled.NetMap);
        var explicitOff = JsonSerializer.Deserialize("{\"NetMap\":false}", SettingsSerializationContext.Default.EnabledModules);
        enabled.MergeFrom(explicitOff);
        Assert.IsFalse(enabled.NetMap);
    }

    [TestMethod]
    public void NetMapSettingsAndIpcUseGeneratedMetadata()
    {
        var settings = new NetMapSettings();
        settings.Properties.ProxyMode = new IntProperty(1);
        settings.Properties.ProxyAddress = new StringProperty("socks5://localhost:1080");
        Assert.IsTrue(settings.Properties.OnlineLookup.Value);
        settings.Properties.OnlineLookup = new BoolProperty(false);
        var clone = JsonSerializer.Deserialize(settings.ToJsonString(), SettingsSerializationContext.Default.NetMapSettings);
        Assert.AreEqual(1, clone.Properties.ProxyMode.Value);
        Assert.AreEqual(settings.Properties.ProxyAddress.Value, clone.Properties.ProxyAddress.Value);
        Assert.IsFalse(clone.Properties.OnlineLookup.Value);
        var ipc = new SndModuleSettings<SndNetMapSettings>(new SndNetMapSettings { Settings = settings });
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ipc, SettingsSerializationContext.Default.SndModuleSettingsSndNetMapSettings));
        Assert.AreEqual("NetMap", document.RootElement.GetProperty("powertoys").GetProperty("NetMap").GetProperty("name").GetString());
    }

    [TestMethod]
    public void NetMapCatalogAndHelperAgree()
    {
        Assert.IsTrue(KitModuleCatalog.IsActiveModule(ModuleType.NetMap));
        var general = new GeneralSettings();
        ModuleHelper.SetIsModuleEnabled(general, ModuleType.NetMap, true);
        Assert.IsTrue(general.Enabled.NetMap);
        Assert.IsTrue(ModuleHelper.GetIsModuleEnabled(general, ModuleType.NetMap));
    }
}
