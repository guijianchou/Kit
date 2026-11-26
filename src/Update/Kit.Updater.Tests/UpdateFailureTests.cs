using Kit.Updater;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kit.Updater.Tests;

[TestClass]
public sealed class UpdateFailureTests
{
    private string temp = null!;

    [TestInitialize]
    public void Setup()
    {
        temp = Path.Combine(Path.GetTempPath(), "KitUpdaterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(temp, true);

    [TestMethod]
    public void StartupAccessDeniedIsReportedWithoutInitializedStorage()
    {
        using var stderr = new StringWriter();
        var error = new UnauthorizedAccessException("Cannot create the update cache.");

        var result = UpdateFailure.Report(error, null, "check", localApplicationData: temp, standardError: stderr);

        Assert.AreEqual(5, result);
        StringAssert.Contains(stderr.ToString(), error.Message);
        StringAssert.Contains(File.ReadAllText(Path.Combine(temp, "Kit", "updater-startup.log")), error.Message);
        Assert.IsFalse(File.Exists(Path.Combine(temp, "Kit", "UpdateState.json")));
    }

    [TestMethod]
    public void OperationBusyDoesNotOverwriteAnActiveUpdate()
    {
        var storage = CreateStorage();
        storage.Save(new UpdateState { State = UpdateStatus.Downloading, ErrorCode = string.Empty, GithubUpdateLastCheckedDate = "123" });
        var before = File.ReadAllText(storage.StatePath);

        var result = UpdateFailure.Report(new TimeoutException("Operation is busy."), storage, "check", operationBusy: true, standardError: TextWriter.Null);

        Assert.AreEqual(3, result);
        Assert.AreEqual(before, File.ReadAllText(storage.StatePath));
    }

    [TestMethod]
    public void OtherTimeoutsRemainGeneralFailures()
    {
        var storage = CreateStorage();

        var result = UpdateFailure.Report(new TimeoutException("State write timed out."), storage, "check", standardError: TextWriter.Null);

        Assert.AreEqual(1, result);
        Assert.AreEqual("updateFailed", storage.Read().ErrorCode);
    }

    [TestMethod]
    public void CheckFailurePreservesReadyToInstallPackage()
    {
        var storage = CreateStorage();
        storage.Save(new UpdateState
        {
            State = UpdateStatus.ReadyToInstall,
            TargetVersion = "3.5.12",
            DownloadedInstallerFilename = "package.zip",
            PackageSha256 = new string('a', 64),
            GithubUpdateLastCheckedDate = "123",
        });

        var result = UpdateFailure.Report(new UnauthorizedAccessException("Cannot read the release."), storage, "check", standardError: TextWriter.Null);

        var state = storage.Read();
        Assert.AreEqual(5, result);
        Assert.AreEqual(UpdateStatus.ReadyToInstall, state.State);
        Assert.AreEqual("3.5.12", state.TargetVersion);
        Assert.AreEqual("package.zip", state.DownloadedInstallerFilename);
        Assert.AreEqual(new string('a', 64), state.PackageSha256);
        Assert.AreEqual("accessDenied", state.ErrorCode);
        Assert.AreNotEqual("123", state.GithubUpdateLastCheckedDate);
    }

    [TestMethod]
    public void PrimaryLogFailureStillWritesOriginalFailureStateAndFallback()
    {
        var storage = CreateStorage();
        Directory.CreateDirectory(Path.Combine(storage.Cache, "updater.log"));
        using var stderr = new StringWriter();

        var result = UpdateFailure.Report(new UnauthorizedAccessException("Original access failure."), storage, "check", standardError: stderr);

        Assert.AreEqual(5, result);
        var state = storage.Read();
        Assert.AreEqual(UpdateStatus.NetworkError, state.State);
        Assert.AreEqual("accessDenied", state.ErrorCode);
        Assert.IsFalse(string.IsNullOrEmpty(state.GithubUpdateLastCheckedDate));
        StringAssert.Contains(File.ReadAllText(Path.Combine(storage.DataRoot, "updater-startup.log")), "Original access failure.");
        StringAssert.StartsWith(stderr.ToString(), "System.UnauthorizedAccessException: Original access failure.");
    }

    [TestMethod]
    public void FailedDiagnosticsAndStateWritePreserveOriginalExitCode()
    {
        var storage = CreateStorage();
        Directory.CreateDirectory(Path.Combine(storage.Cache, "updater.log"));
        Directory.CreateDirectory(storage.StatePath);
        var blockedFallback = Path.Combine(temp, "blocked-fallback");
        File.WriteAllText(blockedFallback, "This file prevents creating a log directory.");
        var stderr = new StringWriter();
        stderr.Dispose();

        var result = UpdateFailure.Report(new UnauthorizedAccessException("Original denied operation."), storage, "check", localApplicationData: blockedFallback, standardError: stderr);

        Assert.AreEqual(5, result);
        Assert.IsTrue(Directory.Exists(storage.StatePath));
    }

    [TestMethod]
    public void ClosedStandardErrorDoesNotPreventFallbackLogging()
    {
        var stderr = new StringWriter();
        stderr.Dispose();

        var result = UpdateFailure.Report(new UpdateException("invalidInstall", "Missing installation."), null, "check", localApplicationData: temp, standardError: stderr);

        Assert.AreEqual(1, result);
        StringAssert.Contains(File.ReadAllText(Path.Combine(temp, "Kit", "updater-startup.log")), "Missing installation.");
    }

    [TestMethod]
    public async Task CheckFailureKeepsItsOriginalErrorWhenPrimaryLogCannotBeWritten()
    {
        var storage = CreateStorage();
        Directory.CreateDirectory(Path.Combine(storage.Cache, "updater.log"));
        using var http = new HttpClient(new FailingHttpHandler());

        await new UpdateService(storage, new ReleaseClient(http)).CheckAsync(CancellationToken.None);

        Assert.AreEqual("checkFailed", storage.Read().ErrorCode);
        StringAssert.Contains(File.ReadAllText(Path.Combine(storage.DataRoot, "updater-startup.log")), "Original metadata request failure.");
    }

    [TestMethod]
    public void FallbackLogBoundsEachEntryAndTotalSize()
    {
        UpdateFailure.Report(new IOException(new string('x', 40000)), null, "check", localApplicationData: temp, standardError: TextWriter.Null);
        var path = Path.Combine(temp, "Kit", "updater-startup.log");
        Assert.IsTrue(new FileInfo(path).Length < 20000);
        StringAssert.Contains(File.ReadAllText(path), "[truncated]");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(1024 * 1024);
        }

        UpdateFailure.Report(new IOException("Another failure."), null, "check", localApplicationData: temp, standardError: TextWriter.Null);

        Assert.AreEqual(1024L * 1024, new FileInfo(path).Length);
    }

    private UpdateStorage CreateStorage()
    {
        var root = Path.Combine(temp, "installed");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Kit.exe"), "test executable");
        return new UpdateStorage(root, Path.Combine(temp, "data"));
    }

    private sealed class FailingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("Original metadata request failure."));
    }
}
