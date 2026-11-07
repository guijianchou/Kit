// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Text.Json.Serialization;

using Kit.Settings.UI.Library.Helpers;

namespace Kit.Settings.UI.Library;

public sealed partial class AdvancedPasteAdditionalAction : Observable, IAdvancedPasteAction
{
    private bool _isShown;

    [JsonPropertyName("isShown")]
    public bool IsShown
    {
        get => _isShown;
        set => Set(ref _isShown, value);
    }

    [JsonIgnore]
    public IEnumerable<IAdvancedPasteAction> SubActions => [];
}
