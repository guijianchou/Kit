// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace Kit.WingetLib;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WingetServiceStatus))]
[JsonSerializable(typeof(WingetServiceRequest))]
[JsonSerializable(typeof(WingetServiceAck))]
[JsonSerializable(typeof(WingetPackageIdentity))]
[JsonSerializable(typeof(WingetPreparedOperation))]
[JsonSerializable(typeof(WingetQueueItem))]
public sealed partial class WingetServiceJsonContext : JsonSerializerContext
{
}
