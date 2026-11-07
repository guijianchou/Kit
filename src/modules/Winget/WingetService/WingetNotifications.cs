// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Security;
using Kit.WingetLib;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Kit.WingetService;

internal interface IWingetUpdateNotifier
{
    string? Show(IReadOnlyList<WingetPackageUpdate> packages);
}

internal sealed class WingetUpdateNotifier : IWingetUpdateNotifier
{
    public string? Show(IReadOnlyList<WingetPackageUpdate> packages)
    {
        if (packages.Count == 0)
        {
            return null;
        }

        bool chinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
        try
        {
            WingetNotificationRegistration.EnsureRegistered(AppContext.BaseDirectory);
            string title = chinese ? "Winget 应用更新" : "Winget application updates";
            string message = chinese ? $"发现 {packages.Count} 个可用更新。打开 Kit 查看并选择更新。" : $"{packages.Count} updates are available. Open Kit to review and choose updates.";
            string button = chinese ? "在 Kit 中查看" : "Review in Kit";
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Winget.png");
            string image = File.Exists(iconPath)
                ? "<image placement=\"appLogoOverride\" src=\"" + SecurityElement.Escape(new Uri(iconPath).AbsoluteUri) + "\"/>" : string.Empty;
            var xml = new XmlDocument();
            xml.LoadXml("<toast activationType=\"protocol\" launch=\"kit://open_winget/\"><visual><binding template=\"ToastGeneric\">"
                + image + "<text>" + SecurityElement.Escape(title) + "</text><text>" + SecurityElement.Escape(message)
                + "</text></binding></visual><actions><action activationType=\"protocol\" arguments=\"kit://open_winget/\" content=\""
                + SecurityElement.Escape(button) + "\"/></actions></toast>");
            var notifier = ToastNotificationManager.CreateToastNotifier(WingetNotificationRegistration.ApplicationId);

            // For an unpackaged app, querying Setting before its first Show can
            // fail with ERROR_NOT_FOUND because Windows has no notification entry.
            notifier.Show(new ToastNotification(xml) { Tag = "WingetUpdates", Group = "KitToastTag" });
            if (notifier.Setting != NotificationSetting.Enabled)
            {
                return chinese ? "Windows 已关闭 Kit 的通知。" : "Windows notifications are disabled for Kit.";
            }

            return null;
        }
        catch (Exception ex)
        {
            return FormatError(ex, chinese);
        }
    }

    internal static string FormatError(Exception exception, bool chinese)
    {
        string prefix = chinese ? "Windows 通知失败" : "Windows notification failed";
        string detail = string.IsNullOrWhiteSpace(exception.Message)
            ? $"{exception.GetType().Name}, 0x{exception.HResult:X8}" : exception.Message;
        return $"{prefix}: {detail}";
    }
}
