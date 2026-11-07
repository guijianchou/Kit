// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Management.Deployment;
using Windows.Foundation;

namespace Kit.WingetLib;

public sealed class WingetClient : IWingetClient
{
    private readonly WingetDownloadCache _cache = new();
    private readonly WingetCliInstaller _installer = new();
    private readonly WingetBoundedOperation _operations = new(TimeSpan.FromSeconds(30));
    private readonly Action<string>? _diagnostic;

    public WingetClient()
        : this(null)
    {
    }

    public WingetClient(Action<string>? diagnostic)
    {
        _diagnostic = diagnostic;
    }

    public Task<WingetAvailability> DetectAsync(CancellationToken cancellationToken = default) => Run(() => Detect(cancellationToken), cancellationToken);

    public Task<IReadOnlyList<WingetPackageUpdate>> CheckUpdatesAsync(CancellationToken cancellationToken = default) => Run<IReadOnlyList<WingetPackageUpdate>>(
        () =>
        {
            var manager = WinGetComFactory.CreateManager();
            EnsureNoActiveInstallation(manager, cancellationToken);
            var catalog = ConnectInstalledCatalog(manager, cancellationToken);
            var result = catalog.FindPackages(WinGetComFactory.CreateFindOptions());
            EnsureFindSucceeded(result);
            var updates = new List<WingetPackageUpdate>();
            for (var index = 0; index < result.Matches.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var package = result.Matches[index].CatalogPackage;
                if (package.InstalledVersion is null || package.DefaultInstallVersion is null
                    || (!package.IsUpdateAvailable && !WingetSafetyPolicy.IsUnknownVersion(package.InstalledVersion.Version)))
                {
                    continue;
                }

                updates.Add(BuildSummary(package, package.DefaultInstallVersion));
            }

            EnsureNoActiveInstallation(manager, cancellationToken);
            return updates.OrderBy(package => package.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(package => package.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        },
        cancellationToken);

    public Task<IReadOnlyList<WingetPackageUpdate>> ListInstalledAsync(CancellationToken cancellationToken = default) => Run<IReadOnlyList<WingetPackageUpdate>>(
        () =>
        {
            var packages = ReadInstalledPackages(WinGetComFactory.CreateManager(), cancellationToken);
            var summaries = packages.Select(BuildInstalledSummary).ToArray();
            var identityCounts = summaries.GroupBy(package => InstalledKey(package.Id, package.InstalledVersion), StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            return summaries.Select(package => package with { CanUninstall = !string.IsNullOrEmpty(package.InstalledVersion) && identityCounts[InstalledKey(package.Id, package.InstalledVersion)] == 1 })
                .OrderBy(package => package.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(package => package.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        },
        cancellationToken);

    public Task<IReadOnlyList<WingetPackageUpdate>> SearchAsync(string query, CancellationToken cancellationToken = default) => _operations.RunAsync(
        token => Task.FromResult(WinGetComFactory.Run<IReadOnlyList<WingetPackageUpdate>>(
        () =>
        {
            var elapsed = Stopwatch.StartNew();
            string trace = Guid.NewGuid().ToString("N");
            string search = WingetSafetyPolicy.ValidateSearchQuery(query);
            void Log(string stage) => LogDiagnostic($"search={trace}; elapsedMs={elapsed.ElapsedMilliseconds}; {stage}");
            Log($"stage=activate; queryLength={search.Length}");
            try
            {
                var manager = WinGetComFactory.CreateManager();
                Log("stage=connect");
                var catalog = ConnectCatalog(manager, CompositeSearchBehavior.RemotePackagesFromAllCatalogs, token);
                Log("stage=find");
                var options = WinGetComFactory.CreateFindOptions();
                options.ResultLimit = 100;
                foreach (var field in new[] { PackageMatchField.Id, PackageMatchField.Name, PackageMatchField.Moniker })
                {
                    var selector = WinGetComFactory.CreateFilter();
                    selector.Field = field;
                    selector.Option = PackageFieldMatchOption.ContainsCaseInsensitive;
                    selector.Value = search;
                    options.Selectors.Add(selector);
                }

                token.ThrowIfCancellationRequested();
                var result = catalog.FindPackages(options);
                EnsureFindSucceeded(result);
                token.ThrowIfCancellationRequested();
                var matches = result.Matches;
                int count = matches.Count;
                Log($"stage=identities; matches={count}");
                var packages = new List<WingetPackageUpdate>(Math.Min(count, 100));
                for (int index = 0; index < Math.Min(count, 100); index++)
                {
                    token.ThrowIfCancellationRequested();
                    var package = matches[index].CatalogPackage;

                    // Even DefaultInstallVersion/AvailableVersions can download V2
                    // version data per result. Only index-backed identity/name are
                    // read here; details resolve one selected package on demand.
                    packages.Add(CreateSearchSummary(package.Id, package.Name));
                }

                Log($"stage=complete; returned={packages.Count}");
                return packages.OrderBy(package => package.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(package => package.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception exception)
            {
                Log($"stage=failed; error={exception.GetType().Name}; hresult=0x{exception.HResult:X8}");
                throw;
            }
        })),
        cancellationToken);

    internal static WingetPackageUpdate CreateSearchSummary(string id, string name)
        => new(id, name, string.Empty, string.Empty, "winget", null, null, WingetInstallEligibility.ManualOnly, "Load package details to verify the installed state, version, and installer permissions.", string.Empty)
        {
            IsMetadataResolved = false,
        };

    public Task<WingetPackageDetails> GetDetailsAsync(WingetPackageUpdate package, CancellationToken cancellationToken = default)
        => GetDetailsAsync(package, null, cancellationToken);

    public Task<WingetPackageDetails> GetDetailsAsync(WingetPackageUpdate package, WingetPackageOptions? options, CancellationToken cancellationToken) => Run(
        () =>
        {
            var elapsed = Stopwatch.StartNew();
            string trace = Guid.NewGuid().ToString("N");
            LogDiagnostic($"details={trace}; stage=resolve; metadataResolved={package.IsMetadataResolved}");
            if (package.IsMetadataResolved && (package.Source != "winget" || string.IsNullOrEmpty(package.AvailableVersion)))
            {
                var installed = ResolveInstalledPackage(package, cancellationToken);
                return new WingetPackageDetails(
                    BuildInstalledSummary(installed.Package) with { CanUninstall = true },
                    NullIfEmpty(installed.Package.InstalledVersion.GetMetadata(PackageVersionMetadataField.PublisherDisplayName)),
                    null,
                    null,
                    null,
                    null,
                    []);
            }

            var resolved = ResolvePackage(package, cancellationToken, allowUnresolved: true);
            cancellationToken.ThrowIfCancellationRequested();
            LogDiagnostic($"details={trace}; stage=metadata; elapsedMs={elapsed.ElapsedMilliseconds}");
            var metadata = resolved.Version.GetCatalogPackageMetadata();
            var detailsPackage = BuildUpdate(resolved.Package, resolved.Version, options);
            if (package.CanUninstall)
            {
                _ = ResolveInstalledPackage(package, cancellationToken);
                detailsPackage = detailsPackage with { CanUninstall = true };
            }

            cancellationToken.ThrowIfCancellationRequested();
            LogDiagnostic($"details={trace}; stage=complete; elapsedMs={elapsed.ElapsedMilliseconds}");
            return new WingetPackageDetails(
                detailsPackage,
                NullIfEmpty(metadata.Publisher),
                NullIfEmpty(metadata.Description) ?? NullIfEmpty(metadata.ShortDescription),
                HttpUrl(metadata.PackageUrl),
                NullIfEmpty(metadata.License),
                HttpUrl(metadata.LicenseUrl),
                Snapshot(metadata.Agreements).Select(agreement => new WingetPackageAgreement(agreement.Label, agreement.Text, HttpUrl(agreement.Url))).ToArray());
        },
        cancellationToken);

    public Task<WingetDownloadResult> DownloadAsync(WingetPackageUpdate package, IProgress<WingetOperationProgress>? progress = null, bool acceptPackageAgreements = false, CancellationToken cancellationToken = default) => Run(
        () =>
        {
            var resolved = ResolvePackage(package, cancellationToken);
            var directory = _cache.CreateOperationDirectory();
            try
            {
                var selection = CreateInstallOptions(resolved.VersionId, GetInstalledScope(resolved.Package), acceptPackageAgreements);
                var installer = resolved.Version.GetApplicableInstaller(selection)
                    ?? throw new WingetOperationException("No compatible installer is available for this package version.");
                var options = WinGetComFactory.CreateDownloadOptions();
                options.PackageVersionId = resolved.VersionId;
                options.DownloadDirectory = directory;
                options.AllowHashMismatch = false;
                options.SkipDependencies = false;
                options.AcceptPackageAgreements = acceptPackageAgreements;
                options.Scope = selection.PackageInstallScope;
                options.Architecture = installer.Architecture;
                options.InstallerType = installer.InstallerType;
                options.Locale = installer.Locale;
                cancellationToken.ThrowIfCancellationRequested();
                var operation = resolved.Manager.DownloadPackageAsync(resolved.Package, options);
                operation.Progress = (_, value) => progress?.Report(new WingetOperationProgress(value.State.ToString(), ToPercentage(value.DownloadProgress), value.BytesDownloaded, value.BytesRequired));
                using var cancellation = ForwardCancellation(operation, cancellationToken);
                var result = operation.AsTask().GetAwaiter().GetResult();
                if (result.Status != DownloadResultStatus.Ok)
                {
                    throw new WingetOperationException($"WinGet download failed: {result.Status}.", result.ExtendedErrorCode?.HResult);
                }

                var files = _cache.GetDownloadedFiles(directory);
                if (files.Count == 0)
                {
                    throw new WingetOperationException("WinGet reported success but did not return any downloaded files.");
                }

                progress?.Report(new WingetOperationProgress("Completed", 100));
                return new WingetDownloadResult(directory, files, true);
            }
            catch
            {
                _cache.TryRemoveOperationDirectory(directory);
                throw;
            }
        },
        cancellationToken);

    public Task<WingetInstallResult> InstallAsync(WingetPackageUpdate package, IProgress<WingetOperationProgress>? progress = null, bool acceptPackageAgreements = false, WingetPackageOptions? options = null, CancellationToken cancellationToken = default) =>
        RunInstallationAsync(package, upgrade: false, progress, acceptPackageAgreements, options, cancellationToken);

    public Task<WingetInstallResult> UpgradeAsync(WingetPackageUpdate package, IProgress<WingetOperationProgress>? progress = null, bool acceptPackageAgreements = false, WingetPackageOptions? options = null, CancellationToken cancellationToken = default) =>
        RunInstallationAsync(package, upgrade: true, progress, acceptPackageAgreements, options, cancellationToken);

    public async Task<WingetInstallResult> UninstallAsync(WingetPackageUpdate package, IProgress<WingetOperationProgress>? progress = null, WingetPackageOptions? options = null, CancellationToken cancellationToken = default)
    {
        options = WingetSafetyPolicy.ValidateOptions(options, uninstall: true);
        var validated = await Run(
            () =>
            {
                var resolved = ResolveInstalledPackage(package, cancellationToken);
                EnsureNoActiveInstallation(resolved.Manager, cancellationToken);
                return (Package: BuildInstalledSummary(resolved.Package) with { CanUninstall = true }, InstalledId: resolved.Package.InstalledVersion.Id);
            },
            cancellationToken).ConfigureAwait(false);
        var result = await _installer.RunUninstallAsync(validated.Package, progress, cancellationToken, options).ConfigureAwait(false);
        return await VerifyResultAsync(
            result,
            token => !ReadLocalInstalledPackages(WinGetComFactory.CreateManager(), token).Any(candidate =>
                string.Equals(candidate.InstalledVersion?.Id, validated.InstalledId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.InstalledVersion?.Version, validated.Package.InstalledVersion, StringComparison.Ordinal))).ConfigureAwait(false);
    }

    private async Task<WingetInstallResult> RunInstallationAsync(WingetPackageUpdate package, bool upgrade, IProgress<WingetOperationProgress>? progress, bool acceptPackageAgreements, WingetPackageOptions? options, CancellationToken cancellationToken)
    {
        options = WingetSafetyPolicy.ValidateOptions(options);
        var validated = await Run(
            () =>
            {
                var resolved = ResolvePackage(package, cancellationToken);
                EnsureNoActiveInstallation(resolved.Manager, cancellationToken);
                var current = BuildUpdate(resolved.Package, resolved.Version, options);
                if (current.InstallEligibility == WingetInstallEligibility.ManualOnly)
                {
                    throw new WingetOperationException(current.InstallEligibilityReason);
                }

                if (upgrade && !string.Equals(resolved.Package.InstalledVersion?.Version, package.InstalledVersion, StringComparison.Ordinal))
                {
                    throw new WingetOperationException("The installed version changed after selection. Check for updates again before upgrading.");
                }

                bool canUpgrade = resolved.Package.InstalledVersion is { } installed
                    && WingetSafetyPolicy.CanUpgradeVersion(installed.Version, resolved.Version.CompareToVersion(installed.Version) == CompareResult.Greater, options.IncludeUnknown);
                if (upgrade ? !canUpgrade : resolved.Package.InstalledVersion is not null)
                {
                    throw new WingetOperationException(upgrade ? "The selected update is no longer newer than the installed package. Check again." : "This package is already installed. Use software updates instead.");
                }

                if (resolved.Version.GetCatalogPackageMetadata().Agreements.Count > 0 && !acceptPackageAgreements)
                {
                    throw new WingetOperationException("Review and accept this package's agreements before installation.");
                }

                return current;
            },
            cancellationToken).ConfigureAwait(false);

        var result = await _installer.RunAsync(validated, upgrade, acceptPackageAgreements, progress, cancellationToken, options).ConfigureAwait(false);
        return await VerifyResultAsync(
            result,
            token =>
            {
                var resolved = ResolvePackage(validated, token);
                return resolved.Package.InstalledVersion is { } installed &&
                    resolved.Version.CompareToVersion(installed.Version) is CompareResult.Equal or CompareResult.Lesser;
            }).ConfigureAwait(false);
    }

    private async Task<WingetInstallResult> VerifyResultAsync(WingetInstallResult result, Func<CancellationToken, bool> verify)
    {
        if (!result.Succeeded)
        {
            return result;
        }

        // ARP/catalog registration can lag a successful CLI exit. Preserve that success
        // while reporting that read-only verification still needs a later check.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
            }

            try
            {
                using var verificationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var verificationToken = verificationTimeout.Token;
                bool verified = await Run(() => verify(verificationToken), verificationToken).WaitAsync(verificationToken).ConfigureAwait(false);
                if (verified)
                {
                    return result;
                }
            }
            catch (Exception exception) when (exception is WingetOperationException or COMException or InvalidOperationException or OperationCanceledException)
            {
                // WinGet already reported success; delayed metadata is not installer failure.
            }
        }

        return result with { VerificationPending = true };
    }

    private Task<T> Run<T>(Func<T> operation, CancellationToken cancellationToken)
        => _operations.RunAsync(
            _ => Task.FromResult(WinGetComFactory.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return operation();
                })),
            cancellationToken,
            bounded: false);

    private void LogDiagnostic(string message)
    {
        try
        {
            _diagnostic?.Invoke(message);
        }
        catch (Exception)
        {
            // Diagnostic sinks must not change package-operation outcomes.
        }
    }

    private static CancellationTokenRegistration ForwardCancellation(IAsyncInfo operation, CancellationToken cancellationToken) => cancellationToken.Register(
        () =>
        {
            try
            {
                // Request cancellation but keep the service's operation slot until the
                // actual WinGet completion. An already running installer may continue.
                operation.Cancel();
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException)
            {
                // A completed or noncancelable COM operation still supplies its result.
            }
        });

    private static WingetAvailability Detect(CancellationToken cancellationToken)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = WinGetComFactory.CreateManager().GetPackageCatalogs();
            _ = WinGetComFactory.CreateDownloadOptions();
            string? version = null;
            if (File.Exists(executable))
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo(executable)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    },
                };
                process.StartInfo.ArgumentList.Add("--version");
                if (process.Start())
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                        var error = process.StandardError.ReadToEndAsync(timeout.Token);
                        process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
                        _ = error.GetAwaiter().GetResult();
                        if (process.ExitCode == 0)
                        {
                            version = output.GetAwaiter().GetResult().Trim();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
            }

            return new WingetAvailability(true, version, File.Exists(executable) ? executable : null, null);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or InvalidCastException or TypeInitializationException or DllNotFoundException or BadImageFormatException or FileNotFoundException or TypeLoadException or System.ComponentModel.Win32Exception)
        {
            return new WingetAvailability(false, null, File.Exists(executable) ? executable : null, $"WinGet is unavailable. Install or repair Microsoft App Installer. {exception.Message}");
        }
    }

    private static PackageCatalog ConnectInstalledCatalog(PackageManager manager, CancellationToken cancellationToken)
        => ConnectCatalog(manager, CompositeSearchBehavior.LocalCatalogs, cancellationToken);

    private static CatalogPackage[] ReadInstalledPackages(PackageManager manager, CancellationToken cancellationToken)
    {
        try
        {
            var result = ConnectInstalledCatalog(manager, cancellationToken).FindPackages(WinGetComFactory.CreateFindOptions());
            EnsureFindSucceeded(result);
            cancellationToken.ThrowIfCancellationRequested();
            return Snapshot(result.Matches).Select(match => match.CatalogPackage).Where(package => package.InstalledVersion is not null).ToArray();
        }
        catch (Exception exception) when (exception is WingetOperationException or COMException)
        {
            // The complete local installed inventory remains usable when a remote
            // catalog is offline or needs its source agreements reviewed separately.
            return ReadLocalInstalledPackages(manager, cancellationToken);
        }
    }

    private static CatalogPackage[] ReadLocalInstalledPackages(PackageManager manager, CancellationToken cancellationToken)
    {
        var connected = manager.GetLocalPackageCatalog(LocalPackageCatalog.InstalledPackages).ConnectAsync().AsTask(cancellationToken).GetAwaiter().GetResult();
        if (connected.Status != ConnectResultStatus.Ok || connected.PackageCatalog is null)
        {
            throw new WingetOperationException($"WinGet could not read installed packages: {connected.Status}.", connected.ExtendedErrorCode?.HResult);
        }

        var result = connected.PackageCatalog.FindPackages(WinGetComFactory.CreateFindOptions());
        EnsureFindSucceeded(result);
        cancellationToken.ThrowIfCancellationRequested();
        return Snapshot(result.Matches).Select(match => match.CatalogPackage).Where(package => package.InstalledVersion is not null).ToArray();
    }

    private static PackageCatalog ConnectCatalog(PackageManager manager, CompositeSearchBehavior behavior, CancellationToken cancellationToken)
    {
        // Version one covers the official community catalog. Store authentication and
        // arbitrary third-party source agreements are not accepted implicitly.
        var source = manager.GetPredefinedPackageCatalog(PredefinedPackageCatalog.OpenWindowsCatalog);
        source.AcceptSourceAgreements = false;
        var options = WinGetComFactory.CreateCompositeOptions();
        options.Catalogs.Add(source);
        options.CompositeSearchBehavior = behavior;
        cancellationToken.ThrowIfCancellationRequested();
        var operation = manager.CreateCompositePackageCatalog(options).ConnectAsync();
        using var cancellation = ForwardCancellation(operation, cancellationToken);

        // Cancellation requests native cancellation, but keeps the worker reserved
        // until the native operation really completes.
        var result = operation.AsTask().GetAwaiter().GetResult();
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Status != ConnectResultStatus.Ok || result.PackageCatalog is null)
        {
            throw new WingetOperationException($"WinGet could not connect to the winget catalog: {result.Status}.", result.ExtendedErrorCode?.HResult);
        }

        return result.PackageCatalog;
    }

    private static void EnsureNoActiveInstallation(PackageManager manager, CancellationToken cancellationToken)
    {
        foreach (var process in Process.GetProcessesByName("winget"))
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!process.HasExited)
                {
                    throw new WingetOperationException("Windows Package Manager is still running. Wait for its installer to finish before checking or starting another operation.");
                }
            }
        }

        var connected = manager.GetLocalPackageCatalog(LocalPackageCatalog.InstallingPackages).ConnectAsync().AsTask(cancellationToken).GetAwaiter().GetResult();
        if (connected.Status != ConnectResultStatus.Ok || connected.PackageCatalog is null)
        {
            throw new WingetOperationException($"WinGet could not verify that previous installations have finished: {connected.Status}.", connected.ExtendedErrorCode?.HResult);
        }

        var installing = connected.PackageCatalog.FindPackages(WinGetComFactory.CreateFindOptions());
        EnsureFindSucceeded(installing);
        for (var index = 0; index < installing.Matches.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var package = installing.Matches[index].CatalogPackage;

            // This only observes an existing WinGet queue item; null means none exists.
            // Do not Cancel, await with a token, or forward cancellation to this observer:
            // a management-capable caller could otherwise cancel somebody else's install.
            var observed = manager.GetInstallProgress(package, null);
            if (observed is null)
            {
                continue;
            }

            var status = observed.Status;
            if (status != AsyncStatus.Completed)
            {
                throw new WingetOperationException($"WinGet has an installation that is still running or cannot be verified ({package.Id}, {status}). Check again after it finishes.", status == AsyncStatus.Error ? observed.ErrorCode?.HResult : null);
            }

            var result = observed.GetResults();
            if (result.Status != InstallResultStatus.Ok)
            {
                throw new WingetOperationException($"WinGet is still reporting an unfinished installation result for {package.Id}: {result.Status}. Check again after it finishes.", result.ExtendedErrorCode?.HResult);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static ResolvedPackage ResolvePackage(WingetPackageUpdate request, CancellationToken cancellationToken, bool allowUnresolved = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        WingetSafetyPolicy.ValidateIdentityPart(request.Id, nameof(request.Id));
        WingetSafetyPolicy.ValidateIdentityPart(request.Source, nameof(request.Source));
        if (!string.Equals(request.Source, "winget", StringComparison.OrdinalIgnoreCase))
        {
            throw new WingetOperationException("Only packages from the official winget community catalog are supported.");
        }

        if (request.IsMetadataResolved)
        {
            WingetSafetyPolicy.ValidateIdentityPart(request.AvailableVersion, nameof(request.AvailableVersion));
        }
        else if (!allowUnresolved || request.AvailableVersion.Length != 0 || request.InstalledVersion.Length != 0 || request.CanUninstall)
        {
            throw new WingetOperationException("Resolve and review this package's details before downloading or installing it.");
        }

        var manager = WinGetComFactory.CreateManager();
        var catalog = ConnectCatalog(manager, CompositeSearchBehavior.RemotePackagesFromAllCatalogs, cancellationToken);
        var options = WinGetComFactory.CreateFindOptions();
        var filter = WinGetComFactory.CreateFilter();
        filter.Field = PackageMatchField.Id;
        filter.Option = PackageFieldMatchOption.EqualsCaseInsensitive;
        filter.Value = request.Id;
        options.Filters.Add(filter);
        var result = catalog.FindPackages(options);
        EnsureFindSucceeded(result);
        cancellationToken.ThrowIfCancellationRequested();
        var matches = Snapshot(result.Matches).Select(match => match.CatalogPackage).Where(package => string.Equals(package.Id, request.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            throw new WingetOperationException("The catalog package could not be matched uniquely. Search or check for updates again.");
        }

        var package = matches[0];
        string requestedVersion = request.IsMetadataResolved ? request.AvailableVersion
            : package.DefaultInstallVersion?.Version ?? throw new WingetOperationException("This package has no available installer version.");
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = Snapshot(package.AvailableVersions).Where(version => string.Equals(version.Version, requestedVersion, StringComparison.Ordinal)).ToArray();
        if (candidates.Length != 1)
        {
            throw new WingetOperationException("The selected update version is unavailable or ambiguous. Check for updates again.");
        }

        var versionId = candidates[0];
        var versionInfo = package.GetPackageVersionInfo(versionId);
        if (!string.Equals(versionInfo.PackageCatalog.Info.Name, request.Source, StringComparison.OrdinalIgnoreCase))
        {
            throw new WingetOperationException("The selected update source changed. Check for updates again.");
        }

        return new ResolvedPackage(manager, package, versionInfo, versionId);
    }

    private static ResolvedInstalledPackage ResolveInstalledPackage(WingetPackageUpdate request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        WingetSafetyPolicy.ValidateIdentityPart(request.Id, nameof(request.Id));
        WingetSafetyPolicy.ValidateIdentityPart(request.InstalledVersion, nameof(request.InstalledVersion));
        if (request.Source is not ("winget" or "msstore" or "local" or "msix"))
        {
            throw new WingetOperationException("The installed package source is unsupported.");
        }

        var manager = WinGetComFactory.CreateManager();
        var matches = ReadInstalledPackages(manager, cancellationToken).Where(package => string.Equals(package.Id, request.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(package.InstalledVersion?.Version, request.InstalledVersion, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            throw new WingetOperationException("The installed package could not be matched uniquely. Refresh installed software before removal.");
        }

        var current = BuildInstalledSummary(matches[0]);
        if (!string.Equals(current.Source, request.Source, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current.InstalledVersion, request.InstalledVersion, StringComparison.Ordinal))
        {
            throw new WingetOperationException("The installed package changed. Refresh installed software before removal.");
        }

        return new ResolvedInstalledPackage(manager, matches[0]);
    }

    private static WingetPackageUpdate BuildInstalledSummary(CatalogPackage package)
    {
        if (package.DefaultInstallVersion is { } available && available.PackageCatalog.Info.Name is "winget" or "msstore")
        {
            return BuildSummary(package, available);
        }

        var installed = package.InstalledVersion;
        string type = installed.GetMetadata(PackageVersionMetadataField.InstallerType);
        string source = string.Equals(type, "msix", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "msstore", StringComparison.OrdinalIgnoreCase) ? "msix" : "local";
        return new WingetPackageUpdate(package.Id, package.Name, installed.Version, string.Empty, source, null, null, WingetInstallEligibility.ManualOnly, "This installed package has no matched winget installer. Its native uninstaller remains available when the installed identity is unique.", string.Empty);
    }

    private static WingetPackageUpdate BuildSummary(CatalogPackage package, PackageVersionInfo version)
    {
        // Search/check list construction must not download a manifest for every row.
        // Permissions, agreements, and release notes are fetched for selected details.
        string installedVersion = package.InstalledVersion?.Version ?? string.Empty;
        string source = version.PackageCatalog.Info.Name;
        string manualCommand = string.IsNullOrEmpty(installedVersion)
            ? WingetSafetyPolicy.CreateManualInstallCommand(package.Id, version.Version, source)
            : WingetSafetyPolicy.CreateManualUpgradeCommand(package.Id, version.Version, source);
        return new WingetPackageUpdate(package.Id, package.Name, installedVersion, version.Version, source, null, null, WingetInstallEligibility.ManualOnly, "Load package details to verify the compatible installer and permissions.", manualCommand)
        {
            IsUpdateAvailable = package.IsUpdateAvailable,
        };
    }

    private static WingetPackageUpdate BuildUpdate(CatalogPackage package, PackageVersionInfo version, WingetPackageOptions? packageOptions = null)
    {
        var installedScope = GetInstalledScope(package);
        var eligibility = (Eligibility: WingetInstallEligibility.ManualOnly, Reason: "Installer permission metadata is unavailable. Update it manually.");
        try
        {
            var versionId = Snapshot(package.AvailableVersions).Single(candidate => string.Equals(candidate.Version, version.Version, StringComparison.Ordinal) && string.Equals(candidate.PackageCatalogId, version.PackageCatalog.Info.Id, StringComparison.OrdinalIgnoreCase));
            var installer = version.GetApplicableInstaller(CreateInstallOptions(versionId, installedScope, false, packageOptions));
            if (installer is not null)
            {
                using var identity = WindowsIdentity.GetCurrent();
                var isElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                eligibility = WingetSafetyPolicy.Evaluate(installedScope, installer.Scope.ToString(), installer.ElevationRequirement.ToString(), isElevated);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            eligibility = (WingetInstallEligibility.ManualOnly, "Installer permission metadata could not be verified. Update it manually.");
        }

        var metadata = version.GetCatalogPackageMetadata();
        return BuildSummary(package, version) with
        {
            ReleaseNotes = NullIfEmpty(metadata.ReleaseNotes),
            ReleaseNotesUrl = HttpUrl(metadata.ReleaseNotesUrl),
            InstallEligibility = eligibility.Eligibility,
            InstallEligibilityReason = eligibility.Reason,
        };
    }

    private static InstallOptions CreateInstallOptions(PackageVersionId version, string? installedScope, bool acceptPackageAgreements, WingetPackageOptions? packageOptions = null)
    {
        packageOptions = WingetSafetyPolicy.ValidateOptions(packageOptions);
        var options = WinGetComFactory.CreateInstallOptions();
        options.PackageVersionId = version;
        installedScope = packageOptions.Scope ?? installedScope;
        options.PackageInstallScope = string.Equals(installedScope, "User", StringComparison.OrdinalIgnoreCase) ? PackageInstallScope.User : string.Equals(installedScope, "Machine", StringComparison.OrdinalIgnoreCase) || string.Equals(installedScope, "System", StringComparison.OrdinalIgnoreCase) ? PackageInstallScope.System : PackageInstallScope.Any;
        options.PackageInstallMode = packageOptions.Silent ? PackageInstallMode.Silent : PackageInstallMode.Interactive;
        if (packageOptions.Architecture is { } architecture)
        {
            options.AllowedArchitectures.Add(architecture switch
            {
                "x64" => Windows.System.ProcessorArchitecture.X64,
                "x86" => Windows.System.ProcessorArchitecture.X86,
                _ => Windows.System.ProcessorArchitecture.Arm64,
            });
        }

        options.AllowHashMismatch = false;

        // These options select metadata; native installation uses WinGet's normal
        // dependency resolution and hash verification.
        options.SkipDependencies = false;
        options.AcceptPackageAgreements = acceptPackageAgreements;
        options.AllowUpgradeToUnknownVersion = packageOptions.IncludeUnknown;
        options.Force = false;
        return options;
    }

    private static string? GetInstalledScope(CatalogPackage package) => package.InstalledVersion?.GetMetadata(PackageVersionMetadataField.InstalledScope);

    private static void EnsureFindSucceeded(FindPackagesResult result)
    {
        if (result.Status != FindPackagesResultStatus.Ok)
        {
            throw new WingetOperationException($"WinGet package lookup failed: {result.Status}.", result.ExtendedErrorCode?.HResult);
        }
    }

    private static double? ToPercentage(double value) => double.IsFinite(value) ? Math.Clamp(value * 100, 0, 100) : null;

    private static T[] Snapshot<T>(IReadOnlyList<T> values)
    {
        // WinGet's out-of-process vectors support indexed access. Their IIterable
        // projection can fail to marshal, so never enumerate them through LINQ/foreach.
        var result = new T[values.Count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = values[index];
        }

        return result;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string InstalledKey(string id, string version) => id + "\n" + version;

    private static string? HttpUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri.AbsoluteUri : null;

    private sealed record ResolvedPackage(PackageManager Manager, CatalogPackage Package, PackageVersionInfo Version, PackageVersionId VersionId);

    private sealed record ResolvedInstalledPackage(PackageManager Manager, CatalogPackage Package);
}
