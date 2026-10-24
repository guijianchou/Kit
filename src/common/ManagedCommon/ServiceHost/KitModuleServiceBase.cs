// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ManagedCommon.ServiceHost
{
    public abstract class KitModuleServiceBase<TSettings> : IDisposable
        where TSettings : class, new()
    {
        private readonly CancellationTokenSource _cts = new();
        private Mutex? _instanceMutex;
        private FileSystemWatcher? _settingsWatcher;
        private RegisteredWaitHandle? _exitWaitHandle;
        private RegisteredWaitHandle? _refreshWaitHandle;
        private EventWaitHandle? _exitEvent;
        private EventWaitHandle? _refreshEvent;
        private KitServicePipeServer? _pipeServer;
        private Timer? _settingsDebounceTimer;
        private bool _disposed;

        public abstract string ModuleKey { get; }
        public abstract string ServiceExeName { get; }
        public abstract string ExitEventName { get; }
        public abstract string RefreshSettingsEventName { get; }

        public TSettings CurrentSettings { get; private set; } = new();
        public CancellationToken CancellationToken => _cts.Token;

        public async Task<int> RunAsync(string[] args)
        {
            Logger.InitializeLogger(Path.Combine("\\", ModuleKey, "Service", "Logs"));
            Logger.LogInfo($"[KitModuleServiceBase] Starting {ModuleKey} Service...");

            int runnerPid = 0;
            bool useKitConfig = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.Equals("--use-kit-config", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("-c", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("--use-pt-config", StringComparison.OrdinalIgnoreCase))
                {
                    useKitConfig = true;
                }
                else if ((arg.Equals("--pid", StringComparison.OrdinalIgnoreCase) || arg.Equals("-p", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out int pidVal))
                    {
                        runnerPid = pidVal;
                    }
                }
            }

            _ = useKitConfig;

            string mutexName = $@"Local\Kit.{ModuleKey}.Service.Mutex";
            _instanceMutex = new Mutex(true, mutexName, out bool createdNew);
            if (!createdNew)
            {
                Logger.LogWarning($"[KitModuleServiceBase] Another instance of {ModuleKey} Service is already running. Exiting.");
                return 0;
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseResources();

            if (runnerPid > 0)
            {
                Logger.LogInfo($"[KitModuleServiceBase] Binding to Kit Runner PID={runnerPid}");
                RunnerHelper.WaitForKitRunner(runnerPid, () =>
                {
                    Logger.LogInfo($"[KitModuleServiceBase] Runner process exited. Terminating {ModuleKey} Service.");
                    Stop();
                });
            }

            InitExitEvent();
            InitRefreshEvent();

            LoadInitialSettings();
            InitSettingsWatcher();

            _pipeServer = new KitServicePipeServer(ModuleKey, HandlePipeCommandAsync);
            _pipeServer.Start();

            try
            {
                await StartServiceAsync(CurrentSettings, _cts.Token).ConfigureAwait(false);
                Logger.LogInfo($"[KitModuleServiceBase] {ModuleKey} Service started successfully.");

                var tcs = new TaskCompletionSource();
                using (_cts.Token.Register(() => tcs.TrySetResult()))
                {
                    await tcs.Task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.LogError($"[KitModuleServiceBase] Fatal error in {ModuleKey} Service: {ex}");
                return 1;
            }
            finally
            {
                try
                {
                    await StopServiceAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[KitModuleServiceBase] Error during {ModuleKey} Service shutdown: {ex.Message}");
                }

                ReleaseResources();
            }

            Logger.LogInfo($"[KitModuleServiceBase] {ModuleKey} Service terminated cleanly.");
            return 0;
        }

        public void Stop()
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
        }

        protected virtual Task<KitPipeResponse> HandlePipeCommandAsync(KitPipeRequest request, CancellationToken ct)
        {
            return Task.FromResult(KitPipeResponse.Ok());
        }

        protected abstract Task StartServiceAsync(TSettings initialSettings, CancellationToken ct);

        protected abstract Task StopServiceAsync();

        protected abstract TSettings DeserializeSettings(string json);

        protected abstract string SerializeSettings(TSettings settings);

        protected virtual void OnSettingsUpdated(TSettings newSettings)
        {
        }

        private string GetSettingsFolderPath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Kit", ModuleKey);
        }

        private string GetSettingsFilePath()
        {
            return Path.Combine(GetSettingsFolderPath(), "settings.json");
        }

        private void LoadInitialSettings()
        {
            string folder = GetSettingsFolderPath();
            string file = GetSettingsFilePath();

            try
            {
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                if (File.Exists(file))
                {
                    string json = File.ReadAllText(file);
                    var parsed = DeserializeSettings(json);
                    if (parsed != null)
                    {
                        CurrentSettings = parsed;
                        return;
                    }
                }

                CurrentSettings = new TSettings();
                string defaultJson = SerializeSettings(CurrentSettings);
                File.WriteAllText(file, defaultJson);
                Logger.LogInfo($"[KitModuleServiceBase] Created default settings file at {file}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[KitModuleServiceBase] Failed to load/create settings: {ex.Message}");
                CurrentSettings = new TSettings();
            }
        }

        private void InitSettingsWatcher()
        {
            string folder = GetSettingsFolderPath();
            if (!Directory.Exists(folder))
            {
                return;
            }

            try
            {
                _settingsWatcher = new FileSystemWatcher(folder, "settings.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };

                _settingsWatcher.Changed += OnSettingsFileChanged;
                _settingsWatcher.Created += OnSettingsFileChanged;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[KitModuleServiceBase] Could not initialize settings file watcher: {ex.Message}");
            }
        }

        private void OnSettingsFileChanged(object sender, FileSystemEventArgs e)
        {
            _settingsDebounceTimer?.Dispose();
            _settingsDebounceTimer = new Timer(_ => ReloadSettings(), null, 250, Timeout.Infinite);
        }

        private void ReloadSettings()
        {
            string file = GetSettingsFilePath();
            if (!File.Exists(file))
            {
                return;
            }

            for (int retry = 0; retry < 5; retry++)
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    string json = reader.ReadToEnd();
                    var parsed = DeserializeSettings(json);
                    if (parsed != null)
                    {
                        CurrentSettings = parsed;
                        Logger.LogInfo($"[KitModuleServiceBase] Settings reloaded from {file}");
                        OnSettingsUpdated(CurrentSettings);
                        return;
                    }
                }
                catch (IOException)
                {
                    Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[KitModuleServiceBase] Error reloading settings: {ex.Message}");
                    return;
                }
            }
        }

        private void InitExitEvent()
        {
            if (string.IsNullOrEmpty(ExitEventName))
            {
                return;
            }

            try
            {
                _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
                _exitWaitHandle = ThreadPool.RegisterWaitForSingleObject(
                    _exitEvent,
                    (_, _) =>
                    {
                        Logger.LogInfo($"[KitModuleServiceBase] Exit event signaled for {ModuleKey}. Stopping service.");
                        Stop();
                    },
                    null,
                    -1,
                    false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[KitModuleServiceBase] Failed to bind exit event {ExitEventName}: {ex.Message}");
            }
        }

        private void InitRefreshEvent()
        {
            if (string.IsNullOrEmpty(RefreshSettingsEventName))
            {
                return;
            }

            try
            {
                _refreshEvent = new EventWaitHandle(false, EventResetMode.AutoReset, RefreshSettingsEventName);
                _refreshWaitHandle = ThreadPool.RegisterWaitForSingleObject(
                    _refreshEvent,
                    (_, _) =>
                    {
                        Logger.LogInfo($"[KitModuleServiceBase] Refresh settings event signaled for {ModuleKey}.");
                        ReloadSettings();
                    },
                    null,
                    -1,
                    false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[KitModuleServiceBase] Failed to bind refresh settings event {RefreshSettingsEventName}: {ex.Message}");
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    ReleaseResources();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void ReleaseResources()
        {
            _settingsDebounceTimer?.Dispose();
            _settingsWatcher?.Dispose();

            _exitWaitHandle?.Unregister(null);
            _exitEvent?.Dispose();

            _refreshWaitHandle?.Unregister(null);
            _refreshEvent?.Dispose();

            if (_pipeServer != null)
            {
                try
                {
                    _pipeServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                }

                _pipeServer = null;
            }

            if (_instanceMutex != null)
            {
                try
                {
                    _instanceMutex.ReleaseMutex();
                }
                catch
                {
                }

                _instanceMutex.Dispose();
                _instanceMutex = null;
            }

            _cts.Dispose();
        }
    }
}
