// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Kit.Settings.UI.Helpers
{
    internal sealed class BoundedObservableCollection<T> : ObservableCollection<T>
    {
        internal long Revision { get; private set; }

        public void Append(T item, int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
            CheckReentrancy();
            Items.Add(item);
            TrimItems(capacity);
            NotifyReset();
        }

        public void AppendRange(IEnumerable<T> items, int capacity)
        {
            ArgumentNullException.ThrowIfNull(items);
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
            CheckReentrancy();
            bool changed = false;
            foreach (T item in items)
            {
                Items.Add(item);
                TrimItems(capacity);
                changed = true;
            }

            if (changed)
            {
                NotifyReset();
            }
        }

        public void Trim(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
            CheckReentrancy();
            if (Count > capacity)
            {
                TrimItems(capacity);
                NotifyReset();
            }
        }

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs args)
        {
            Revision++;
            base.OnCollectionChanged(args);
        }

        private void TrimItems(int capacity)
        {
            while (Items.Count > capacity)
            {
                Items.RemoveAt(0);
            }
        }

        private void NotifyReset()
        {
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
