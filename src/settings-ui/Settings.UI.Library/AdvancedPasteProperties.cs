// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

using Kit.Settings.UI.Library.Attributes;

namespace Kit.Settings.UI.Library
{
    public class AdvancedPasteProperties
    {
        public AdvancedPasteProperties()
        {
            CustomActions = new();
            AdditionalActions = new();
            IsAIEnabled = false;
            ShowCustomPreview = true;
            CloseAfterLosingFocus = false;
            EnableClipboardPreview = true;
            PasteAIConfiguration = new();
        }

        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool IsAIEnabled { get; set; }

        private bool? _legacyAdvancedAIEnabled;

        [JsonPropertyName("IsAdvancedAIEnabled")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BoolProperty LegacyAdvancedAIEnabledProperty
        {
            get => null;
            set
            {
                if (value is not null)
                {
                    LegacyAdvancedAIEnabled = value.Value;
                }
            }
        }

        [JsonIgnore]
        public bool? LegacyAdvancedAIEnabled
        {
            get => _legacyAdvancedAIEnabled;
            private set => _legacyAdvancedAIEnabled = value;
        }

        public bool TryConsumeLegacyAdvancedAIEnabled(out bool value)
        {
            if (_legacyAdvancedAIEnabled is bool flag)
            {
                value = flag;
                _legacyAdvancedAIEnabled = null;
                return true;
            }

            value = default;
            return false;
        }

        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool ShowCustomPreview { get; set; }

        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool CloseAfterLosingFocus { get; set; }

        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool EnableClipboardPreview { get; set; }

        [JsonPropertyName("custom-actions")]
        [CmdConfigureIgnoreAttribute]
        public AdvancedPasteCustomActions CustomActions { get; set; }

        [JsonPropertyName("additional-actions")]
        [CmdConfigureIgnoreAttribute]
        public AdvancedPasteAdditionalActions AdditionalActions { get; set; }

        [JsonPropertyName("paste-ai-configuration")]
        [CmdConfigureIgnoreAttribute]
        public PasteAIConfiguration PasteAIConfiguration { get; set; }

        public override string ToString()
            => JsonSerializer.Serialize(this, SettingsSerializationContext.Default.AdvancedPasteProperties);
    }
}
