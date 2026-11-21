// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Kit.Settings.UI.ViewModels;
using Kit.WingetLib;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.Views;

public sealed class WingetConfirmationItem
{
    public WingetConfirmationItem(WingetPackageDetails details, string operationKind)
    {
        ArgumentNullException.ThrowIfNull(details);
        Name = details.Package.Name;
        Identity = details.Package.Id + " · " + details.Package.Source;
        VersionTransition = details.Package.InstalledVersion + (operationKind == "uninstall" ? string.Empty : "  →  " + details.Package.AvailableVersion);
        LicenseSummary = string.IsNullOrWhiteSpace(details.License)
            ? string.Empty
            : WingetViewModel.L("LicenseLabel") + ": " + details.License;
        AgreementsSummary = details.Agreements.Count == 0
            ? string.Empty
            : string.Join(
                Environment.NewLine + Environment.NewLine,
                details.Agreements.Select(a => string.Join(Environment.NewLine, new[] { a.Label, a.Text, a.Url }.Where(part => !string.IsNullOrWhiteSpace(part)))));
    }

    public string Name { get; }

    public string Identity { get; }

    public string VersionTransition { get; }

    public string LicenseSummary { get; }

    public Visibility LicenseVisibility => string.IsNullOrEmpty(LicenseSummary) ? Visibility.Collapsed : Visibility.Visible;

    public string AgreementsSummary { get; }

    public Visibility AgreementsVisibility => string.IsNullOrEmpty(AgreementsSummary) ? Visibility.Collapsed : Visibility.Visible;
}

public sealed partial class WingetConfirmationDialog : ContentDialog
{
    public WingetConfirmationDialog(WingetPreparedOperation prepared, string optionsSummary)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        InitializeComponent();
        Title = WingetViewModel.L(prepared.Kind == "uninstall" ? "BatchUninstallTitle" : prepared.Kind == "upgrade" ? "BatchUpgradeTitle" : "BatchInstallTitle");
        PrimaryButtonText = WingetViewModel.L(prepared.Packages.Any(item => item.Agreements.Count > 0) ? "AcceptContinue" : "Continue");
        CloseButtonText = WingetViewModel.L("CancelText");
        ExplanationText.Text = WingetViewModel.L(prepared.Kind == "uninstall" ? "BatchUninstallExplanation" : "BatchInstallExplanation");
        ElevationText.Text = WingetViewModel.L("ElevationConfirmation");
        OptionsSummaryText.Text = optionsSummary ?? string.Empty;
        PackagesRepeater.ItemsSource = prepared.Packages.Select(details => new WingetConfirmationItem(details, prepared.Kind)).ToList();
    }
}
