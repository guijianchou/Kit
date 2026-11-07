// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.WingetLib;

public interface IWingetClient
{
    Task<WingetAvailability> DetectAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WingetPackageUpdate>> CheckUpdatesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WingetPackageUpdate>> ListInstalledAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WingetPackageUpdate>> SearchAsync(string query, CancellationToken cancellationToken = default);

    Task<WingetPackageDetails> GetDetailsAsync(WingetPackageUpdate package, CancellationToken cancellationToken = default);

    Task<WingetPackageDetails> GetDetailsAsync(WingetPackageUpdate package, WingetPackageOptions? options, CancellationToken cancellationToken)
        => GetDetailsAsync(package, cancellationToken);

    Task<WingetDownloadResult> DownloadAsync(WingetPackageUpdate package, IProgress<WingetOperationProgress>? progress = null, bool acceptPackageAgreements = false, CancellationToken cancellationToken = default);

    Task<WingetInstallResult> InstallAsync(WingetPackageUpdate package, IProgress<WingetOperationProgress>? progress = null, bool acceptPackageAgreements = false, WingetPackageOptions? options = null, CancellationToken cancellationToken = default);

    Task<WingetInstallResult> UpgradeAsync(WingetPackageUpdate package, IProgress<WingetOperationProgress>? progress = null, bool acceptPackageAgreements = false, WingetPackageOptions? options = null, CancellationToken cancellationToken = default);

    Task<WingetInstallResult> UninstallAsync(WingetPackageUpdate package, IProgress<WingetOperationProgress>? progress = null, WingetPackageOptions? options = null, CancellationToken cancellationToken = default);
}
