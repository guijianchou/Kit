// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.Settings.UI.Services;

/// <summary>Refreshes presentation text without replacing the view or its editable state.</summary>
public interface ILiveLocalizable
{
    void RefreshLocalization();
}
