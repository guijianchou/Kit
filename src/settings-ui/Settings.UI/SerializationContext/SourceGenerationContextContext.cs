// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;

namespace Kit.Settings.UI.SerializationContext;

[JsonSerializable(typeof(ActionMessage))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
[JsonSerializable(typeof(AwakeSettings))]
[JsonSerializable(typeof(LightSwitchSettings))]
[JsonSerializable(typeof(ShortcutConflictProperties))]
[JsonSerializable(typeof(WINDOWPLACEMENT))]
public sealed partial class SourceGenerationContextContext : JsonSerializerContext
{
}
