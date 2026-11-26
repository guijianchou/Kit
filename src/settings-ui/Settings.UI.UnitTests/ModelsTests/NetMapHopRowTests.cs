// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using Kit.Settings.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetMapLib;

namespace ModelsTests;

[TestClass]
[DoNotParallelize]
public sealed class NetMapHopRowTests
{
    private static readonly string[] MapProperties = ["MapHeading", "MapLocation", "MapAsn", "MapMetrics", "MapLabel"];

    [TestMethod]
    public void MapLabelIncludesIdentityLocationAsnAndMeasurements()
    {
        var row = new NetMapHopRow(7);
        row.Update(new HopResult(7, "198.51.100.7", 10, 8, 42.5, 40.0, false, new GeoInfo(64500, "Example Network", "Seattle", "US", Region: "Washington")), true);

        Assert.AreEqual("# 7 · 198.51.100.7", row.MapHeading);
        StringAssert.Contains(row.MapLocation, "Seattle");
        StringAssert.Contains(row.MapLocation, "Washington");
        Assert.AreEqual("AS64500 · Example Network", row.MapAsn);
        StringAssert.Contains(row.MapMetrics, row.Last);
        StringAssert.Contains(row.MapMetrics, row.Rtt);
        StringAssert.Contains(row.MapMetrics, row.Loss);
        CollectionAssert.AreEqual(
            new[] { row.MapHeading, row.MapLocation, row.MapAsn, row.MapMetrics },
            row.MapLabel.Split(Environment.NewLine, StringSplitOptions.None));
        StringAssert.Contains(row.Detail, row.MapAsn);
    }

    [TestMethod]
    [DataRow("Pending", "LocPending", "Asn_Pending")]
    [DataRow("Private", "LocPrivate", "Asn_Private")]
    [DataRow("RateLimited", "LocLimited", "Asn_RateLimited")]
    [DataRow("LocalUnavailable", "UnknownLocation", "Asn_LocalUnavailable")]
    [DataRow("Ready", "UnknownLocation", "Asn_NotFound")]
    [DataRow("UnexpectedStatus", "UnknownLocation", "Asn_NotFound")]
    public void UnknownGeographyAndAsnShowExplicitLocalizedStatus(string status, string locationKey, string asnKey)
    {
        var row = new NetMapHopRow(1);
        row.Update(new HopResult(1, "198.51.100.1", 1, 1, 1, 1, false, new GeoInfo(LookupStatus: status)), true);

        Assert.AreEqual(NetMapViewModel.Text(locationKey), row.MapLocation);
        Assert.AreEqual(NetMapViewModel.Text(asnKey), row.MapAsn);
        Assert.IsFalse(string.IsNullOrWhiteSpace(row.MapLocation));
        Assert.IsFalse(string.IsNullOrWhiteSpace(row.MapAsn));
    }

    [TestMethod]
    public void LateGeographyRefreshesAllMapFieldsWithoutChangingMeasurements()
    {
        var row = new NetMapHopRow(4);
        var pending = new HopResult(4, "198.51.100.4", 10, 9, 33, 30, false, new GeoInfo(LookupStatus: "Pending"));
        row.Update(pending, true);
        string measurements = row.MapMetrics;
        var changed = new HashSet<string>(StringComparer.Ordinal);
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is { } property)
            {
                changed.Add(property);
            }
        };

        row.Update(pending with { Geography = new GeoInfo(64501, "Transit Network", "Singapore", "SG", LookupStatus: "Ready") }, true);

        Assert.AreEqual(measurements, row.MapMetrics);
        Assert.AreEqual("AS64501 · Transit Network", row.MapAsn);
        StringAssert.Contains(row.MapLocation, "Singapore");
        foreach (string property in MapProperties)
        {
            Assert.IsTrue(changed.Contains(property), property + " must refresh when lookup finishes.");
        }

        row.Update(pending with { Ip = string.Empty, Geography = null, LastMs = null, ConsecutiveFailures = 3 }, true);
        Assert.AreEqual(NetMapViewModel.Text("UnknownLocation"), row.MapLocation);
        Assert.AreEqual(NetMapViewModel.Text("Asn_NotFound"), row.MapAsn);
        Assert.IsFalse(row.MapLabel.Contains("64501", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LiveLanguageAndNumberCultureRefreshMapContentAndBindings()
    {
        CultureInfo originalUi = CultureInfo.CurrentUICulture;
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var row = new NetMapHopRow(3);
            row.Update(new HopResult(3, "198.51.100.3", 2, 2, 12.5, 12.5, false, new GeoInfo(64502, "Example", CountryCode: "US")), true);
            string englishLocation = row.MapLocation;
            string englishMetrics = row.MapMetrics;
            var changed = new HashSet<string>(StringComparer.Ordinal);
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is { } property)
                {
                    changed.Add(property);
                }
            };

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            row.RefreshLocalization();

            Assert.AreNotEqual(englishLocation, row.MapLocation);
            Assert.AreNotEqual(englishMetrics, row.MapMetrics);
            StringAssert.Contains(row.MapMetrics, "12,5");
            Assert.AreEqual("AS64502 · Example", row.MapAsn, "ASN is an identifier and must not acquire locale separators.");
            foreach (string property in MapProperties)
            {
                Assert.IsTrue(changed.Contains(property), property + " must refresh after a language change.");
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUi;
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
