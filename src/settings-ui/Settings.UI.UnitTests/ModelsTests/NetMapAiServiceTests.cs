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
    public void RouteSchema_ValidatesModelResult()
    {
        var schema = NetMapAiService.CreateRouteSchema();
        var itemIds = new HashSet<string>(StringComparer.Ordinal);

        Assert.IsTrue(schema.ValidateOutput(new NetMapAiRouteResult("CMI", "223.120.* 直连香港", "AS58453 承载，无绕美。"), itemIds));
        Assert.IsFalse(schema.ValidateOutput(new NetMapAiRouteResult(string.Empty, "223.120.* 直连香港", "详情"), itemIds));
        Assert.IsFalse(schema.ValidateOutput(new NetMapAiRouteResult("CMI", string.Empty, "详情"), itemIds));
        Assert.IsFalse(schema.ValidateOutput(new NetMapAiRouteResult("CMI", new string('x', 121), "详情"), itemIds));
        Assert.IsFalse(schema.ValidateOutput(new NetMapAiRouteResult("CMI", "短句", new string('x', 601)), itemIds));
    }

    [TestMethod]
    public void RouteClassificationInstruction_IsSingleLineAndLanguageAware()
    {
        string instructionZh = NetMapAiService.GetRouteInstruction(true);
        Assert.IsFalse(instructionZh.Contains('\n'));
        Assert.IsTrue(instructionZh.Length is > 0 and <= 512);

        string instructionEn = NetMapAiService.GetRouteInstruction(false);
        Assert.IsFalse(instructionEn.Contains('\n'));
        Assert.IsTrue(instructionEn.Length is > 0 and <= 512);

        string current = NetMapAiService.RouteClassificationInstruction;
        Assert.IsFalse(current.Contains('\n'));
        Assert.IsTrue(current.Length is > 0 and <= 512);
    }

    [TestMethod]
    public void SelectLanguageSegment_PicksCorrectSideForBilingualText()
    {
        string bilingual = "中国移动 CMI / China Mobile CMI";
        Assert.AreEqual("China Mobile CMI", NetMapAiService.SelectLanguageSegment(bilingual, isChinese: false));
        Assert.AreEqual("中国移动 CMI", NetMapAiService.SelectLanguageSegment(bilingual, isChinese: true));

        string summaryBilingual = "香港转接，约 67 ms / Via Hong Kong, approx 67 ms";
        Assert.AreEqual("Via Hong Kong, approx 67 ms", NetMapAiService.SelectLanguageSegment(summaryBilingual, isChinese: false));
        Assert.AreEqual("香港转接，约 67 ms", NetMapAiService.SelectLanguageSegment(summaryBilingual, isChinese: true));
    }

    [TestMethod]
    public void SanitizeLineType_NormalizesAndFollowsLanguage()
    {
        // Bilingual slash input
        string bilingual = "中国移动 CMI / China Mobile CMI";
        Assert.AreEqual("CMI", NetMapAiService.SanitizeLineType(bilingual, isChinese: false));
        Assert.AreEqual("移动 CMI", NetMapAiService.SanitizeLineType(bilingual, isChinese: true));

        // Long carrier names
        Assert.AreEqual("CMI", NetMapAiService.SanitizeLineType("China Mobile International (CMI)", isChinese: false));
        Assert.AreEqual("CN2 GIA", NetMapAiService.SanitizeLineType("China Telecom CN2 GIA", isChinese: false));
        Assert.AreEqual("163 Net", NetMapAiService.SanitizeLineType("China Telecom 163", isChinese: false));
        Assert.AreEqual("CU 9929", NetMapAiService.SanitizeLineType("China Unicom 9929", isChinese: false));

        Assert.AreEqual("移动 CMI", NetMapAiService.SanitizeLineType("中国移动 CMI", isChinese: true));
        Assert.AreEqual("电信 CN2 GIA", NetMapAiService.SanitizeLineType("中国电信 CN2 GIA", isChinese: true));
        Assert.AreEqual("电信 163", NetMapAiService.SanitizeLineType("中国电信 163", isChinese: true));
        Assert.AreEqual("联通 9929", NetMapAiService.SanitizeLineType("中国联通 9929", isChinese: true));
    }

    [TestMethod]
    public void SanitizeDetails_SelectsCorrectLanguage()
    {
        string detailsBilingual = "关键 IP 223.120.3.197 (AS58453 CMI) / Key IP 223.120.3.197 (AS58453 CMI) via Hong Kong";
        Assert.AreEqual("Key IP 223.120.3.197 (AS58453 CMI) via Hong Kong", NetMapAiService.SanitizeDetails(detailsBilingual, isChinese: false));
        Assert.AreEqual("关键 IP 223.120.3.197 (AS58453 CMI)", NetMapAiService.SanitizeDetails(detailsBilingual, isChinese: true));
    }

    [TestMethod]
    public void RouteResult_JsonContext_RejectsUnmappedMembers()
    {
        string valid = """{"lineType":"CMI","summary":"Direct via AS58453","details":"223.120.* hops observed."}""";
        var parsed = JsonSerializer.Deserialize(valid, NetMapAiJsonContext.Default.NetMapAiRouteResult);
        Assert.IsNotNull(parsed);
        Assert.AreEqual("CMI", parsed.LineType);

        string unmapped = """{"lineType":"CMI","summary":"Direct","details":"d","extra":"unexpected"}""";
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize(unmapped, NetMapAiJsonContext.Default.NetMapAiRouteResult));
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
