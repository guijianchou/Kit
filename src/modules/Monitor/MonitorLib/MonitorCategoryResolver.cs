// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.RegularExpressions;

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Resolves Monitor categories from file names.
/// </summary>
public static class MonitorCategoryResolver
{
    /// <summary>
    /// Resolves the configured Monitor category for a file name.
    /// </summary>
    /// <param name="fileName">The file name or path to categorize.</param>
    /// <param name="settings">The Monitor settings containing smart rules and extension maps.</param>
    /// <returns>The configured category, or <c>Others</c> when no rule matches.</returns>
    public static string ResolveCategory(string fileName, MonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(settings);

        string normalizedFileName = Path.GetFileName(fileName);

        foreach (MonitorSmartRule rule in settings.SmartRules)
        {
            if (WildcardMatches(normalizedFileName, rule.Pattern))
            {
                return rule.Category;
            }
        }

        string extension = Path.GetExtension(normalizedFileName);
        foreach (KeyValuePair<string, IReadOnlyList<string>> category in settings.Categories)
        {
            if (category.Value.Any(value => string.Equals(value, extension, StringComparison.OrdinalIgnoreCase)))
            {
                return category.Key;
            }
        }

        return "Others";
    }

    private static bool WildcardMatches(string value, string pattern)
    {
        string escapedPattern = Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal);
        return Regex.IsMatch(value, "^" + escapedPattern + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
