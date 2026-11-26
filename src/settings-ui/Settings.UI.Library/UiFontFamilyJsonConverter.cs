// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kit.Settings.UI.Library
{
    public sealed class UiFontFamilyJsonConverter : JsonConverter<string>
    {
        public override bool HandleNull => true;

        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && reader.GetString() == GeneralSettings.NotoSansScUiFontFamily)
            {
                return GeneralSettings.NotoSansScUiFontFamily;
            }

            reader.Skip();
            return GeneralSettings.DefaultUiFontFamily;
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value == GeneralSettings.NotoSansScUiFontFamily ? value : GeneralSettings.DefaultUiFontFamily);
        }
    }
}
