// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Text.Json;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetMapLib;

namespace ModelsTests;

[TestClass]
public class NetMapAiServiceTests
{
    [TestMethod]
    public void ParseResponse_ValidJson_ReturnsParsedResult()
    {
        string json = """
            {
              "lineType": "CMI",
              "summary": "经过 223.120.* 移动国际骨干网直连香港出口",
              "details": "国内端直接交由 CMI AS58453 处理，无绕美情况。"
            }
            """;

        var result = NetMapAiService.ParseResponse(json, isChinese: true);

        Assert.AreEqual("CMI", result.LineType);
        Assert.AreEqual("经过 223.120.* 移动国际骨干网直连香港出口", result.Summary);
        Assert.AreEqual("国内端直接交由 CMI AS58453 处理，无绕美情况。", result.Details);
    }

    [TestMethod]
    public void ParseResponse_MarkdownCodeBlock_StripsFencesAndParses()
    {
        string markdown = """
            ```json
            {
              "lineType": "CN2 GIA",
              "summary": "China Telecom CN2 GIA premium line",
              "details": "Dual-direction 59.43.* hops observed at domestic and international border."
            }
            ```
            """;

        var result = NetMapAiService.ParseResponse(markdown, isChinese: false);

        Assert.AreEqual("CN2 GIA", result.LineType);
        Assert.AreEqual("Premium line", result.Summary);
        Assert.AreEqual("Dual-direction 59.43.* hops observed at domestic and international border.", result.Details);
    }

    [TestMethod]
    public void SanitizeSummary_CompactsVerboseSentencesAndRemovesRepeats()
    {
        string verbose = "The route most likely uses China Mobile International (CMI) for cross-border transit to Singapore via Hong Kong.";
        string sanitized = NetMapAiService.SanitizeSummary(verbose, "CMI");
        Assert.AreEqual("Singapore via Hong Kong.", sanitized);

        string withPrefix = "该链路主要使用 223.120.* 直连香港出口";
        string sanitizedZh = NetMapAiService.SanitizeSummary(withPrefix, "CMI");
        Assert.AreEqual("223.120.* 直连香港出口", sanitizedZh);
    }

    [TestMethod]
    public void ParseResponse_HeuristicFallback_DetectsRouteKeywords()
    {
        var cmi = NetMapAiService.ParseResponse("经过分析，该跨境路由属于中国移动 CMI 香港直连线路。", isChinese: true);
        Assert.AreEqual("CMI", cmi.LineType);

        var cn2Gia = NetMapAiService.ParseResponse("Routing trace shows CN2 GIA bidirectional transit with low latency.", isChinese: false);
        Assert.AreEqual("CN2 GIA", cn2Gia.LineType);

        var cn2Gt = NetMapAiService.ParseResponse("出口阶段切入 CN2 GT 节点。", isChinese: true);
        Assert.AreEqual("CN2 GT", cn2Gt.LineType);

        var b163 = NetMapAiService.ParseResponse("通过电信 163 骨干网出境。", isChinese: true);
        Assert.AreEqual("163 骨干网", b163.LineType);

        var cu9929 = NetMapAiService.ParseResponse("Matched China Unicom 9929 enterprise routing.", isChinese: false);
        Assert.AreEqual("CU 9929", cu9929.LineType);

        var cu4837 = NetMapAiService.ParseResponse("普通联通 4837 回国链路。", isChinese: true);
        Assert.AreEqual("联通 4837", cu4837.LineType);

        var bgp = NetMapAiService.ParseResponse("Standard international direct routing without special acceleration.", isChinese: false);
        Assert.AreEqual("BGP / Direct", bgp.LineType);
    }

    [TestMethod]
    public void ParseResponse_EmptyOrNull_ReturnsUnknown()
    {
        var zh = NetMapAiService.ParseResponse(string.Empty, isChinese: true);
        Assert.AreEqual("未知链路", zh.LineType);

        var en = NetMapAiService.ParseResponse(null!, isChinese: false);
        Assert.AreEqual("Unknown", en.LineType);
    }

    [TestMethod]
    public void TraceData_SourceGeneratedJson_RoundTrip()
    {
        var hops = new List<NetMapMtrHopItem>
        {
            new(1, "192.168.1.1", "局域网", string.Empty, 0.0, 1.2, 1.5, 0.8, 3.1),
            new(2, "223.120.2.1", "香港", "AS58453 CMI", 0.0, 18.5, 19.0, 18.2, 22.0),
        };

        var trace = new NetMapMtrTraceData(
            DateTimeOffset.UtcNow,
            "1.1.1.1",
            "香港",
            "AS13335 Cloudflare",
            "114.114.114.114",
            "中国上海",
            "AS4134 中国电信",
            hops);

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(trace, NetMapAiJsonContext.Default.NetMapMtrTraceData);
        Assert.IsNotNull(bytes);
        Assert.IsTrue(bytes.Length > 0);

        var deserialized = JsonSerializer.Deserialize(bytes, NetMapAiJsonContext.Default.NetMapMtrTraceData);
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("1.1.1.1", deserialized.TargetIp);
        Assert.AreEqual("114.114.114.114", deserialized.DirectIp);
        Assert.AreEqual(2, deserialized.Hops.Count);
        Assert.AreEqual("223.120.2.1", deserialized.Hops[1].Ip);
        Assert.AreEqual("AS58453 CMI", deserialized.Hops[1].Asn);
    }

    [TestMethod]
    public void NetMapCard_PreservesAiRouteProperties()
    {
        var channel = new ChannelState();
        var cardWithAi = new NetMapCard("Proxy", channel, running: true, aiRoute: "CMI", aiRouteSummary: "Direct peering", aiRouteDetails: "AS58453");

        Assert.AreEqual("CMI", cardWithAi.AiRoute);
        Assert.AreEqual("Direct peering", cardWithAi.AiRouteSummary);
        Assert.AreEqual("AS58453", cardWithAi.AiRouteDetails);

        var cardDefault = new NetMapCard("Direct", channel, running: true);
        Assert.IsNull(cardDefault.AiRoute);
        Assert.IsNull(cardDefault.AiRouteSummary);
        Assert.IsNull(cardDefault.AiRouteDetails);
    }
}
