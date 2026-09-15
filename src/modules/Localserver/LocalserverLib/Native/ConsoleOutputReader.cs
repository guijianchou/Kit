using System.Text;

namespace LocalServerHub.Windows;

public static class ConsoleOutputReader
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static Encoding ResolveEncoding(string name) => name.ToLowerInvariant() switch
    {
        "auto" or "utf-8" or "utf8" => new UTF8Encoding(false),
        "gbk" or "gb2312" or "936" => CodePageEncodings.Gbk,
        _ => throw new InvalidOperationException("Console encoding must be auto, utf-8 or gbk."),
    };

    public static async Task ReadAsync(Stream stream, string encodingName, Action<string> onLine,
        CancellationToken cancellationToken = default)
    {
        Encoding encoding = ResolveEncoding(encodingName);
        bool auto = encodingName.Equals("auto", StringComparison.OrdinalIgnoreCase);
        byte[] input = new byte[4096];
        byte[] line = new byte[65536];
        int length = 0;
        bool truncated = false;
        bool afterCr = false;
        bool first = true;
        void Emit()
        {
            string text;
            try { text = (auto ? StrictUtf8 : encoding).GetString(line, 0, length); }
            catch (DecoderFallbackException) { text = CodePageEncodings.Gbk.GetString(line, 0, length); }
            if (first) text = text.TrimStart('\uFEFF');
            first = false;
            onLine(truncated ? text + " [line truncated at 64 KiB]" : text);
            length = 0;
            truncated = false;
        }
        int count;
        while ((count = await stream.ReadAsync(input, cancellationToken).ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < count; i++)
            {
                byte value = input[i];
                if (value == 10 && afterCr) { afterCr = false; continue; }
                afterCr = value == 13;
                if (value is 10 or 13) { Emit(); continue; }
                if (length < line.Length) line[length++] = value;
                else truncated = true;
            }
        }
        if (length > 0 || truncated) Emit();
    }
}
