// Manual, opt-in Windows notification probe. Invoke-WingetToastSmoke.ps1 links
// this into an isolated TestResults project; it is not part of the product or unit tests.
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Kit.WingetLib;
using Microsoft.Win32;
using Windows.UI.Notifications;

internal static class WingetToastProbe
{
    private const string ApplicationId = "Microsoft.KitWin32";
    private const string Tag = "WingetUpdates";
    private const string Group = "KitToastTag";
    private static readonly string[] RegistrationKeys =
    [
        @"Software\Classes\AppUserModelId\Microsoft.KitWin32",
        @"Software\Classes\CLSID\{DD5CACDA-7C2E-4997-A62A-04A597B58F76}",
        @"Software\Classes\kit",
    ];

    internal static async Task<int> RunAsync(string runtimeDirectory, string reportDirectory)
    {
        runtimeDirectory = Path.GetFullPath(runtimeDirectory);
        reportDirectory = Path.GetFullPath(reportDirectory);
        foreach (string name in new[] { "Kit", "Kit.Settings", "Kit.WingetService" })
        {
            var processes = Process.GetProcessesByName(name);
            bool running = processes.Length > 0;
            foreach (var process in processes)
            {
                process.Dispose();
            }

            if (running)
            {
                throw new InvalidOperationException("Close Kit and its Winget service before the notification probe.");
            }
        }

        if (!File.Exists(Path.Combine(runtimeDirectory, "Kit.exe")))
        {
            throw new FileNotFoundException("Use the complete built Kit runtime directory.");
        }

        Directory.CreateDirectory(reportDirectory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var client = new WingetClient();
        var availability = await client.DetectAsync(timeout.Token);
        if (!availability.IsAvailable)
        {
            Console.WriteLine("LIMITATION: Windows Package Manager is unavailable: " + availability.Error);
            return 2;
        }

        // The displayed count comes from a real read-only query. This probe never
        // calls package download, install, cancellation, or activation APIs.
        var packages = await client.CheckUpdatesAsync(timeout.Token);
        File.WriteAllText(Path.Combine(reportDirectory, "queried-updates.json"), JsonSerializer.Serialize(packages));
        if (packages.Count == 0)
        {
            Console.WriteLine("LIMITATION: no real updates are available, so no notification was displayed.");
            return 2;
        }

        var assembly = Assembly.LoadFrom(Path.Combine(runtimeDirectory, "Kit.WingetService.dll"));
        var registration = assembly.GetType("Kit.WingetService.WingetNotificationRegistration", throwOnError: true)!;
        var notifierType = assembly.GetType("Kit.WingetService.WingetUpdateNotifier", throwOnError: true)!;
        var backups = RegistrationKeys.ToDictionary(path => path, Capture);
        string backupJson = JsonSerializer.Serialize(backups);
        File.WriteAllText(Path.Combine(reportDirectory, "registration-before.json"), backupJson);
        object? oldBaseDirectory = AppContext.GetData("APP_CONTEXT_BASE_DIRECTORY");
        bool ownsNotification = false;
        try
        {
            // Invoke the compiled production registration and notifier. Redirecting
            // this isolated probe's base directory supplies the real shipped icon.
            AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", runtimeDirectory + Path.DirectorySeparatorChar);
            registration.GetMethod("EnsureRegistered", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [runtimeDirectory]);
            if (ToastNotificationManager.History.GetHistory(ApplicationId).Any(IsProbeNotification))
            {
                throw new InvalidOperationException("An existing Winget notification uses this tag. The probe preserves it and stops.");
            }

            var notifier = Activator.CreateInstance(notifierType, nonPublic: true)!;
            ownsNotification = true;
            EventHandler<FirstChanceExceptionEventArgs> capture = (_, args) =>
            {
                try
                {
                    File.AppendAllText(Path.Combine(reportDirectory, "notification-exceptions.log"),
                        args.Exception.GetType().FullName + " HRESULT=0x" + args.Exception.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
                        + Environment.NewLine + args.Exception + Environment.NewLine);
                }
                catch
                {
                }
            };
            string? error;
            AppDomain.CurrentDomain.FirstChanceException += capture;
            try
            {
                error = notifierType.GetMethod("Show")!.Invoke(notifier, [packages]) as string;
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= capture;
            }

            if (error is not null)
            {
                throw new InvalidOperationException("Production notification failed: " + error);
            }

            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                var accepted = ToastNotificationManager.History.GetHistory(ApplicationId).FirstOrDefault(IsProbeNotification);
                if (accepted is not null)
                {
                    string xml = accepted.Content.GetXml();
                    if (!xml.Contains("kit://open_winget/", StringComparison.Ordinal)
                        || !xml.Contains(packages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("The accepted notification did not contain the queried update count and Winget route.");
                    }

                    File.WriteAllText(Path.Combine(reportDirectory, "accepted-toast.xml"), xml);
                    Console.WriteLine($"PASS: Windows notification history accepted the production Winget toast for {packages.Count} real updates.");
                    return 0;
                }

                await Task.Delay(250);
            }

            throw new TimeoutException("Show returned successfully, but the Winget toast did not appear in Windows notification history.");
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            if (ownsNotification)
            {
                try
                {
                    ToastNotificationManager.History.Remove(Tag, Group, ApplicationId);
                    if (ToastNotificationManager.History.GetHistory(ApplicationId).Any(IsProbeNotification))
                    {
                        throw new IOException("The probe notification remains in Windows history.");
                    }
                }
                catch (Exception ex)
                {
                    cleanupErrors.Add(ex);
                }
            }

            foreach (string path in RegistrationKeys.Reverse())
            {
                try
                {
                    Restore(path, backups[path]);
                }
                catch (Exception ex)
                {
                    cleanupErrors.Add(ex);
                }
            }

            AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", oldBaseDirectory);
            string afterJson = JsonSerializer.Serialize(RegistrationKeys.ToDictionary(path => path, Capture));
            File.WriteAllText(Path.Combine(reportDirectory, "registration-after.json"), afterJson);
            if (afterJson != backupJson)
            {
                cleanupErrors.Add(new IOException("Registration restoration did not match the original values, kinds, and key existence."));
            }

            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException("Notification probe cleanup failed. Inspect the saved registry backup.", cleanupErrors);
            }

            Console.WriteLine("RESTORE verified: own toast removed; all three registration keys match original values and existence.");
        }
    }

    private static bool IsProbeNotification(ToastNotification notification) => notification.Tag == Tag && notification.Group == Group;

    private static KeySnapshot? Capture(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
        return key is null ? null : CaptureKey(key);
    }

    private static KeySnapshot CaptureKey(RegistryKey key)
    {
        var result = new KeySnapshot();
        foreach (string name in key.GetValueNames().Order(StringComparer.Ordinal))
        {
            object data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                ?? throw new IOException("Cannot preserve registry value: " + key.Name + "\\" + name);
            result.Values.Add(name, new(key.GetValueKind(name), data));
        }

        foreach (string name in key.GetSubKeyNames().Order(StringComparer.Ordinal))
        {
            using var child = key.OpenSubKey(name, writable: false)
                ?? throw new IOException("Cannot preserve registry key: " + key.Name + "\\" + name);
            result.Children.Add(name, CaptureKey(child));
        }

        return result;
    }

    private static void Restore(string path, KeySnapshot? snapshot)
    {
        if (!RegistrationKeys.Contains(path, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The restoration target is outside the probe's three registration keys.");
        }

        if (snapshot is null)
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
        RestoreKey(key, snapshot);
    }

    private static void RestoreKey(RegistryKey key, KeySnapshot snapshot)
    {
        foreach (string name in key.GetValueNames().Where(name => !snapshot.Values.ContainsKey(name)))
        {
            key.DeleteValue(name);
        }

        foreach (string name in key.GetSubKeyNames().Where(name => !snapshot.Children.ContainsKey(name)))
        {
            key.DeleteSubKeyTree(name);
        }

        foreach (var value in snapshot.Values)
        {
            key.SetValue(value.Key, value.Value.Data, value.Value.Kind);
        }

        foreach (var child in snapshot.Children)
        {
            using var childKey = key.CreateSubKey(child.Key, writable: true);
            RestoreKey(childKey, child.Value);
        }
    }

    private sealed class KeySnapshot
    {
        public SortedDictionary<string, ValueSnapshot> Values { get; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, KeySnapshot> Children { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ValueSnapshot(RegistryValueKind Kind, object Data);
}
