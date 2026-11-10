// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Kit.AIHubLib.Models;
using Kit.Settings.UI.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace Kit.Settings.UI.Views;

public sealed partial class AIHubPage
{
    private AuditIssueEnhanced _detailsFinding;
    private Control _detailsFocusOrigin;
    private bool _detailsInitialized;
    private bool _detailsRefreshQueued;
    private bool _restoreDetailsFocus;

    private void InitializeFluentDetails()
    {
        if (_detailsInitialized)
        {
            return;
        }

        _detailsInitialized = true;
        AiHubFindingDetails.CloseRequested += OnDetailsCloseRequested;
        AiHubFindingDetails.PreviousRequested += OnDetailsPreviousRequested;
        AiHubFindingDetails.NextRequested += OnDetailsNextRequested;
        AiHubFindingDetails.AnalyzeRequested += OnDetailsAnalyzeRequested;
        AiHubDetailsHost.PaneOpening += OnDetailsPaneOpening;
        AiHubDetailsHost.PaneClosing += OnDetailsPaneClosing;
        AiHubDetailsHost.PaneClosed += OnDetailsPaneClosed;
        AiHubDetailsHost.SizeChanged += OnDetailsHostSizeChanged;
        ViewModel.PropertyChanged += OnFluentDetailsPropertyChanged;
        ViewModel.AllIssues.CollectionChanged += OnDetailsFindingsChanged;
        ViewModel.FilteredIssues.CollectionChanged += OnDetailsFindingsChanged;
        AddHandler(KeyDownEvent, new KeyEventHandler(OnDetailsKeyDown), handledEventsToo: true);
    }

    private void DisposeFluentDetails()
    {
        if (!_detailsInitialized)
        {
            return;
        }

        CloseFluentDetails(restoreFocus: false);
        _detailsInitialized = false;
        AiHubFindingDetails.CloseRequested -= OnDetailsCloseRequested;
        AiHubFindingDetails.PreviousRequested -= OnDetailsPreviousRequested;
        AiHubFindingDetails.NextRequested -= OnDetailsNextRequested;
        AiHubFindingDetails.AnalyzeRequested -= OnDetailsAnalyzeRequested;
        AiHubDetailsHost.PaneOpening -= OnDetailsPaneOpening;
        AiHubDetailsHost.PaneClosing -= OnDetailsPaneClosing;
        AiHubDetailsHost.PaneClosed -= OnDetailsPaneClosed;
        AiHubDetailsHost.SizeChanged -= OnDetailsHostSizeChanged;
        ViewModel.PropertyChanged -= OnFluentDetailsPropertyChanged;
        ViewModel.AllIssues.CollectionChanged -= OnDetailsFindingsChanged;
        ViewModel.FilteredIssues.CollectionChanged -= OnDetailsFindingsChanged;
        RemoveHandler(KeyDownEvent, new KeyEventHandler(OnDetailsKeyDown));
        _detailsFinding = null;
        _detailsFocusOrigin = null;
    }

    private void OpenFinding(AuditIssueEnhanced issue, FrameworkElement trigger = null)
    {
        if (issue == null || _disposed || !_currentPage || !IsLoaded || _activeDialog != null)
        {
            return;
        }

        if (!AiHubDetailsHost.IsPaneOpen)
        {
            _detailsFocusOrigin = trigger as Control;
        }

        _restoreDetailsFocus = true;
        _detailsFinding = issue;
        ResizeDetailsPane();
        RenderFindingDetails();
        if (DetailsBackdropScrim != null)
        {
            DetailsBackdropScrim.Visibility = Visibility.Visible;
        }

        AiHubDetailsHost.IsPaneOpen = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!_disposed && _currentPage && AiHubDetailsHost.IsPaneOpen)
            {
                AiHubFindingDetails.FocusCloseButton();
            }
        });
    }

    private void CloseFluentDetails(bool restoreFocus = true)
    {
        if (!_detailsInitialized)
        {
            return;
        }

        _restoreDetailsFocus = restoreFocus;
        if (DetailsBackdropScrim != null)
        {
            DetailsBackdropScrim.Visibility = Visibility.Collapsed;
        }

        AiHubDetailsHost.IsPaneOpen = false;
        if (!restoreFocus)
        {
            _detailsFocusOrigin = null;
            _detailsFinding = null;
        }
    }

    private void OnDetailsPaneOpening(SplitView sender, object args)
    {
        if (DetailsBackdropScrim != null)
        {
            DetailsBackdropScrim.Visibility = Visibility.Visible;
        }
    }

    private void OnDetailsPaneClosing(SplitView sender, SplitViewPaneClosingEventArgs args)
    {
        if (DetailsBackdropScrim != null)
        {
            DetailsBackdropScrim.Visibility = Visibility.Collapsed;
        }
    }

    private void OnDetailsBackdropTapped(object sender, TappedRoutedEventArgs e) => CloseFluentDetails();

    private void OnDetailsPaneClosed(SplitView sender, object args)
    {
        if (DetailsBackdropScrim != null)
        {
            DetailsBackdropScrim.Visibility = Visibility.Collapsed;
        }

        var origin = _detailsFocusOrigin;
        bool restore = _restoreDetailsFocus;
        _detailsFocusOrigin = null;
        _detailsFinding = null;
        _restoreDetailsFocus = false;
        if (restore && !_disposed && _currentPage && IsLoaded)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_disposed || !_currentPage || AiHubDetailsHost.IsPaneOpen)
                {
                    return;
                }

                if (origin == null || !origin.IsLoaded || !origin.Focus(FocusState.Programmatic))
                {
                    AiHubSections.Focus(FocusState.Programmatic);
                }
            });
        }
    }

    private void OnDetailsHostSizeChanged(object sender, SizeChangedEventArgs e) => ResizeDetailsPane();

    private void ResizeDetailsPane()
    {
        if (AiHubDetailsHost.ActualWidth > 0)
        {
            // A narrow window uses the whole content width; the regular desktop view
            // leaves the overview visible behind this temporary detail surface.
            AiHubDetailsHost.OpenPaneLength = Math.Min(464, AiHubDetailsHost.ActualWidth);
        }
    }

    private void OnDetailsCloseRequested(object sender, EventArgs e) => CloseFluentDetails();

    private void OnDetailsPreviousRequested(object sender, EventArgs e) => MoveFinding(-1);

    private void OnDetailsNextRequested(object sender, EventArgs e) => MoveFinding(1);

    private void MoveFinding(int direction)
    {
        var findings = MatchingFindings();
        int index = findings.FindIndex(issue => ReferenceEquals(issue, _detailsFinding));
        int target = index + direction;
        if (index >= 0 && target >= 0 && target < findings.Count)
        {
            _detailsFinding = findings[target];
            RenderFindingDetails();
        }
    }

    private void OnDetailsAnalyzeRequested(object sender, EventArgs e)
    {
        if (!ViewModel.DeepAnalyzeCommand.CanExecute(null))
        {
            return;
        }

        // Deep analysis belongs to the selected audit snapshot, not an individual
        // finding. Returning to the overview also exposes its non-modal progress.
        CloseFluentDetails();
        ViewModel.DeepAnalyzeCommand.Execute(null);
    }

    private void OnDetailsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && AiHubDetailsHost.IsPaneOpen)
        {
            e.Handled = true;
            CloseFluentDetails();
        }
    }

    private void OnFluentDetailsPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (!AiHubDetailsHost.IsPaneOpen || _disposed)
        {
            return;
        }

        if ((e.PropertyName == nameof(AIHubPageViewModel.ActiveTabIndex) && ViewModel.ActiveTabIndex != 0)
            || (e.PropertyName == nameof(AIHubPageViewModel.IsEnabled) && !ViewModel.IsEnabled))
        {
            CloseFluentDetails(restoreFocus: false);
            return;
        }

        AiHubFindingDetails.SetActionState(ViewModel.DeepAnalyzeCommand.CanExecute(null), ViewModel.DeepAnalyzeHint);
        QueueDetailsRefresh();
    }

    private void OnDetailsFindingsChanged(object sender, NotifyCollectionChangedEventArgs e) => QueueDetailsRefresh();

    private void QueueDetailsRefresh()
    {
        if (_detailsRefreshQueued || !AiHubDetailsHost.IsPaneOpen || _disposed)
        {
            return;
        }

        _detailsRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _detailsRefreshQueued = false;
            if (_disposed || !_currentPage || !AiHubDetailsHost.IsPaneOpen || _detailsFinding == null)
            {
                return;
            }

            // Filtering and snapshot loads rebuild observable collections with Clear
            // followed by Add. Resolve once the dispatcher has applied that batch.
            var current = ViewModel.AllIssues.FirstOrDefault(issue => ReferenceEquals(issue, _detailsFinding))
                ?? ViewModel.AllIssues.FirstOrDefault(issue => SameEvidence(issue, _detailsFinding));
            if (current == null)
            {
                CloseFluentDetails();
                return;
            }

            _detailsFinding = current;
            RenderFindingDetails();
        }))
        {
            _detailsRefreshQueued = false;
        }
    }

    private static bool SameEvidence(AuditIssueEnhanced left, AuditIssueEnhanced right) =>
        !string.IsNullOrWhiteSpace(left.Key)
        && string.Equals(left.Key, right.Key, StringComparison.Ordinal)
        && string.Equals(left.EventRef, right.EventRef, StringComparison.Ordinal)
        && string.Equals(left.EventTimestamp, right.EventTimestamp, StringComparison.Ordinal);

    private List<AuditIssueEnhanced> MatchingFindings() => ViewModel.FilteredIssues
        .OrderByDescending(issue => issue.IsHigh)
        .ThenByDescending(issue => issue.IsMedium)
        .ThenByDescending(issue => issue.Occurrences)
        .ToList();

    private void RenderFindingDetails()
    {
        if (_detailsFinding == null)
        {
            return;
        }

        var findings = MatchingFindings();
        int index = findings.FindIndex(issue => ReferenceEquals(issue, _detailsFinding));
        AiHubFindingDetails.SetFinding(_detailsFinding, ViewModel.WindowText, index, findings.Count,
            ViewModel.DeepAnalyzeCommand.CanExecute(null), ViewModel.DeepAnalyzeHint);
    }
}
