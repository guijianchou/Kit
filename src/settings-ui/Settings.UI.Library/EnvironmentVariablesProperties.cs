// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

using Kit.Settings.UI.Library.Enumerations;

namespace Kit.Settings.UI.Library
{
    public class EnvironmentVariablesProperties
    {
        [JsonConverter(typeof(BoolPropertyJsonConverter))]
        public bool LaunchAdministrator { get; set; }

        public EnvironmentVariablesProperties()
        {
            LaunchAdministrator = true;
        }
    }
}
