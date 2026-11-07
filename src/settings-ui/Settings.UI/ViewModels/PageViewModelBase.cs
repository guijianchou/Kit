// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using Kit.Settings.UI.Library.Helpers;

namespace Kit.Settings.UI.ViewModels
{
    public abstract class PageViewModelBase : Observable, IDisposable
    {
        protected abstract string ModuleName { get; }

        public virtual void OnPageLoaded()
        {
            Debug.WriteLine($"=== PAGE LOADED: {ModuleName} ===");
        }

        public virtual void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
        }
    }
}
