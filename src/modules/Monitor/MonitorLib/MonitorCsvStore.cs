// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Reads and writes Monitor scan state in the Python-compatible CSV format.
/// </summary>
public static class MonitorCsvStore
{
    private static readonly string[] FieldNames =
    {
        "path", "rel_path", "folder_name", "filename", "sha1sum", "mtime_iso", "file_size",
    };

    /// <summary>
    /// Saves Monitor records to a CSV file.
    /// </summary>
    /// <param name="csvPath">The destination CSV file path.</param>
    /// <param name="records">The records to save.</param>
    public static void Save(string csvPath, IReadOnlyList<MonitorFileRecord> records)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csvPath);
        ArgumentNullException.ThrowIfNull(records);

        string? directoryName = Path.GetDirectoryName(csvPath);
        if (!string.IsNullOrEmpty(directoryName))
        {
            Directory.CreateDirectory(directoryName);
        }

        string temporaryPath = Path.Combine(
            directoryName ?? string.Empty,
            Path.GetFileName(csvPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            WriteCsv(temporaryPath, records);
            if (File.Exists(csvPath))
            {
                File.Replace(temporaryPath, csvPath, null);
            }
            else
            {
                File.Move(temporaryPath, csvPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void WriteCsv(string csvPath, IReadOnlyList<MonitorFileRecord> records)
    {
        using StreamWriter writer = new(csvPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine(string.Join(",", FieldNames));

        foreach (MonitorFileRecord record in records)
        {
            string path = record.FolderName == "~" ? @"~\" + record.FileName : @"~\" + record.FolderName + @"\" + record.FileName;
            string fileSize = record.FileSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            string[] values =
            {
                path,
                record.RelativePath,
                record.FolderName,
                record.FileName,
                record.Sha1 ?? string.Empty,
                record.Timestamp ?? string.Empty,
                fileSize,
            };

            writer.WriteLine(string.Join(",", values.Select(EscapeCsvValue)));
        }
    }

    /// <summary>
    /// Loads Monitor records from a CSV file.
    /// </summary>
    /// <param name="csvPath">The source CSV file path.</param>
    /// <param name="downloadsPath">The Downloads root used to reconstruct full paths.</param>
    /// <param name="settings">The Monitor settings used to resolve categories.</param>
    /// <returns>The records read from the CSV file.</returns>
    public static IReadOnlyList<MonitorFileRecord> Load(string csvPath, string downloadsPath, MonitorSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csvPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadsPath);
        ArgumentNullException.ThrowIfNull(settings);

        if (!File.Exists(csvPath))
        {
            return Array.Empty<MonitorFileRecord>();
        }

        try
        {
            List<MonitorFileRecord> records = new();
            using TextFieldParser parser = new(csvPath, Encoding.UTF8)
            {
                TextFieldType = FieldType.Delimited,
            };

            parser.SetDelimiters(",");
            string[]? headers = parser.ReadFields();
            if (headers is null)
            {
                return records;
            }

            Dictionary<string, int> columnIndex = BuildColumnIndex(headers);
            while (!parser.EndOfData)
            {
                string[]? fields = parser.ReadFields();
                if (fields is null)
                {
                    continue;
                }

                string folderName = GetField(fields, columnIndex, "folder_name");
                string fileName = GetField(fields, columnIndex, "filename");
                string relativePath = GetField(fields, columnIndex, "rel_path");
                if (string.IsNullOrEmpty(relativePath))
                {
                    relativePath = folderName == "~" ? fileName : folderName + "/" + fileName;
                }

                long? fileSize = TryParseFileSize(GetField(fields, columnIndex, "file_size"));
                string fullPath = Path.Combine(downloadsPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                string category = folderName != "~" && settings.Categories.ContainsKey(folderName) ? folderName : MonitorCategoryResolver.ResolveCategory(fileName, settings);

                records.Add(new MonitorFileRecord(
                    "~",
                    folderName,
                    fileName,
                    relativePath,
                    fullPath,
                    GetField(fields, columnIndex, "sha1sum"),
                    GetField(fields, columnIndex, "mtime_iso"),
                    fileSize,
                    category));
            }

            return records;
        }
        catch (IOException)
        {
            return Array.Empty<MonitorFileRecord>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<MonitorFileRecord>();
        }
        catch (MalformedLineException)
        {
            return Array.Empty<MonitorFileRecord>();
        }
    }

    private static Dictionary<string, int> BuildColumnIndex(IReadOnlyList<string> headers)
    {
        Dictionary<string, int> columnIndex = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < headers.Count; index++)
        {
            columnIndex[headers[index]] = index;
        }

        return columnIndex;
    }

    private static string GetField(IReadOnlyList<string> fields, IReadOnlyDictionary<string, int> columnIndex, string fieldName)
    {
        if (!columnIndex.TryGetValue(fieldName, out int index) || index >= fields.Count)
        {
            return string.Empty;
        }

        return fields[index];
    }

    private static long? TryParseFileSize(string value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long fileSize) ? fileSize : null;
    }

    private static string EscapeCsvValue(string value)
    {
        if (!value.Contains(',', StringComparison.Ordinal) &&
            !value.Contains('"', StringComparison.Ordinal) &&
            !value.Contains('\r', StringComparison.Ordinal) &&
            !value.Contains('\n', StringComparison.Ordinal))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
