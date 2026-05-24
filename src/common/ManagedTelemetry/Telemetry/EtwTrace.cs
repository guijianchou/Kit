// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace Microsoft.PowerToys.Telemetry
{
    /// <summary>
    /// No-op ETW compatibility shim retained for PowerToys-derived modules.
    /// </summary>
    public class ETWTrace : IDisposable
    {
        private bool disposedValue;

        /// <summary>
        /// Initializes a new instance of the <see cref="ETWTrace"/> class.
        /// </summary>
        public ETWTrace()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ETWTrace"/> class.
        /// </summary>
        /// <param name="etwPath">Ignored compatibility path.</param>
        public ETWTrace(string etwPath)
        {
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            this.Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Compatibility no-op; Kit does not start managed trace sessions.
        /// </summary>
        public void Start()
        {
        }

        /// <summary>
        /// Compatibility no-op; Kit does not start managed trace sessions.
        /// </summary>
        public void Stop()
        {
        }

        /// <summary>
        /// Disposes the object.
        /// </summary>
        /// <param name="disposing">boolean for disposing.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposedValue)
            {
                if (disposing)
                {
                    this.Stop();
                }

                this.disposedValue = true;
            }
        }
    }
}
