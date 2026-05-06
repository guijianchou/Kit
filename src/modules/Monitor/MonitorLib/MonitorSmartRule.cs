// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Describes a filename wildcard rule that maps matching files to a Monitor category.
/// </summary>
/// <param name="Pattern">The wildcard pattern to match against a file name.</param>
/// <param name="Category">The category assigned when the pattern matches.</param>
public sealed record MonitorSmartRule(string Pattern, string Category);
