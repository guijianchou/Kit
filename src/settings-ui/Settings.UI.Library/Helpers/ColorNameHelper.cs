// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Drawing;

using ManagedCommon;

using ColorResources = Kit.Settings.UI.Library.Resources.Resources;

namespace Kit.Settings.UI.Library.Helpers
{
    public static class ColorNameHelper
    {
        public static string GetColorNameFromColorIdentifier(string colorIdentifier)
        {
            switch (colorIdentifier)
            {
                case "TEXT_COLOR_WHITE": return ColorResources.TEXT_COLOR_WHITE;
                case "TEXT_COLOR_BLACK": return ColorResources.TEXT_COLOR_BLACK;
                case "TEXT_COLOR_LIGHTGRAY": return ColorResources.TEXT_COLOR_LIGHTGRAY;
                case "TEXT_COLOR_GRAY": return ColorResources.TEXT_COLOR_GRAY;
                case "TEXT_COLOR_DARKGRAY": return ColorResources.TEXT_COLOR_DARKGRAY;
                case "TEXT_COLOR_CORAL": return ColorResources.TEXT_COLOR_CORAL;
                case "TEXT_COLOR_ROSE": return ColorResources.TEXT_COLOR_ROSE;
                case "TEXT_COLOR_LIGHTORANGE": return ColorResources.TEXT_COLOR_LIGHTORANGE;
                case "TEXT_COLOR_TAN": return ColorResources.TEXT_COLOR_TAN;
                case "TEXT_COLOR_LIGHTYELLOW": return ColorResources.TEXT_COLOR_LIGHTYELLOW;
                case "TEXT_COLOR_LIGHTGREEN": return ColorResources.TEXT_COLOR_LIGHTGREEN;
                case "TEXT_COLOR_LIME": return ColorResources.TEXT_COLOR_LIME;
                case "TEXT_COLOR_AQUA": return ColorResources.TEXT_COLOR_AQUA;
                case "TEXT_COLOR_SKYBLUE": return ColorResources.TEXT_COLOR_SKYBLUE;
                case "TEXT_COLOR_LIGHTTURQUOISE": return ColorResources.TEXT_COLOR_LIGHTTURQUOISE;
                case "TEXT_COLOR_PALEBLUE": return ColorResources.TEXT_COLOR_PALEBLUE;
                case "TEXT_COLOR_LIGHTBLUE": return ColorResources.TEXT_COLOR_LIGHTBLUE;
                case "TEXT_COLOR_ICEBLUE": return ColorResources.TEXT_COLOR_ICEBLUE;
                case "TEXT_COLOR_PERIWINKLE": return ColorResources.TEXT_COLOR_PERIWINKLE;
                case "TEXT_COLOR_LAVENDER": return ColorResources.TEXT_COLOR_LAVENDER;
                case "TEXT_COLOR_PINK": return ColorResources.TEXT_COLOR_PINK;
                case "TEXT_COLOR_RED": return ColorResources.TEXT_COLOR_RED;
                case "TEXT_COLOR_ORANGE": return ColorResources.TEXT_COLOR_ORANGE;
                case "TEXT_COLOR_BROWN": return ColorResources.TEXT_COLOR_BROWN;
                case "TEXT_COLOR_GOLD": return ColorResources.TEXT_COLOR_GOLD;
                case "TEXT_COLOR_YELLOW": return ColorResources.TEXT_COLOR_YELLOW;
                case "TEXT_COLOR_OLIVEGREEN": return ColorResources.TEXT_COLOR_OLIVEGREEN;
                case "TEXT_COLOR_GREEN": return ColorResources.TEXT_COLOR_GREEN;
                case "TEXT_COLOR_BRIGHTGREEN": return ColorResources.TEXT_COLOR_BRIGHTGREEN;
                case "TEXT_COLOR_TEAL": return ColorResources.TEXT_COLOR_TEAL;
                case "TEXT_COLOR_TURQUOISE": return ColorResources.TEXT_COLOR_TURQUOISE;
                case "TEXT_COLOR_BLUE": return ColorResources.TEXT_COLOR_BLUE;
                case "TEXT_COLOR_BLUEGRAY": return ColorResources.TEXT_COLOR_BLUEGRAY;
                case "TEXT_COLOR_INDIGO": return ColorResources.TEXT_COLOR_INDIGO;
                case "TEXT_COLOR_PURPLE": return ColorResources.TEXT_COLOR_PURPLE;
                case "TEXT_COLOR_DARKRED": return ColorResources.TEXT_COLOR_DARKRED;
                case "TEXT_COLOR_DARKYELLOW": return ColorResources.TEXT_COLOR_DARKYELLOW;
                case "TEXT_COLOR_DARKGREEN": return ColorResources.TEXT_COLOR_DARKGREEN;
                case "TEXT_COLOR_DARKTEAL": return ColorResources.TEXT_COLOR_DARKTEAL;
                case "TEXT_COLOR_DARKBLUE": return ColorResources.TEXT_COLOR_DARKBLUE;
                case "TEXT_COLOR_DARKPURPLE": return ColorResources.TEXT_COLOR_DARKPURPLE;
                case "TEXT_COLOR_PLUM": return ColorResources.TEXT_COLOR_PLUM;
                default: return colorIdentifier;
            }
        }

        public static string ReplaceName(string colorFormat, Color? colorOrNull)
        {
            Color color = (Color)(colorOrNull == null ? Color.Moccasin : colorOrNull);
            return colorFormat.Replace(ColorFormatHelper.GetColorNameParameter(), GetColorNameFromColorIdentifier(ManagedCommon.ColorNameHelper.GetColorNameIdentifier(color)));
        }
    }
}
