// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using ManagedCommon;

namespace Kit.Settings.UI.Helpers
{
    internal sealed partial class AsyncCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool> _canExecute;

        public event EventHandler CanExecuteChanged;

        public AsyncCommand(Func<Task> execute, Func<bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter)
        {
            return _canExecute == null || _canExecute();
        }

        /// <summary>
        /// Runs the command. An <c>async void</c> entry point cannot propagate its exception
        /// to a caller, so failures are observed and logged here instead of crashing the
        /// process through the synchronization context.
        /// </summary>
        public async void Execute(object parameter)
        {
            try
            {
                await _execute();
            }
            catch (Exception ex)
            {
                Logger.LogError("An asynchronous command failed.", ex);
            }
        }

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
