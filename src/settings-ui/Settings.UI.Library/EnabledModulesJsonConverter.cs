// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kit.Settings.UI.Library
{
    public sealed class EnabledModulesJsonConverter : JsonConverter<EnabledModules>
    {
        private const string AwakeKey = "Awake";
        private const string LightSwitchKey = "LightSwitch";
        private const string LocalserverKey = "Localserver";
        private const string UDPtestKey = "UDPtest";
        private const string AiHubKey = "AiHub";

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
                    case LocalserverKey:
                        modules.Localserver = isEnabled;
                        break;
                    case UDPtestKey:
                        modules.UDPtest = isEnabled;
                        break;
                    case AiHubKey:
                        modules.AiHub = isEnabled;
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
            writer.WriteBoolean(LocalserverKey, value.Localserver);
            writer.WriteBoolean(UDPtestKey, value.UDPtest);
            writer.WriteBoolean(AiHubKey, value.AiHub);
            writer.WriteEndObject();
        }
    }
}
