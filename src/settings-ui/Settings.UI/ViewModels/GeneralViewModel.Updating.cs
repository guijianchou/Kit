// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Helpers;
using Kit.Settings.UI.Library.ViewModels.Commands;
using Kit.Settings.UI.SerializationContext;
using ManagedCommon;

namespace Kit.Settings.UI.ViewModels
{
    public partial class GeneralViewModel
    {
        private bool _portableUpdatePending;
        private int _portableUpdateRequest;
        private int _updateRefreshRequest;

        public ButtonClickCommand DownloadUpdateCommand { get; private set; }

        public ButtonClickCommand RestartToUpdateCommand { get; private set; }

        public ButtonClickCommand CancelUpdateDownloadCommand { get; private set; }

        public bool IsPortableUpdateBusy => _portableUpdatePending || PowerToysUpdatingState is UpdatingSettings.UpdatingState.Downloading or UpdatingSettings.UpdatingState.Verifying or UpdatingSettings.UpdatingState.Installing;

        public bool CanDownloadPortableUpdate => !IsPortableUpdateBusy && !IsCheckingForUpdates && UpdatingSettingsConfig.ErrorCode != "recoveryRequired" && PowerToysNewAvailableVersionUri != null &&
            PowerToysUpdatingState is UpdatingSettings.UpdatingState.ReadyToDownload or UpdatingSettings.UpdatingState.ErrorDownloading or UpdatingSettings.UpdatingState.NetworkError;

        public bool CanRestartToUpdate => !IsPortableUpdateBusy && !IsCheckingForUpdates && (PowerToysUpdatingState == UpdatingSettings.UpdatingState.ReadyToInstall || UpdatingSettingsConfig.ErrorCode == "recoveryRequired");

        public bool CanCancelUpdateDownload => PowerToysUpdatingState == UpdatingSettings.UpdatingState.Downloading ||
            (PowerToysUpdatingState == UpdatingSettings.UpdatingState.Verifying && string.IsNullOrEmpty(UpdatingSettingsConfig.DownloadedInstallerFilename));

        public bool IsUpdateProgressIndeterminate => PowerToysUpdatingState != UpdatingSettings.UpdatingState.Downloading || UpdatingSettingsConfig.TotalBytes <= 0;

        public double UpdateDownloadPercent => UpdatingSettingsConfig.TotalBytes > 0 ? Math.Clamp(100.0 * UpdatingSettingsConfig.DownloadedBytes / UpdatingSettingsConfig.TotalBytes, 0, 100) : 0;

        public string UpdateDownloadSummary => PowerToysUpdatingState == UpdatingSettings.UpdatingState.Downloading && UpdatingSettingsConfig.TotalBytes > 0 ?
            string.Format(CultureInfo.CurrentCulture, "{0:0.0} / {1:0.0} MB · {2:0}%", UpdatingSettingsConfig.DownloadedBytes / 1048576.0, UpdatingSettingsConfig.TotalBytes / 1048576.0, UpdateDownloadPercent) : string.Empty;

        public string RestartUpdateLabel => GetResourceString(UpdatingSettingsConfig.ErrorCode == "recoveryRequired" ? "General_UpdateRecover/Content" : "General_UpdateRestart/Content");

        private void InitializePortableUpdateCommands()
        {
            DownloadUpdateCommand = new ButtonClickCommand(() =>
            {
                if (CanDownloadPortableUpdate)
                {
                    SendPortableUpdateAction("download_update", true);
                }
            });
            RestartToUpdateCommand = new ButtonClickCommand(() =>
            {
                if (CanRestartToUpdate)
                {
                    SendPortableUpdateAction("restart_to_update", true);
                }
            });
            CancelUpdateDownloadCommand = new ButtonClickCommand(() =>
            {
                if (CanCancelUpdateDownload)
                {
                    SendPortableUpdateAction("cancel_update_download", false);
                }
            });
        }

        private void SendPortableUpdateAction(string action, bool pending)
        {
            try
            {
                if (pending)
                {
                    _portableUpdatePending = true;
                    NotifyPortableUpdateProperties();
                }

                var message = JsonSerializer.Serialize(ActionMessage.Create(action), SourceGenerationContextContext.Default.ActionMessage);
                SendCheckForUpdatesConfigMSG(message);
                if (pending)
                {
                    var request = ++_portableUpdateRequest;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(15000).ConfigureAwait(false);
                        RunOnViewModelThread(() =>
                        {
                            if (request != _portableUpdateRequest || !_portableUpdatePending)
                            {
                                return;
                            }
                            _portableUpdatePending = false;
                            NotifyPortableUpdateProperties();
                            ShowUpdateCheckMessage(GetResourceString("General_UpdateError/Text"), "Error", true);
                        });
                    });
                }
            }
            catch (Exception error)
            {
                _portableUpdatePending = false;
                NotifyPortableUpdateProperties();
                Logger.LogError("Could not send Portable update action.", error);
                if (pending)
                {
                    ShowUpdateCheckMessage(GetResourceString("General_UpdateError/Text"), "Error", true);
                }
            }
        }

        private void RefreshPortableUpdateProperties(UpdatingSettings settings)
        {
            _portableUpdatePending = false;
            IsNewVersionDownloading = settings.State == UpdatingSettings.UpdatingState.Downloading;
            NotifyPortableUpdateProperties();
        }

        private void NotifyPortableUpdateProperties()
        {
            OnPropertyChanged(nameof(IsPortableUpdateBusy));
            OnPropertyChanged(nameof(IsCheckForUpdatesButtonEnabled));
            OnPropertyChanged(nameof(CanDownloadPortableUpdate));
            OnPropertyChanged(nameof(CanRestartToUpdate));
            OnPropertyChanged(nameof(CanCancelUpdateDownload));
            OnPropertyChanged(nameof(IsUpdateProgressIndeterminate));
            OnPropertyChanged(nameof(UpdateDownloadPercent));
            OnPropertyChanged(nameof(UpdateDownloadSummary));
            OnPropertyChanged(nameof(RestartUpdateLabel));
        }

        private string PortableUpdateErrorMessage(string code) => GetResourceString(code switch
        {
            "hashMismatch" or "invalidPackage" or "invalidRelease" => "General_UpdateInvalid/Text",
            "accessDenied" or "unsafePath" => "General_UpdateAccess/Text",
            "closeFailed" => "General_UpdateCloseFailed/Text",
            "recoveryRequired" => "General_UpdateRecoveryRequired/Text",
            "recovered" => "General_UpdateRecovered/Text",
            "interrupted" => "General_UpdateInterrupted/Text",
            "helperMissing" => "General_UpdateHelperMissing/Text",
            _ => "General_UpdateError/Text",
        });
    }
}
