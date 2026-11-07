// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kit.Settings.UI.Library
{
    public class KeysDataModel
    {
        [JsonPropertyName("originalKeys")]
        public string OriginalKeys { get; set; }

        [JsonPropertyName("secondKeyOfChord")]
        public uint SecondKeyOfChord { get; set; }

        [JsonPropertyName("newRemapKeys")]
        public string NewRemapKeys { get; set; }

        [JsonPropertyName("unicodeText")]
        public string NewRemapString { get; set; }

        [JsonPropertyName("runProgramFilePath")]
        public string RunProgramFilePath { get; set; }

        [JsonPropertyName("runProgramArgs")]
        public string RunProgramArgs { get; set; }

        [JsonPropertyName("openUri")]
        public string OpenUri { get; set; }

        [JsonPropertyName("operationType")]
        public int OperationType { get; set; }

        public string ToJsonString()
        {
            return JsonSerializer.Serialize(this);
        }
    }
}
