using System.Text;

namespace LocalServerHub.Windows;

/// <summary>
/// Legacy Windows code pages, which .NET does not register by default.
/// </summary>
/// <remarks>
/// Chinese toolchains still emit GBK on the console, and decoding that as UTF-8
/// produces the mojibake that makes a log unreadable exactly when the user needs
/// it (plan.md §4.2).
/// </remarks>
internal static class CodePageEncodings
{
    private static readonly Lazy<Encoding> LazyGbk = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
    });

    public static Encoding Gbk => LazyGbk.Value;
}
