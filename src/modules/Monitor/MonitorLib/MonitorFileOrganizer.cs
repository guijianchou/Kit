// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Organizes root Downloads files into configured category folders.
/// </summary>
public static class MonitorFileOrganizer
{
    /// <summary>
    /// Organizes files from the root of a Downloads folder into category folders.
    /// </summary>
    /// <param name="downloadsPath">The Downloads root folder.</param>
    /// <param name="settings">The Monitor settings.</param>
    /// <param name="dryRun">True to report planned moves without changing files.</param>
    /// <returns>A summary of the organization run.</returns>
    public static MonitorFileOrganizerResult Organize(string downloadsPath, MonitorSettings settings, bool dryRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadsPath);
        ArgumentNullException.ThrowIfNull(settings);

        DirectoryInfo rootDirectory = new(downloadsPath);
        if (!rootDirectory.Exists)
        {
            return new MonitorFileOrganizerResult(0, 0, 0, 1);
        }

        HashSet<string> excludedFiles = new(settings.ExcludedFiles, StringComparer.OrdinalIgnoreCase);
        int totalFiles = 0;
        int organized = 0;
        int skipped = 0;
        int errors = 0;

        if (!dryRun)
        {
            errors += EnsureCategoryFolders(rootDirectory, settings);
        }

        foreach (FileInfo sourceFile in rootDirectory.EnumerateFiles())
        {
            if (sourceFile.Attributes.HasFlag(FileAttributes.ReparsePoint) || excludedFiles.Contains(sourceFile.Name))
            {
                continue;
            }

            totalFiles++;
            string category = MonitorCategoryResolver.ResolveCategory(sourceFile.Name, settings);
            if (!settings.Categories.ContainsKey(category))
            {
                skipped++;
                continue;
            }

            string destinationFolder = Path.Combine(rootDirectory.FullName, category);
            if (!IsPathInsideRoot(rootDirectory.FullName, destinationFolder))
            {
                errors++;
                continue;
            }

            if (dryRun)
            {
                organized++;
                continue;
            }

            try
            {
                Directory.CreateDirectory(destinationFolder);
                string destinationPath = ResolveAvailablePath(Path.Combine(destinationFolder, sourceFile.Name));
                if (!IsPathInsideRoot(rootDirectory.FullName, destinationPath))
                {
                    errors++;
                    continue;
                }

                File.Move(sourceFile.FullName, destinationPath);
                organized++;
            }
            catch (IOException)
            {
                errors++;
            }
            catch (UnauthorizedAccessException)
            {
                errors++;
            }
        }

        return new MonitorFileOrganizerResult(totalFiles, organized, skipped, errors);
    }

    /// <summary>
    /// Ensures that all configured category folders exist under the Downloads root.
    /// </summary>
    /// <param name="downloadsPath">The Downloads root folder.</param>
    /// <param name="settings">The Monitor settings.</param>
    /// <returns>The number of folder creation errors.</returns>
    public static int EnsureCategoryFolders(string downloadsPath, MonitorSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadsPath);
        ArgumentNullException.ThrowIfNull(settings);

        DirectoryInfo rootDirectory = new(downloadsPath);
        if (!rootDirectory.Exists)
        {
            return 1;
        }

        return EnsureCategoryFolders(rootDirectory, settings);
    }

    private static int EnsureCategoryFolders(DirectoryInfo rootDirectory, MonitorSettings settings)
    {
        int errors = 0;

        foreach (string categoryName in settings.Categories.Keys)
        {
            string destinationFolder = Path.Combine(rootDirectory.FullName, categoryName);
            if (!IsPathInsideRoot(rootDirectory.FullName, destinationFolder))
            {
                errors++;
                continue;
            }

            try
            {
                Directory.CreateDirectory(destinationFolder);
            }
            catch (IOException)
            {
                errors++;
            }
            catch (UnauthorizedAccessException)
            {
                errors++;
            }
            catch (NotSupportedException)
            {
                errors++;
            }
        }

        return errors;
    }

    /// <summary>
    /// Checks whether a target path is inside a root path.
    /// </summary>
    /// <param name="rootPath">The root path.</param>
    /// <param name="targetPath">The target path.</param>
    /// <returns>True when the target is the root or a child of it.</returns>
    public static bool IsPathInsideRoot(string rootPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        string normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedTarget = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(normalizedRoot, normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
            normalizedTarget.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveAvailablePath(string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            return destinationPath;
        }

        string directory = Path.GetDirectoryName(destinationPath) ?? string.Empty;
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(destinationPath);
        string extension = Path.GetExtension(destinationPath);
        int counter = 1;

        string candidatePath;
        do
        {
            candidatePath = Path.Combine(directory, fileNameWithoutExtension + "_" + counter.ToString(CultureInfo.InvariantCulture) + extension);
            counter++;
        }
        while (File.Exists(candidatePath));

        return candidatePath;
    }
}
