using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kit.Settings.UI;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

internal static class NavigationPanePersistenceChecks
{
    public static async Task RunAsync(string output, string phase)
    {
        var window = App.GetSettingsWindow();
        var shell = window.Content as ShellPage ?? Descendants(window.Content).OfType<ShellPage>().Single();
        var navigation = (NavigationView)shell.FindName("navigationView");
        string report = Path.Combine(output, "navigation-pane-" + phase + ".log");
        bool expectedOnStartup = phase != "restore-collapsed";
        File.WriteAllText(report, "Fresh Settings process: " + phase + "\n");

        await Resize(1280, NavigationViewDisplayMode.Expanded);
        Require(navigation.IsPaneOpen == expectedOnStartup, "The fresh process did not restore the persisted wide-window preference.");
        Require(ReadPreference() == expectedOnStartup, "Startup changed the persisted preference.");
        File.AppendAllText(report, "PASS startup restored " + expectedOnStartup + "\n");

        bool expectedAfterClick = phase == "collapse" ? false : true;
        if (phase != "restore-expanded")
        {
            var toggle = Descendants(shell).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "TitleBar_PaneToggleButton");
            new ButtonAutomationPeer(toggle).Invoke();
            await Settle();
            Require(navigation.IsPaneOpen == expectedAfterClick, "The real title-bar button failed to toggle the pane.");
            Require(ReadPreference() == expectedAfterClick, "The title-bar button did not persist its new preference.");
            File.AppendAllText(report, "PASS real title-bar button persisted " + expectedAfterClick + "\n");
        }

        await Resize(800, NavigationViewDisplayMode.Compact);
        Require(!navigation.IsPaneOpen, "Compact layout must close the pane automatically.");
        Require(ReadPreference() == expectedAfterClick, "Compact layout overwrote user intent.");
        await Resize(520, NavigationViewDisplayMode.Minimal);
        Require(!navigation.IsPaneOpen, "Minimal layout must close the pane automatically.");
        Require(ReadPreference() == expectedAfterClick, "Minimal layout overwrote user intent.");
        await Resize(1280, NavigationViewDisplayMode.Expanded);
        Require(navigation.IsPaneOpen == expectedAfterClick, "Returning to wide layout did not restore user intent.");
        Require(ReadPreference() == expectedAfterClick, "Responsive layout rewrote the persisted preference.");
        File.AppendAllText(report, "PASS Compact -> Minimal -> Expanded retains " + expectedAfterClick + "\nPASS\n");

        async Task Resize(int width, NavigationViewDisplayMode mode)
        {
            double scale = shell.XamlRoot.RasterizationScale;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Ceiling(width * scale), (int)Math.Ceiling(800 * scale)));
            await Settle();
            Require(navigation.DisplayMode == mode, "Actual NavigationView display mode mismatch: expected " + mode + ", got " + navigation.DisplayMode);
        }
    }

    private static bool ReadPreference() => SettingsUtils.Default.GetSettings<SettingsUiPreferences>(fileName: SettingsUiPreferences.FileName).NavigationPaneOpen;

    private static async Task Settle()
    {
        // Include the native SplitView animation and the queued Auto-mode restoration.
        await Task.Delay(450);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root == null) yield break;
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var item in Descendants(VisualTreeHelper.GetChild(root, index))) yield return item;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
