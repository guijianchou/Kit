// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Win32;

namespace Kit.WingetService;

internal static class WingetNotificationRegistration
{
    internal const string ApplicationId = "Microsoft.KitWin32";
    internal const string ActivatorId = "{DD5CACDA-7C2E-4997-A62A-04A597B58F76}";
    internal const string ApplicationKey = @"Software\Classes\AppUserModelId\" + ApplicationId;
    internal const string ComServerKey = @"Software\Classes\CLSID\" + ActivatorId + @"\LocalServer32";
    internal const string ProtocolKey = @"Software\Classes\kit";
    private const string OwnerValue = "KitPortableNotificationOwner";

    internal static void EnsureRegistered(string installDirectory)
    {
        string executable = Path.GetFullPath(Path.Combine(installDirectory, "Kit.exe"));
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("Kit.exe is required to activate Winget notifications.", executable);
        }

        string comCommand = Command(executable, " -ToastActivated");
        string protocolCommand = Command(executable, " \"%1\"");
        string? previousOwner = ReadValue(ApplicationKey, OwnerValue);
        string? existingActivator = ReadValue(ApplicationKey, "CustomActivator");
        if ((!string.IsNullOrEmpty(existingActivator) && !string.Equals(existingActivator, ActivatorId, StringComparison.OrdinalIgnoreCase))
            || !CanReplaceCommand(ReadValue(ComServerKey, string.Empty), comCommand, previousOwner, " -ToastActivated")
            || !CanReplaceCommand(ReadValue(ProtocolKey + @"\shell\open\command", string.Empty), protocolCommand, previousOwner, " \"%1\""))
        {
            throw new InvalidOperationException("Kit notification activation is registered to another application or installation.");
        }

        // Portable Kit has no installer to register its Win32 notification identity.
        // Reuse the existing Runner COM activator and protocol handler, at current-user
        // scope only. Protocol activation opens Winget; it never installs an update.
        using (var com = Registry.CurrentUser.CreateSubKey(ComServerKey, writable: true))
        {
            com.SetValue(string.Empty, comCommand, RegistryValueKind.String);
        }

        using (var protocol = Registry.CurrentUser.CreateSubKey(ProtocolKey, writable: true))
        {
            protocol.SetValue(string.Empty, "URL:Kit Protocol", RegistryValueKind.String);
            protocol.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);
            using var icon = protocol.CreateSubKey("DefaultIcon", writable: true);
            icon.SetValue(string.Empty, Command(executable, ",0"), RegistryValueKind.String);
            using var command = protocol.CreateSubKey(@"shell\open\command", writable: true);
            command.SetValue(string.Empty, protocolCommand, RegistryValueKind.String);
        }

        using var app = Registry.CurrentUser.CreateSubKey(ApplicationKey, writable: true);
        if (app.GetValue("DisplayName") is not string displayName || string.IsNullOrWhiteSpace(displayName))
        {
            app.SetValue("DisplayName", "Kit", RegistryValueKind.String);
        }

        string kitIcon = Path.Combine(installDirectory, "WinUI3Apps", "Assets", "Settings", "Icons", "Kit.png");
        string iconPath = File.Exists(kitIcon) ? kitIcon : Path.Combine(installDirectory, "Assets", "Winget.png");
        if (File.Exists(iconPath))
        {
            app.SetValue("IconUri", Path.GetFullPath(iconPath), RegistryValueKind.String);
        }

        app.SetValue("CustomActivator", ActivatorId, RegistryValueKind.String);
        app.SetValue(OwnerValue, executable, RegistryValueKind.String);
    }

    internal static string Command(string executable, string suffix) => "\"" + executable + "\"" + suffix;

    internal static bool CanReplaceCommand(string? existing, string desired, string? previousOwner, string suffix)
        => string.IsNullOrEmpty(existing)
            || string.Equals(existing, desired, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(previousOwner)
                && string.Equals(existing, Command(previousOwner, suffix), StringComparison.OrdinalIgnoreCase));

    private static string? ReadValue(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
        return key?.GetValue(name) as string;
    }
}
