// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using System.Xml.Linq;

namespace Microsoft.PowerToys.Tools.XamlIndexBuilder
{
    public static class ModuleIconResolver
    {
        // Contract:
        // - Input: absolute path to the module XAML file
        // - Output: app-relative icon path, or null if not found
        // - Strategy: take the first SettingsCard under the page and read its HeaderIcon value
        public static string ResolveIconFromFirstSettingsCard(string xamlFilePath)
        {
            if (string.IsNullOrWhiteSpace(xamlFilePath))
            {
                return null;
            }

            try
            {
                var doc = XDocument.Load(xamlFilePath);

                // Prefer looking inside SettingsPageControl.ModuleContent to avoid picking cards in Resources/DataTemplates
                var pageControl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "SettingsPageControl");

                if (pageControl != null)
                {
                    // Locate the property element <SettingsPageControl.ModuleContent>
                    var moduleContent = pageControl
                        .Elements()
                        .FirstOrDefault(e => e.Name.LocalName.EndsWith(".ModuleContent", System.StringComparison.OrdinalIgnoreCase))
                        ?? pageControl
                            .Descendants()
                            .FirstOrDefault(e => e.Name.LocalName.EndsWith(".ModuleContent", System.StringComparison.OrdinalIgnoreCase));

                    if (moduleContent != null)
                    {
                        // Find the first SettingsCard under ModuleContent and try to read its HeaderIcon
                        var firstCardUnderModule = moduleContent
                            .Descendants()
                            .FirstOrDefault(e => e.Name.LocalName == "SettingsCard");

                        if (firstCardUnderModule != null)
                        {
                            var icon = Program.ExtractIconValue(firstCardUnderModule);
                            if (!string.IsNullOrWhiteSpace(icon))
                            {
                                return icon;
                            }
                        }
                    }
                }

                return null;
            }
            catch
            {
                // Non-fatal: let caller decide fallback
                return null;
            }
        }
    }
}
