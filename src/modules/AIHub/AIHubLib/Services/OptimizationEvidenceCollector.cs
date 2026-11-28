using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Microsoft.Win32;

namespace Kit.AIHubLib.Services;

internal static class OptimizationEvidenceCollector
{
    private static readonly HashSet<string> Families = new(StringComparer.Ordinal) { "system", "diagnostics", "packages", "applications", "editors", "browsers" };
    internal sealed record Observation(string Domain, string Coverage, string Text, string LocalLabel);

    internal static OptimizationEvidenceSnapshot Combine(OptimizationInventoryReport inventory, IReadOnlyList<Observation> runtime,
        OptimizationStabilityEvidence? stability, long elapsedMilliseconds)
    {
        var evidence = new List<OptimizationDiagnosticEvidence>();
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string domain, string coverage, string observation, string label)
        {
            string id = $"item-{evidence.Count + 1:D6}";
            evidence.Add(new() { Id = id, Domain = domain, Coverage = coverage, Observation = observation });
            labels[id] = label;
        }

        Add("storage", inventory.IsPartial ? "partial" : "complete",
            FormattableString.Invariant($"Metadata inventory captured {inventory.CompletedUtc:O}; {inventory.Scopes.Count} scopes; {inventory.ExaminedEntries} examined entries; elapsed {inventory.ElapsedMilliseconds} ms. Only bounded known locations were considered; protected or unvisited locations were not assessed. Occupancy is not cleanup eligibility."), "Storage coverage / 存储覆盖范围");
        foreach (var group in inventory.Scopes.GroupBy(item => Families.Contains(item.FamilyId) ? item.FamilyId : "applications").Take(6))
        {
            long Sum(Func<OptimizationInventoryScope, long> selector) => SaturatingSum(group.Select(selector));
            Add("storage", group.All(item => item.State == "protected") ? "protected" : group.Any(item => item.State is not ("complete" or "missing")) ? "partial" : "complete",
                FormattableString.Invariant($"Family {group.Key}; scopes {group.Count()}; observed bytes {Sum(item => item.ObservedBytes ?? 0)}; measured scopes {group.Count(item => item.ObservedBytes.HasValue)}; protected {group.Count(item => item.State == "protected")}; missing {group.Count(item => item.State == "missing")}; recent-7-day bytes {Sum(item => item.RecentBytes)}; older-than-30-day bytes {Sum(item => item.OlderBytes)}."), group.Key);
        }
        foreach (var scope in inventory.Scopes.Where(item => item.ObservedBytes.HasValue).OrderByDescending(item => item.ObservedBytes).Take(12))
        {
            string family = Families.Contains(scope.FamilyId) ? scope.FamilyId : "applications";
            string disposition = scope.Disposition is "cache" or "system-managed" or "protected" ? scope.Disposition : "analysis-only";
            Add("storage", scope.State is "complete" or "protected" ? scope.State : "partial",
                FormattableString.Invariant($"Anonymous scope; family {family}; disposition {disposition}; observed bytes {scope.ObservedBytes}; files {scope.FileCount}; recent-7-day bytes {scope.RecentBytes}; older-than-30-day bytes {scope.OlderBytes}; access failures {scope.AccessFailures}. No file names, contents or deletion allowance supplied."), scope.NameEn + " / " + scope.NameZh);
        }
        foreach (var item in runtime.Take(12)) Add(item.Domain, item.Coverage, item.Text, item.LocalLabel);
        if (stability is null)
        {
            Add("stability", "unavailable", "No prior audit counters supplied. Stability was not assessed by this collection.", "Stability / 稳定性");
        }
        else
        {
            Add("stability", stability.IsPartial ? "partial" : "sampled",
                FormattableString.Invariant($"Prior audit summary: requested {Math.Clamp(stability.PeriodDays, 1, 366)} days; scans {Math.Max(0, stability.ScanCount)}; critical observations {Math.Max(0, stability.CriticalCount)}; warning observations {Math.Max(0, stability.WarningCount)}; observed from {stability.ObservedFromUtc:O} to {stability.ObservedToUtc:O}. Counts are historical observations, may repeat the same issue, and do not prove a performance cause."), "Audit history / 审计历史");
        }
        return new() { CollectedUtc = DateTimeOffset.UtcNow, CollectionElapsedMilliseconds = elapsedMilliseconds, Evidence = evidence, LocalLabels = labels };
    }

    internal static async Task<IReadOnlyList<Observation>> CollectRuntimeAsync(CancellationToken token)
    {
        return await Task.Run(async () =>
        {
            var observations = new List<Observation>();
            var clock = Stopwatch.StartNew();
            bool cpuAvailable = GetSystemTimes(out ulong idleBefore, out ulong kernelBefore, out ulong userBefore);
            var processes = new List<(Process Process, TimeSpan Cpu)>();
            int denied = 0;
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                observations.Add(new("performance", "unavailable", "Process enumeration unavailable; CPU/memory sample could not be completed.", "Runtime sample / 系统采样"));
                observations.Add(CollectStartup(token));
                return (IReadOnlyList<Observation>)observations;
            }
            try
            {
                foreach (var process in all.Take(256))
                {
                    token.ThrowIfCancellationRequested();
                    try { processes.Add((process, process.TotalProcessorTime)); }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { denied++; }
                }
                await Task.Delay(750, token).ConfigureAwait(false);
                double milliseconds = Math.Max(1, clock.Elapsed.TotalMilliseconds);
                if (cpuAvailable && GetSystemTimes(out ulong idleAfter, out ulong kernelAfter, out ulong userAfter) &&
                    kernelAfter >= kernelBefore && userAfter >= userBefore && idleAfter >= idleBefore)
                {
                    ulong total = kernelAfter - kernelBefore + userAfter - userBefore;
                    double busy = total == 0 ? 0 : 100.0 * (1 - Math.Min(1, (double)(idleAfter - idleBefore) / total));
                    observations.Add(new("performance", "sampled", FormattableString.Invariant($"System CPU busy {busy:F1} percent during {milliseconds:F0} ms; logical processors {Environment.ProcessorCount}. A brief sample is not a sustained-load or speedup baseline."), "CPU sample / CPU 短时采样"));
                }
                else
                {
                    observations.Add(new("performance", "unavailable", "System CPU sample unavailable.", "CPU sample / CPU 短时采样"));
                }

                var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                observations.Add(GlobalMemoryStatusEx(ref memory)
                    ? new("performance", "sampled", FormattableString.Invariant($"Physical memory total {memory.TotalPhysical} bytes; available {memory.AvailablePhysical} bytes; load {memory.MemoryLoad} percent. One point-in-time sample; no paging or long-term pressure measurements."), "Memory sample / 内存采样")
                    : new("performance", "unavailable", "Physical memory sample unavailable.", "Memory sample / 内存采样"));

                var samples = new List<(double Cpu, long Memory, string Name)>();
                foreach (var entry in processes)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        entry.Process.Refresh();
                        if (entry.Process.HasExited) continue;
                        double cpu = Math.Clamp((entry.Process.TotalProcessorTime - entry.Cpu).TotalMilliseconds / milliseconds / Environment.ProcessorCount * 100, 0, 100);
                        samples.Add((cpu, Math.Max(0, entry.Process.WorkingSet64), entry.Process.ProcessName));
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { denied++; }
                }
                observations.Add(new("performance", "partial", FormattableString.Invariant($"Processes enumerated {all.Length}; bounded sample attempted {Math.Min(all.Length, 256)}; readable samples {samples.Count}; denied/exited observations {denied}. Only top 6 CPU/working-set samples shown; process names and commands withheld. No disk I/O, boot traces or historical baseline collected."), "Process coverage / 进程覆盖范围"));
                foreach (var sample in samples.OrderByDescending(item => item.Cpu).Take(3).Concat(samples.OrderByDescending(item => item.Memory).Take(3)).Distinct())
                    observations.Add(new("performance", "sampled", FormattableString.Invariant($"Anonymous process sample: CPU {sample.Cpu:F2} percent of total machine capacity; working set {sample.Memory} bytes during approximately {milliseconds:F0} ms. This is not private memory, sustained consumption or causal evidence."), sample.Name));
                observations.Add(CollectStartup(token));
            }
            finally { foreach (var process in all) process.Dispose(); }
            token.ThrowIfCancellationRequested();
            return (IReadOnlyList<Observation>)observations;
        }, token).ConfigureAwait(false);
    }

    private static Observation CollectStartup(CancellationToken token)
    {
        int registryEntries = 0;
        int folderEntries = 0;
        int unavailable = 0;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                foreach (string keyName in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\RunOnce" })
                {
                    using var key = root.OpenSubKey(keyName, writable: false);
                    registryEntries += Math.Max(0, key?.ValueCount ?? 0);
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException) { unavailable++; }
        }
        foreach (var folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (folderEntries >= 256)
                {
                    unavailable++;
                    break;
                }
                string path = Environment.GetFolderPath(folder);
                if (Directory.Exists(path))
                {
                    if (!OptimizationFileSafety.HasNoReparsePoints(path))
                    {
                        unavailable++;
                        continue;
                    }
                    foreach (string entryPath in Directory.EnumerateFiles(path).Take(257))
                    {
                        token.ThrowIfCancellationRequested();
                        if (++folderEntries >= 256)
                        {
                            unavailable++;
                            break;
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { unavailable++; }
        }
        return new("startup", "partial", FormattableString.Invariant($"Run/RunOnce entries {registryEntries}; Startup folder entries {folderEntries}; unavailable or truncated sources {unavailable}. Counts only; names, commands and contents withheld. Disabled state, scheduled tasks, services and measured boot impact were not collected."), "Startup entry inventory / 启动项计数");
    }

    private static long SaturatingSum(IEnumerable<long> values)
    {
        long total = 0;
        foreach (long value in values) total = value <= 0 ? total : value > long.MaxValue - total ? long.MaxValue : total + value;
        return total;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
