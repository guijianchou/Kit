// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Kit.WingetLib;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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

public sealed partial class WingetConfirmationDialog : ContentDialog, ILiveLocalizable
{
    private readonly WingetPreparedOperation prepared;
    private readonly string optionsSummary;
    private readonly Func<string> optionsSummaryProvider;
    private string displayedLanguage;

    public WingetConfirmationDialog(WingetPreparedOperation prepared, string optionsSummary, Func<string> optionsSummaryProvider = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        this.prepared = prepared;
        this.optionsSummary = optionsSummary ?? string.Empty;
        this.optionsSummaryProvider = optionsSummaryProvider;
        InitializeComponent();
        AppearanceService.RegisterRoot(this);
        RefreshLocalization();
    }

    public void RefreshLocalization()
    {
        Title = WingetViewModel.L(prepared.Kind == "uninstall" ? "BatchUninstallTitle" : prepared.Kind == "upgrade" ? "BatchUpgradeTitle" : "BatchInstallTitle");
        PrimaryButtonText = WingetViewModel.L(prepared.Packages.Any(item => item.Agreements.Count > 0) ? "AcceptContinue" : "Continue");
        CloseButtonText = WingetViewModel.L("CancelText");
        ExplanationText.Text = WingetViewModel.L(prepared.Kind == "uninstall" ? "BatchUninstallExplanation" : "BatchInstallExplanation");
        ElevationText.Text = WingetViewModel.L("ElevationConfirmation");
        OptionsSummaryText.Text = optionsSummaryProvider?.Invoke() ?? optionsSummary;
        if (displayedLanguage != RuntimeLocalization.EffectiveLanguage)
        {
            displayedLanguage = RuntimeLocalization.EffectiveLanguage;
            PackagesRepeater.ItemsSource = prepared.Packages.Select(details => new WingetConfirmationItem(details, prepared.Kind)).ToList();
        }
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // BackgroundElement owns the rounded acrylic. Avoid a second, square
        // material layer in the footer, which otherwise breaks its continuity.
        if (GetTemplateChild("CommandSpace") is Grid commandSpace)
        {
            commandSpace.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        // On short windows or larger text sizes, keep the title and full agreement
        // reachable without letting them push the fixed confirmation buttons away.
        if (GetTemplateChild("ContentScrollViewer") is ScrollViewer contentScroll)
        {
            contentScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
    }
}
