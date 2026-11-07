using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ManagedCommon;

// Compile with the production Logger.cs. Only the platform path provider is replaced,
// so the failure/retry test cannot write to or prune a user's real log directories.
namespace Kit.Interop
{
    internal static class Constants
    {
        public static string Root;

        public static string AppDataPath() => Root;
    }
}

internal static class ManagedLoggerSmoke
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string[] Files(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();

    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void CloseListeners()
    {
        foreach (TraceListener listener in Trace.Listeners)
        {
            listener.Flush();
            listener.Close();
        }

        Trace.Listeners.Clear();
    }

    private static int Main(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(args[0]);
            Kit.Interop.Constants.Root = Path.Combine(root, "Local", "Kit");
            Directory.CreateDirectory(Kit.Interop.Constants.Root);
            Environment.SetEnvironmentVariable("USERPROFILE", Path.Combine(root, "Profile"));
            Environment.SetEnvironmentVariable("TEMP", Path.Combine(root, "Temp"));
            Environment.SetEnvironmentVariable("TMP", Path.Combine(root, "Temp"));
            Directory.CreateDirectory(Path.Combine(root, "Temp"));

            Logger.InitializeLogger(@"\Writable\Logs");
            Logger.LogInfo("managed normal write");
            Trace.Flush();
            Require(Logger.CurrentLogFile.StartsWith(Kit.Interop.Constants.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Normal logs escaped the configured root.");
            Require(ReadLog(Logger.CurrentLogFile).Contains("managed normal write", StringComparison.Ordinal), "The normal log message was not written.");
            CloseListeners();
            Console.WriteLine("PASS managed: configured path accepts log messages.");

            string blocked = Path.Combine(Kit.Interop.Constants.Root, "Blocked");
            File.WriteAllText(blocked, "This file deliberately prevents directory creation.");
            string[] before = Files(root);
            using var diagnostics = new StringWriter();
            Trace.Listeners.Add(new TextWriterTraceListener(diagnostics));
            Logger.InitializeLogger(@"\Blocked\Logs");
            Logger.LogInfo("This message must not create a fallback file.");
            Logger.InitializeLogger(@"\Blocked\Logs");
            Trace.Flush();
            Require(!Logger.IsLoggingEnabled, "File logging stayed enabled after a failed initialization.");
            Require(diagnostics.ToString().Contains("file logging is disabled", StringComparison.Ordinal), "Initialization failure did not leave a diagnostic.");
            Require(before.SequenceEqual(Files(root), StringComparer.OrdinalIgnoreCase), "Failed initialization scattered logs into another directory.");
            Console.WriteLine("PASS managed: repeated failure is nonfatal, diagnostic is retained, no files scatter.");
            CloseListeners();

            File.Delete(blocked);
            Logger.InitializeLogger(@"\Blocked\Logs");
            Logger.LogInfo("managed recovered write");
            Trace.Flush();
            Require(Logger.IsLoggingEnabled, "File logging did not recover on a later initialization.");
            Require(ReadLog(Logger.CurrentLogFile).Contains("managed recovered write", StringComparison.Ordinal), "Recovered logging did not write its message.");
            Require(!Directory.Exists(Path.Combine(root, "Profile", "AppData", "LocalLow")), "A LocalLow fallback directory was created.");
            Require(Directory.GetFileSystemEntries(Path.Combine(root, "Temp")).Length == 0, "A TEMP fallback path was created.");
            Console.WriteLine("PASS managed: the configured path recovers when it becomes writable.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            CloseListeners();
        }
    }
}
