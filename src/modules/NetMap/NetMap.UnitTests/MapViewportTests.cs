// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetMapLib;

namespace NetMap.UnitTests;

[TestClass]
public sealed class MapViewportTests
{
    [DataTestMethod]
    [DataRow(320, 268)]
    [DataRow(480, 268)]
    [DataRow(1200, 268)]
    [DataRow(320, 600)]
    public void PacificRouteFitsTheActualViewportWithReadableMarkerPadding(int width, int height)
    {
        MapCoordinate[] route = [new(118.78, 32.06), new(-118.24, 34.05), new(-96.80, 32.78), new(103.82, 1.35)];
        var viewport = MapViewport.Fit(route, width, height);
        foreach (var coordinate in route)
        {
            AssertInside(viewport, coordinate);
        }

        Assert.IsTrue(viewport.Position(route[0]).X < viewport.Position(route[1]).X, "Pacific crossing places China west of the US.");
    }

    [TestMethod]
    public void ResizingRefitsWithoutScalingMarkerPadding()
    {
        MapCoordinate[] route = [new(-120, 40), new(30, 40)];
        var narrow = MapViewport.Fit(route, 320, 268);
        var wide = MapViewport.Fit(route, 900, 268);
        Assert.IsTrue(wide.Scale > narrow.Scale);
        Assert.AreEqual(32, narrow.Position(route[0]).X, 0.001);
        Assert.AreEqual(32, wide.Position(route[0]).X, 0.001);
        Assert.AreEqual(288, narrow.Position(route[1]).X, 0.001);
        Assert.AreEqual(868, wide.Position(route[1]).X, 0.001);
    }

    [TestMethod]
    public void SingleLocationAndRepeatedCityPointsUseTheSameRegionalView()
    {
        var location = new MapCoordinate(139.69, 35.68);
        var single = MapViewport.Fit([location], 480, 268);
        var repeated = MapViewport.Fit([location, location, location], 480, 268);
        var world = MapViewport.Fit([], 480, 268);
        Assert.AreEqual(single, repeated);
        Assert.IsTrue(single.Scale > world.Scale * 4, "One located hop should show its region.");
        Assert.AreEqual(240, single.Position(location).X, 0.001);
        AssertInside(single, location);
    }

    [TestMethod]
    public void AdjacentDatelinePointsRemainAdjacentAndReorderingDoesNotMoveTheView()
    {
        MapCoordinate[] points = [new(179, 10), new(-179, 11)];
        var viewport = MapViewport.Fit(points, 480, 268);
        Assert.IsTrue(Math.Abs(viewport.Position(points[0]).X - viewport.Position(points[1]).X) < 30);
        Assert.AreEqual(viewport, MapViewport.Fit(points.Reverse(), 480, 268));
        AssertInside(viewport, points[0]);
        AssertInside(viewport, points[1]);
    }

    [TestMethod]
    public void LatitudeExtremesRemainClearOfControlsAndLegend()
    {
        MapCoordinate[] points = [new(20, 89), new(30, -89), new(25, 0)];
        var viewport = MapViewport.Fit(points, 320, 268);
        foreach (var point in points)
        {
            AssertInside(viewport, point);
        }
    }

    [TestMethod]
    public void InvalidLocationsDoNotCorruptTheTransform()
    {
        MapCoordinate?[] points = [null, new(double.NaN, 0), new(0, double.PositiveInfinity), new(181, 20), new(10, 95)];
        Assert.AreEqual(MapViewport.Fit([], 480, 268), MapViewport.Fit(points, 480, 268));
        var viewport = MapViewport.Fit([], 0, double.NaN, double.PositiveInfinity);
        Assert.IsTrue(double.IsFinite(viewport.Scale) && viewport.Scale > 0);
        Assert.IsTrue(double.IsFinite(viewport.OffsetX) && double.IsFinite(viewport.OffsetY));
    }

    [TestMethod]
    public void UserZoomKeepsTheGeographicCenterAndResetIsDeterministic()
    {
        MapCoordinate[] points = [new(115, 30), new(140, 35)];
        var fitted = MapViewport.Fit(points, 480, 268);
        var zoomed = MapViewport.Fit(points, 480, 268, 2);
        Assert.AreEqual(fitted.CenterX, zoomed.CenterX);
        Assert.AreEqual(fitted.Scale * 2, zoomed.Scale);
        Assert.AreEqual(fitted, MapViewport.Fit(points, 480, 268, 1));
    }

    private static void AssertInside(MapViewport viewport, MapCoordinate coordinate)
    {
        var point = viewport.Position(coordinate);
        Assert.IsTrue(point.X >= 31.99 && point.X <= viewport.Width - 31.99, $"Longitude is clipped: {point}");
        Assert.IsTrue(point.Y >= 53.99 && point.Y <= viewport.Height - 45.99, $"Latitude is clipped: {point}");
    }
}
