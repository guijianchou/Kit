// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.PowerToys.Settings.UI.Library
{
    public sealed class EnabledModulesJsonConverter : JsonConverter<EnabledModules>
    {
        private const string AwakeKey = "Awake";
        private const string LightSwitchKey = "LightSwitch";
        private const string MonitorKey = "Monitor";
        private const string PowerDisplayKey = "PowerDisplay";

        public override EnabledModules Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException();
            }

            var modules = new EnabledModules();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return modules;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException();
                }

                string propertyName = reader.GetString() ?? string.Empty;
                if (!reader.Read())
                {
                    throw new JsonException();
                }

                if (reader.TokenType != JsonTokenType.True && reader.TokenType != JsonTokenType.False)
                {
                    reader.Skip();
                    continue;
                }

                bool isEnabled = reader.GetBoolean();
                switch (propertyName)
                {
                    case AwakeKey:
                        modules.Awake = isEnabled;
                        break;
                    case LightSwitchKey:
                        modules.LightSwitch = isEnabled;
                        break;
                    case MonitorKey:
                        modules.Monitor = isEnabled;
                        break;
                    case PowerDisplayKey:
                        modules.PowerDisplay = isEnabled;
                        break;
                }
            }

            throw new JsonException();
        }

        public override void Write(Utf8JsonWriter writer, EnabledModules value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(value);

            writer.WriteStartObject();
            writer.WriteBoolean(AwakeKey, value.Awake);
            writer.WriteBoolean(LightSwitchKey, value.LightSwitch);
            writer.WriteBoolean(MonitorKey, value.Monitor);
            writer.WriteBoolean(PowerDisplayKey, value.PowerDisplay);
            writer.WriteEndObject();
        }
    }
}
