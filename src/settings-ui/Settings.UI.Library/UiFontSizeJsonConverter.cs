// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kit.Settings.UI.Library
{
    public sealed class UiFontSizeJsonConverter : JsonConverter<int>
    {
        public override bool HandleNull => true;

        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double size) &&
                size >= GeneralSettings.MinimumUiFontSize && size <= GeneralSettings.MaximumUiFontSize && size == Math.Truncate(size))
            {
                return (int)size;
            }

            reader.Skip();
            return GeneralSettings.DefaultUiFontSize;
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value >= GeneralSettings.MinimumUiFontSize && value <= GeneralSettings.MaximumUiFontSize
                ? value : GeneralSettings.DefaultUiFontSize);
        }
    }
}
