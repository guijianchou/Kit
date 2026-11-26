namespace Kit.AiHub.Engine;

using System;
using System.Threading;
using Kit.AiHub.Contract;
using Kit.AiHub.Security;
using Kit.AiHub.Storage;

/// <summary>
/// Static service factory and singleton accessor for the AI Hub subsystem.
/// </summary>
public static class AiHubEngine
{
    private static readonly object Gate = new();
    private static TaskAiEngine? _instance;

    /// <summary>Observes an already initialized service without constructing it or waiting for initialization.</summary>
    public static TaskAiEngine? ExistingEngine => Volatile.Read(ref _instance);

    /// <summary>Signals publication or removal of the shared engine, outside the factory lock.</summary>
    public static event EventHandler? InstanceChanged;

    /// <summary>
    /// Gets the shared singleton instance of IAiTaskEngine.
    /// </summary>
    public static IAiTaskEngine Current => ConcreteEngine;

    /// <summary>
    /// Gets the concrete implementation for settings management and notification.
    /// </summary>
    public static TaskAiEngine ConcreteEngine
    {
        get
        {
            TaskAiEngine instance;
            bool created;
            lock (Gate)
            {
                // Failed construction is retryable, and shutdown never republishes a disposed engine.
                created = _instance is null;
                instance = _instance ??= CreateDefaultEngine();
            }

            if (created)
            {
                NotifyInstanceChanged();
            }

            return instance;
        }
    }

    /// <summary>
    /// Raises state changed notification across listeners.
    /// </summary>
    public static void RaiseStateChanged() => ConcreteEngine.NotifyStateChanged();

    /// <summary>
    /// Creates an independent IAiTaskEngine instance (e.g. for out-of-process workers).
    /// </summary>
    public static IAiTaskEngine Create(string? customDataDirectory = null, string? pluginPackagesDirectory = null)
    {
        var settingsStore = string.IsNullOrWhiteSpace(customDataDirectory)
            ? new AiHubSettingsStore()
            : new AiHubSettingsStore(customDataDirectory);

        var kernelManager = string.IsNullOrWhiteSpace(customDataDirectory)
            ? new KernelManagerService()
            : new KernelManagerService(System.IO.Path.Combine(customDataDirectory, "kernels"));

        var securityService = new SecurityPolicyService(settingsStore.DataDirectory, pluginPackagesDirectory);

        return new TaskAiEngine(settingsStore, kernelManager, securityService);
    }

    public static void Shutdown()
    {
        bool removed;
        lock (Gate)
        {
            TaskAiEngine? instance = _instance;
            removed = instance is not null;
            _instance = null;
            instance?.Dispose();
        }

        if (removed)
        {
            NotifyInstanceChanged();
        }
    }

    private static void NotifyInstanceChanged()
    {
        if (InstanceChanged is not { } handlers)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(null, EventArgs.Empty);
            }
            catch (Exception)
            {
                // Observational UI listeners must not affect engine initialization or shutdown.
            }
        }
    }

    private static TaskAiEngine CreateDefaultEngine()
    {
        var settingsStore = new AiHubSettingsStore();
        var kernelManager = new KernelManagerService();
        var securityService = new SecurityPolicyService();

        return new TaskAiEngine(settingsStore, kernelManager, securityService);
    }
}
