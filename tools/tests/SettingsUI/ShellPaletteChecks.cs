using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kit.Settings.UI;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.UI;

internal static class ShellPaletteChecks
{
    public static async Task RunAsync(string output)
    {
        var window = App.GetSettingsWindow();
        var shell = window.Content as ShellPage ?? Descendants(window.Content).OfType<ShellPage>().Single();
        var navigation = (NavigationView)shell.FindName("navigationView");
        var root = (Grid)shell.FindName("RootGrid");
        var themeBefore = shell.RequestedTheme;
        var sizeBefore = window.AppWindow.Size;
        var selectedBefore = navigation.SelectedItem;
        var paneBefore = navigation.IsPaneOpen;
        var pageBefore = NavigationService.Frame.Content;
        string report = Path.Combine(output, "shell-palette.log");
        File.WriteAllText(report, "Windows Settings-style navigation: neutral native rows, existing module logos in their original colors, native interaction states and full-shell renders. No item invocation or business commands.\n");
        bool highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        var expectedIds = new[] { "AIHubNavItem", "AwakeNavItem", "LightSwitchNavItem", "LocalserverNavItem", "NetMapNavItem", "UDPtestNavItem", "WingetNavItem" };
        var modules = navigation.MenuItems.OfType<NavigationViewItem>().Where(item => expectedIds.Contains(AutomationProperties.GetAutomationId(item))).ToArray();
        Require(modules.Length == 7, "Shell palette must cover all seven module navigation entries.");
        var dashboard = navigation.MenuItems.OfType<NavigationViewItem>().First();
        var firstLightColors = new Dictionary<string, Color>();
        var logoNames = new[] { "AiHub", "Awake", "LightSwitch", "Localserver", "NetMap", "UDPtest", "Winget" };
        try
        {
            if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
                Require(window.SystemBackdrop is MicaBackdrop, "Supported Windows 11 shell must retain a real Mica backdrop.");

            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
            {
                shell.RequestedTheme = theme;
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1000));
                navigation.IsPaneOpen = true;
                await Render();
                await Render();
                Require(navigation.DisplayMode == NavigationViewDisplayMode.Expanded, "Wide shell must use the expanded native navigation pane.");
                // SplitView animates after the display-mode transition; the first
                // rendering callback can still expose the 40-DIP compact row.
                navigation.IsPaneOpen = true;
                var expansionDeadline = DateTime.UtcNow.AddSeconds(3);
                while (modules.Any(item => item.ActualWidth < 100) && DateTime.UtcNow < expansionDeadline) await Render();
                Require(modules.All(item => item.ActualWidth >= 100), "Expanded navigation must finish opening before row checks.");
                var distinctTextColors = new HashSet<Color>();
                Color backdrop = highContrast
                    ? (Color)Application.Current.Resources["SystemColorWindowColor"]
                    : theme == ElementTheme.Dark ? Color.FromArgb(255, 40, 40, 40) : Color.FromArgb(255, 238, 238, 238);
                foreach (var item in modules)
                {
                    string id = AutomationProperties.GetAutomationId(item);
                    navigation.SelectedItem = dashboard;
                    await Render();
                    var label = Descendants(item).OfType<TextBlock>().FirstOrDefault(text => text.Text == Convert.ToString(item.Content) && text.ActualWidth > 0);
                    Require(label != null, id + " must render its localized title in the native item template.");
                    GoToState(item, "Normal");
                    var normal = BrushColor(label.Foreground);
                    var normalFill = CheckRowState(item, dashboard, label, "Normal", backdrop);
                    distinctTextColors.Add(normal);
                    Require(!item.Resources.Keys.Any(key => Convert.ToString(key).StartsWith("NavigationViewItem", StringComparison.Ordinal)), id + " must use native hover and selected resources.");
                    await CheckLogo(item, logoNames[Array.IndexOf(modules, item)]);
                    if (theme == ElementTheme.Light)
                    {
                        if (firstLightColors.TryGetValue(id, out Color previous)) Require(previous == normalFill, id + " must restore its light row fill after the dark theme.");
                        else firstLightColors[id] = normalFill;
                    }
                    foreach (string state in new[] { "PointerOver", "Pressed" })
                    {
                        CheckRowState(item, dashboard, label, state, backdrop);
                    }
                    navigation.SelectedItem = item;
                    await Render();
                    foreach (string state in new[] { "Selected", "PointerOverSelected", "PressedSelected" })
                    {
                        CheckRowState(item, dashboard, label, state, backdrop);
                    }
                    GoToState(item, "Selected");
                    Require(item.Focus(FocusState.Keyboard) && item.FocusState == FocusState.Keyboard, id + " must retain keyboard focus support.");
                    File.AppendAllText(report, $"{theme} {id}: neutral text={normal}, native row={normalFill}, original logo, native hover/pressed/selection, contrast and keyboard focus PASS\n");
                }
                Require(distinctTextColors.Count == 1, "Module titles must use one neutral native foreground.");
                Require(!navigation.Resources.Keys.Any(key => string.Equals(Convert.ToString(key), "NavigationViewExpandedPaneBackground", StringComparison.Ordinal)),
                    "The pane must retain its native Mica surface without a locally overridden colored wash.");
                Require(dashboard.Icon is FontIcon homeIcon && homeIcon.Glyph == "\uE80F", "Home must retain its recognizable native house icon.");
                navigation.SelectedItem = modules[0];
                GoToState(dashboard, "Normal");
                modules[0].Focus(FocusState.Programmatic);
                // NavigationView animates its compositor selection indicator.
                // Capture its resting position, rather than the previous item.
                await Task.Delay(350);
                await Render();
                await Capture(shell, root, Path.Combine(output, $"shell-palette-{theme}-expanded.png"));
            }
            foreach (int width in new[] { 760, 480 })
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 800));
                navigation.IsPaneOpen = false;
                await Render();
                await Render();
                Require(navigation.DisplayMode != NavigationViewDisplayMode.Expanded, "Narrow shell must use native compact/minimal navigation.");
                await Task.Delay(350);
                shell.UpdateLayout();
                await Render();
                await Capture(shell, root, Path.Combine(output, $"shell-palette-Light-{width}-collapsed.png"));
                navigation.IsPaneOpen = true;
                await Task.Delay(350);
                shell.UpdateLayout();
                await Render();
                Require(modules.All(item => item.ActualWidth > 0 && item.IsEnabled), "All module entries must remain reachable when reopening the narrow pane.");
                await Capture(shell, root, Path.Combine(output, $"shell-palette-Light-{width}-open.png"));
            }
            Require(ReferenceEquals(pageBefore, NavigationService.Frame.Content), "Palette checks must not invoke a module or replace the current page.");
            File.AppendAllText(report, "PASS; OS high contrast active=" + highContrast + ". Captures use an opaque representative base because RenderTargetBitmap excludes compositor Mica.\n");
        }
        finally
        {
            navigation.SelectedItem = selectedBefore;
            shell.RequestedTheme = themeBefore;
            window.AppWindow.Resize(sizeBefore);
            navigation.IsPaneOpen = paneBefore;
        }
    }

    private static void GoToState(NavigationViewItem item, string state)
    {
        foreach (var control in new[] { (Control)item }.Concat(Descendants(item).OfType<Control>()))
        {
            if (VisualStateManager.GoToState(control, state, false)) return;
        }
        throw new InvalidOperationException("The real navigation template did not expose state " + state + ".");
    }

    private static Color CheckRowState(NavigationViewItem item, NavigationViewItem nativeReference, TextBlock label, string state, Color backdrop)
    {
        string id = AutomationProperties.GetAutomationId(item);
        GoToState(item, state);
        GoToState(nativeReference, state);
        var referenceLabel = Descendants(nativeReference).OfType<TextBlock>().First(text => text.Text == Convert.ToString(nativeReference.Content) && text.ActualWidth > 0);
        Color foreground = BrushColor(label.Foreground);
        Require(foreground == BrushColor(referenceLabel.Foreground), id + " must retain the native neutral title in " + state + ".");
        var row = Descendants(item).OfType<Grid>().First(grid => grid.Name == "LayoutRoot");
        var referenceRow = Descendants(nativeReference).OfType<Grid>().First(grid => grid.Name == "LayoutRoot");
        Require(row.ActualWidth > 100 && row.Background is not GradientBrush,
            id + " must retain the native row without decorative strips; state=" + state + ", width=" + row.ActualWidth + ", background=" + row.Background?.GetType().Name + ".");
        Color fill = BrushColor(row.Background);
        Require(fill == BrushColor(referenceRow.Background), id + " must use the same native row surface in " + state + ".");
        Require(Contrast(foreground, Composite(fill, backdrop)) >= 4.5, id + " title contrast is below 4.5:1 in " + state + ".");
        return fill;
    }

    private static async Task CheckLogo(NavigationViewItem item, string name)
    {
        Require(item.Icon is ImageIcon, name + " must use its existing original logo.");
        var icon = (ImageIcon)item.Icon;
        Require(icon.Source is BitmapImage bitmap && bitmap.UriSource.AbsoluteUri.EndsWith("/Assets/Settings/Navigation/" + name + ".png", StringComparison.Ordinal),
            name + " logo must use the navigation crop of the existing module artwork.");
        var source = (BitmapImage)icon.Source;
        var imageDeadline = DateTime.UtcNow.AddSeconds(3);
        while (source.PixelWidth == 0 && DateTime.UtcNow < imageDeadline) await Render();
        Require(source.PixelWidth == 64 && source.PixelHeight == 64, name + " logo must finish decoding its navigation asset.");
        Require(icon.ActualWidth > 0 && icon.ActualHeight > 0 && icon.Width == 20 && icon.Height == 20, name + " logo must render at the navigation icon size.");
        var rendered = new RenderTargetBitmap();
        await rendered.RenderAsync(icon);
        var pixels = await rendered.GetPixelsAsync();
        var bytes = new byte[pixels.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
        int coloredPixels = 0;
        int visiblePixels = 0;
        for (int index = 0; index + 3 < bytes.Length; index += 4)
        {
            int maximum = Math.Max(bytes[index], Math.Max(bytes[index + 1], bytes[index + 2]));
            int minimum = Math.Min(bytes[index], Math.Min(bytes[index + 1], bytes[index + 2]));
            // Small line-art assets (UDP Test) rely on antialiased, translucent edges.
            if (bytes[index + 3] > 16) visiblePixels++;
            if (bytes[index + 3] > 16 && maximum - minimum > 12) coloredPixels++;
        }
        Require(visiblePixels > 8, name + " logo must visibly render rather than being blank or transparent; pixels=" + visiblePixels + ".");
        if (name != "LightSwitch") Require(coloredPixels > 4, name + " logo must preserve its original color rather than becoming a monochrome glyph; pixels=" + coloredPixels + ".");
    }

    private static Color BrushColor(Brush brush)
    {
        if (brush == null) return Color.FromArgb(0, 0, 0, 0);
        Require(brush is SolidColorBrush, "Native navigation text must resolve to a solid brush.");
        Color color = ((SolidColorBrush)brush).Color;
        return Color.FromArgb((byte)Math.Round(color.A * brush.Opacity), color.R, color.G, color.B);
    }

    private static Color Composite(Color color, Color background)
    {
        double alpha = color.A / 255.0;
        return Color.FromArgb(255, (byte)Math.Round(color.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(color.G * alpha + background.G * (1 - alpha)), (byte)Math.Round(color.B * alpha + background.B * (1 - alpha)));
    }

    private static double Contrast(Color foreground, Color background)
    {
        static double Linear(byte value) => value / 255.0 <= 0.04045 ? value / 255.0 / 12.92 : Math.Pow((value / 255.0 + 0.055) / 1.055, 2.4);
        static double Luminance(Color color) => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        double text = Luminance(Composite(foreground, background));
        double fill = Luminance(background);
        return (Math.Max(text, fill) + 0.05) / (Math.Min(text, fill) + 0.05);
    }

    private static async Task Render()
    {
        var rendered = new TaskCompletionSource<bool>();
        EventHandler<object> handler = (_, _) => rendered.TrySetResult(true);
        CompositionTarget.Rendering += handler;
        try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= handler; }
    }

    private static async Task Capture(ShellPage shell, Grid root, string path)
    {
        var background = root.Background;
        root.Background = new SolidColorBrush(shell.ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243));
        var bitmap = new RenderTargetBitmap();
        try { await bitmap.RenderAsync(shell); }
        finally { root.Background = background; }
        var pixels = await bitmap.GetPixelsAsync();
        var bytes = new byte[pixels.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
        using var encoded = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
        var png = new byte[encoded.Size];
        using (var reader = new Windows.Storage.Streams.DataReader(encoded.GetInputStreamAt(0))) { await reader.LoadAsync((uint)encoded.Size); reader.ReadBytes(png); }
        File.WriteAllBytes(path, png);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
