// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Reflection;
using System.Text.Json;
using Kit.NetMapService;
using ManagedCommon.ServiceHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NetMap.UnitTests;

[TestClass]
public sealed class ServiceCommandTests
{
    [TestMethod]
    [DataRow("get-topology")]
    [DataRow("start-scan")]
    [DataRow("cancel-scan")]
    [DataRow("scan-host-ports")]
    [DataRow("send-wol")]
    [DataRow("run-traceroute")]
    [DataRow("START-SCAN")]
    public async Task UnimplementedNetworkCommandsCannotReportSuccess(string command)
    {
        using var host = new NetMapServiceHost();
        KitPipeResponse response = await SendAsync(host, command);

        Assert.IsFalse(response.Success);
        Assert.IsNull(response.DataJson);
        Assert.AreEqual("unsupported-command", response.Error?.Code);
    }

    [TestMethod]
    public async Task HostStatusDoesNotClaimThatTheNetworkEngineIsAvailable()
    {
        using var host = new NetMapServiceHost();
        KitPipeResponse response = await SendAsync(host, "get-status");

        Assert.IsTrue(response.Success);
        using JsonDocument data = JsonDocument.Parse(response.DataJson!);
        Assert.AreEqual("running", data.RootElement.GetProperty("status").GetString());
        Assert.IsFalse(data.RootElement.GetProperty("engineAvailable").GetBoolean());
        Assert.AreEqual("Settings", data.RootElement.GetProperty("executionHost").GetString());
        Assert.AreEqual("unknown-command", (await SendAsync(host, "not-a-command")).Error?.Code);
    }

    private static Task<KitPipeResponse> SendAsync(NetMapServiceHost host, string command)
    {
        // Exercise the real dispatcher without starting a worker or touching user settings.
        MethodInfo handler = typeof(NetMapServiceHost).GetMethod("HandlePipeCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<KitPipeResponse>)handler.Invoke(host, [new KitPipeRequest { Command = command }, CancellationToken.None])!;
    }
}
