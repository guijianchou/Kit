// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace Kit.Settings.UI.Library
{
    public sealed class NetMapProperties
    {
        [JsonPropertyName("proxyMode")]
        public IntProperty ProxyMode { get; set; } = new(0);

        [JsonPropertyName("proxyAddress")]
        public StringProperty ProxyAddress { get; set; } = new(string.Empty);

        [JsonPropertyName("asnDatabase")]
        public StringProperty AsnDatabase { get; set; } = new(string.Empty);

        [JsonPropertyName("cityDatabase")]
        public StringProperty CityDatabase { get; set; } = new(string.Empty);

        [JsonPropertyName("onlineLookup")]
        public BoolProperty OnlineLookup { get; set; } = new(true);
    }
}
