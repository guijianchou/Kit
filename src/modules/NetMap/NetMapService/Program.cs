// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.NetMapService
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using ManagedCommon;
    using ManagedCommon.ServiceHost;
    using NetMapLib;

    public sealed class NetMapServiceConfig
    {
        public bool IsEnabled { get; set; } = true;
    }

    public sealed class NetMapServiceHost : KitModuleServiceBase<NetMapServiceConfig>
    {
        public override string ModuleKey => "NetMap";
        public override string ServiceExeName => "Kit.NetMapService.exe";
        public override string ExitEventName => @"Local\Kit_NetMap_Exit_Event_7f8e9d0d";
        public override string RefreshSettingsEventName => @"Local\Kit_NetMap_RefreshSettings_Event_7f8e9d0e";

        private NetMapSession? _session;

        protected override Task StartServiceAsync(NetMapServiceConfig initialSettings, CancellationToken ct)
        {
            Logger.LogInfo("[NetMapService] Service started.");
            _session = new NetMapSession();
            return Task.CompletedTask;
        }

        protected override Task StopServiceAsync()
        {
            Logger.LogInfo("[NetMapService] Service stopping.");
            if (_session != null)
            {
                _session.Dispose();
                _session = null;
            }

            return Task.CompletedTask;
        }

        protected override Task<KitPipeResponse> HandlePipeCommandAsync(KitPipeRequest request, CancellationToken ct)
        {
            switch (request.Command.ToLowerInvariant())
            {
                case "get-topology":
                case "get-status":
                    return Task.FromResult(KitPipeResponse.Ok("{\"status\":\"running\"}"));
                case "start-scan":
                case "cancel-scan":
                case "scan-host-ports":
                case "send-wol":
                case "run-traceroute":
                    return Task.FromResult(KitPipeResponse.Ok("{\"acknowledged\":true}"));
                default:
                    return Task.FromResult(KitPipeResponse.Fail("unknown-command", $"Unknown command: {request.Command}"));
            }
        }

        protected override NetMapServiceConfig DeserializeSettings(string json)
        {
            return new NetMapServiceConfig();
        }

        protected override string SerializeSettings(NetMapServiceConfig settings)
        {
            return "{}";
        }
    }

    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            using var host = new NetMapServiceHost();
            return await host.RunAsync(args).ConfigureAwait(false);
        }
    }
}
