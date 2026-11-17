// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Common.Search.FuzzSearch;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Settings.UI.UnitTests.ModelsTests;

[TestClass]
public sealed class SearchInputRegressionTests
{
    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t\r\n")]
    [DataRow("\u00A0")]
    public void EmptyOrWhitespaceQueryNeverMatches(string query)
    {
        Assert.IsFalse(StringMatcher.FuzzyMatch(query, "Awake settings").Success);
    }

    [TestMethod]
    public void OrdinarySearchStillMatches()
    {
        Assert.IsTrue(StringMatcher.FuzzyMatch("awake", "Awake settings").Success);
    }
}
