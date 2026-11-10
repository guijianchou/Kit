// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Interfaces;
using Kit.Settings.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace ViewModelTests;

[TestClass]
public class GeneralBackupCommands
{
    [TestMethod]
    public async Task PendingPickerBlocksDuplicatePickerBackupAndRestoreUntilTheLocationSettles()
    {
        var selection = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int pickerCalls = 0;
        using var viewModel = new BackupViewModel(() =>
        {
            pickerCalls++;
            return selection.Task;
        });
        var pending = viewModel.SelectSettingBackupDirAsync();

        Assert.IsFalse(pending.IsCompleted);
        foreach (var command in viewModel.BackupCommands)
        {
            Assert.IsFalse(command.CanExecute(null));
            command.Execute(null); // Execute must honor the gate even when called directly.
        }

        Assert.AreEqual(1, pickerCalls);
        Assert.AreEqual(0, viewModel.BackupDirectories.Count);
        Assert.AreEqual(0, viewModel.RestoreDirectories.Count);
        Assert.AreEqual("original-location", viewModel.SettingsBackupAndRestoreDir);

        selection.SetResult("selected-location");
        await pending;
        Assert.AreEqual("selected-location", viewModel.SettingsBackupAndRestoreDir);
        Assert.IsTrue(viewModel.BackupCommands.All(command => command.CanExecute(null)));

        await viewModel.BackupConfigsAsync();
        await viewModel.RestoreConfigsAsync();
        Assert.AreEqual(1, viewModel.BackupDirectories.Count);
        Assert.AreEqual("selected-location", viewModel.BackupDirectories[0]);
        Assert.AreEqual(1, viewModel.RestoreDirectories.Count);
        Assert.AreEqual("selected-location", viewModel.RestoreDirectories[0]);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public async Task CancelledPickerPreservesTheLocationAndDoesNotRunBackupOrRestore(string selectedPath)
    {
        using var viewModel = new BackupViewModel(() => Task.FromResult(selectedPath));
        int notifications = 0;
        viewModel.PropertyChanged += (_, _) => notifications++;

        await viewModel.SelectSettingBackupDirAsync();

        Assert.AreEqual("original-location", viewModel.SettingsBackupAndRestoreDir);
        Assert.AreEqual(0, viewModel.BackupDirectories.Count);
        Assert.AreEqual(0, viewModel.RestoreDirectories.Count);
        Assert.AreEqual(0, notifications);
        Assert.IsTrue(viewModel.BackupCommands.All(command => command.CanExecute(null)));
    }

    [TestMethod]
    public async Task PickerFailureIsObservedByTheCommandAndAllowsAnotherSelection()
    {
        int attempts = 0;
        using var viewModel = new BackupViewModel(() => ++attempts == 1
            ? Task.FromException<string>(new IOException("Synthetic folder picker failure."))
            : Task.FromResult("retry-location"));

        // The real ICommand entry point observes the asynchronous failure rather
        // than posting an unhandled async-void exception to the UI context.
        viewModel.SelectSettingBackupDirEventHandler.Execute(null);
        Assert.AreEqual(1, attempts);
        Assert.AreEqual("original-location", viewModel.SettingsBackupAndRestoreDir);
        Assert.IsTrue(viewModel.SettingsBackupRestoreMessageVisible);
        Assert.AreEqual("Error", viewModel.BackupRestoreMessageSeverity);
        Assert.AreEqual("General_SettingsBackupAndRestore_BackupError", viewModel.SettingsBackupMessage);
        Assert.IsTrue(viewModel.BackupCommands.All(command => command.CanExecute(null)));

        await viewModel.SelectSettingBackupDirAsync();
        Assert.AreEqual(2, attempts);
        Assert.AreEqual("retry-location", viewModel.SettingsBackupAndRestoreDir);
        Assert.AreEqual(0, viewModel.BackupDirectories.Count);
        Assert.AreEqual(0, viewModel.RestoreDirectories.Count);
    }

    [TestMethod]
    public void BackupAndRestoreShareOneSingletonAcrossConcurrentCallers()
    {
        var instances = new SettingsBackupAndRestoreUtils[64];
        Parallel.For(0, instances.Length, index => instances[index] = SettingsBackupAndRestoreUtils.Instance);
        Assert.IsTrue(instances.All(instance => ReferenceEquals(instance, instances[0])));
    }

    private sealed class BackupViewModel : GeneralViewModel
    {
        public BackupViewModel(Func<Task<string>> picker)
            : base(CreateRepository(), "admin", "user", false, false, _ => 0, _ => 0, _ => 0, pickSingleFolderDialog: picker)
        {
        }

        // Isolate these command sequencing tests from the user's registry and
        // settings files while exercising the real commands and picker await.
        public override string SettingsBackupAndRestoreDir { get; set; } = "original-location";

        public List<string> BackupDirectories { get; } = new();

        public List<string> RestoreDirectories { get; } = new();

        public ICommand[] BackupCommands => new[] { SelectSettingBackupDirEventHandler, BackupConfigsEventHandler, RestoreConfigsEventHandler };

        protected override void BackupConfigsClick() => BackupDirectories.Add(SettingsBackupAndRestoreDir);

        protected override void RestoreConfigsClick() => RestoreDirectories.Add(SettingsBackupAndRestoreDir);

        protected override Microsoft.UI.Dispatching.DispatcherQueue GetDispatcherQueue() => null;

        private static ISettingsRepository<GeneralSettings> CreateRepository()
        {
            var repository = new Mock<ISettingsRepository<GeneralSettings>>();
            repository.SetupProperty(value => value.SettingsConfig, new GeneralSettings());
            return repository.Object;
        }
    }
}
