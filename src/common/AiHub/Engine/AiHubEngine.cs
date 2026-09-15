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
    private static readonly Lazy<TaskAiEngine> LazyInstance = new(CreateDefaultEngine);

    /// <summary>
    /// Gets the shared singleton instance of IAiTaskEngine.
    /// </summary>
    public static IAiTaskEngine Current => LazyInstance.Value;

    /// <summary>
    /// Gets the concrete implementation for settings management and notification.
    /// </summary>
    public static TaskAiEngine ConcreteEngine => LazyInstance.Value;

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
        if (LazyInstance.IsValueCreated)
        {
            LazyInstance.Value.Dispose();
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
