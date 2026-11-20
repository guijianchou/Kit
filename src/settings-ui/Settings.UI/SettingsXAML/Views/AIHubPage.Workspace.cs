// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.Views;

public sealed partial class AIHubPage
{
    private Control _auditOverviewOrigin;
    private string _auditOverviewCategory;

    private void OnAuditCategoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string category } trigger)
        {
            ShowAuditFindings(trigger, category);
        }
    }

    private void OnAuditRiskClick(object sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string risk } trigger)
        {
            ShowAuditFindings(trigger, severity: risk);
        }
    }

    private void OnAuditViewAllClick(object sender, RoutedEventArgs e) => ShowAuditFindings(sender as Control);

    private void ShowAuditFindings(Control trigger, string category = null, string severity = "All")
    {
        CloseFluentDetails(restoreFocus: false);
        _auditOverviewOrigin = trigger;
        _auditOverviewCategory = category;
        ViewModel.OpenAuditFindings(category, severity);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!_disposed && _currentPage && ViewModel.ShowAuditFindings)
            {
                AuditBackButton.Focus(FocusState.Programmatic);
                AuditResultViews.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            }
        });
    }

    private void OnAuditBackClick(object sender, RoutedEventArgs e)
    {
        CloseFluentDetails(restoreFocus: false);
        ViewModel.AuditResultViewIndex = 0;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_disposed || !_currentPage || ViewModel.ShowAuditFindings)
            {
                return;
            }

            UpdateLayout();
            var origin = _auditOverviewOrigin;
            if (_auditOverviewCategory != null)
            {
                int index = ViewModel.AuditCategories.ToList().FindIndex(category =>
                    string.Equals(category.Category, _auditOverviewCategory, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    origin = AuditCategoryCards.GetOrCreateElement(index) as Control;
                    UpdateLayout();
                }
            }

            origin?.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            if (origin?.IsLoaded != true || !origin.Focus(FocusState.Programmatic))
            {
                origin = AuditResultViews;
                origin.Focus(FocusState.Programmatic);
            }

            origin.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        });
    }

    private void OnAuditResultViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_detailsInitialized)
        {
            CloseFluentDetails(restoreFocus: false);
        }
    }
}
