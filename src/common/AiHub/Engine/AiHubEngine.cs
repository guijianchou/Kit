namespace Kit.AiHub.Engine;

using System;
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
            lock (Gate)
            {
                // Failed construction is retryable, and shutdown never republishes a disposed engine.
                return _instance ??= CreateDefaultEngine();
            }
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
        lock (Gate)
        {
            TaskAiEngine? instance = _instance;
            _instance = null;
            instance?.Dispose();
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
