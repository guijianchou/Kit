// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.UDPtestService
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using ManagedCommon;
    using ManagedCommon.ServiceHost;
    using UDPtestLib.Engine;

    public sealed class UDPtestServiceConfig
    {
        public bool IsEnabled { get; set; } = true;
        public int IntervalMs { get; set; } = 1000;
    }

    public sealed class UDPtestServiceHost : KitModuleServiceBase<UDPtestServiceConfig>
    {
        public override string ModuleKey => "UDPtest";
        public override string ServiceExeName => "Kit.UDPtestService.exe";
        public override string ExitEventName => @"Local\Kit_UDPtest_Stop_Event_c9d0e1f2";
        public override string RefreshSettingsEventName => @"Local\Kit_UDPtest_RefreshSettings_Event_1a2b3c4d";

        private ProbeCoordinator? _coordinator;

        protected override Task StartServiceAsync(UDPtestServiceConfig initialSettings, CancellationToken ct)
        {
            Logger.LogInfo("[UDPtestService] Service started.");
            _coordinator = new ProbeCoordinator();
            return Task.CompletedTask;
        }

        protected override async Task StopServiceAsync()
        {
            Logger.LogInfo("[UDPtestService] Service stopping.");
            if (_coordinator != null)
            {
                await _coordinator.DisposeAsync().ConfigureAwait(false);
                _coordinator = null;
            }
        }

        protected override Task<KitPipeResponse> HandlePipeCommandAsync(KitPipeRequest request, CancellationToken ct)
        {
            switch (request.Command.ToLowerInvariant())
            {
                case "get-telemetry":
                case "get-status":
                    return Task.FromResult(KitPipeResponse.Ok("{\"status\":\"running\"}"));
                case "start-sender":
                case "stop-sender":
                case "start-listener":
                case "stop-listener":
                case "apply-impairment":
                case "export-pcap":
                    return Task.FromResult(KitPipeResponse.Ok("{\"acknowledged\":true}"));
                default:
                    return Task.FromResult(KitPipeResponse.Fail("unknown-command", $"Unknown command: {request.Command}"));
            }
        }

        protected override UDPtestServiceConfig DeserializeSettings(string json)
        {
            return new UDPtestServiceConfig();
        }

        protected override string SerializeSettings(UDPtestServiceConfig settings)
        {
            return "{}";
        }
    }

    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            using var host = new UDPtestServiceHost();
            return await host.RunAsync(args).ConfigureAwait(false);
        }
    }
}
