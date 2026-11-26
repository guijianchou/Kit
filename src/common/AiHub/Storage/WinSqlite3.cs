namespace Kit.AiHub.Storage;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Minimal, AOT-safe P/Invoke wrapper over Windows' built-in winsqlite3.dll.
/// Keeps zero third-party SQLite binaries or NuGet dependencies in the staged package.
/// </summary>
internal static partial class WinSqlite3
{
    internal const int SqliteOk = 0;
    internal const int SqliteBusy = 5;
    internal const int SqliteLocked = 6;
    internal const int SqliteCorrupt = 11;
    internal const int SqliteNotADb = 26;
    internal const int SqliteRow = 100;
    internal const int SqliteDone = 101;

    internal const int SqliteOpenReadOnly = 0x00000001;
    internal const int SqliteOpenReadWrite = 0x00000002;
    internal const int SqliteOpenCreate = 0x00000004;
    internal const int SqliteOpenFullMutex = 0x00010000;

    internal const int SqliteNull = 5;

    private static readonly IntPtr SqliteTransient = new(-1);
    private static readonly byte[] EmptyUtf8 = [0];

    internal static bool IsCorruptionError(int rc)
    {
        int primary = rc & 0xFF;
        return primary is SqliteCorrupt or SqliteNotADb;
    }

    internal static SqliteConnection Open(string path, bool readOnly)
    {
        byte[] pathBytes = ToNullTerminatedUtf8(path);
        int flags = readOnly
            ? SqliteOpenReadOnly | SqliteOpenFullMutex
            : SqliteOpenReadWrite | SqliteOpenCreate | SqliteOpenFullMutex;
        int rc = sqlite3_open_v2(pathBytes, out IntPtr handle, flags, IntPtr.Zero);
        if (rc != SqliteOk)
        {
            string message = handle != IntPtr.Zero ? ReadErrorMessage(handle) : $"SQLite open failed ({rc}).";
            if (handle != IntPtr.Zero)
            {
                _ = sqlite3_close_v2(handle);
            }

            throw new SqliteNativeException(rc, message);
        }

        var connection = new SqliteConnection(handle);
        try
        {
            connection.SetBusyTimeout(3000);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal sealed partial class SqliteConnection : IDisposable
    {
        private IntPtr _handle;

        internal SqliteConnection(IntPtr handle)
        {
            _handle = handle;
        }

        internal void SetBusyTimeout(int milliseconds)
        {
            ThrowIfDisposed();
            int rc = sqlite3_busy_timeout(_handle, Math.Max(0, milliseconds));
            CheckResult(rc);
        }

        internal void ExecuteNonQuery(string sql)
        {
            ThrowIfDisposed();
            byte[] sqlBytes = ToNullTerminatedUtf8(sql);
            int rc = sqlite3_exec(_handle, sqlBytes, IntPtr.Zero, IntPtr.Zero, out IntPtr errMsg);
            if (rc != SqliteOk)
            {
                string message = errMsg != IntPtr.Zero ? (Marshal.PtrToStringUTF8(errMsg) ?? $"SQLite error {rc}") : ReadErrorMessage(_handle);
                if (errMsg != IntPtr.Zero)
                {
                    sqlite3_free(errMsg);
                }

                throw new SqliteNativeException(rc, message);
            }

            if (errMsg != IntPtr.Zero)
            {
                sqlite3_free(errMsg);
            }
        }

        internal SqliteStatement Prepare(string sql)
        {
            ThrowIfDisposed();
            byte[] sqlBytes = Encoding.UTF8.GetBytes(sql);
            int rc = sqlite3_prepare_v2(_handle, sqlBytes, sqlBytes.Length, out IntPtr stmt, out _);
            if (rc != SqliteOk)
            {
                throw new SqliteNativeException(rc, ReadErrorMessage(_handle));
            }

            return new SqliteStatement(_handle, stmt);
        }

        private void CheckResult(int rc)
        {
            if (rc != SqliteOk)
            {
                throw new SqliteNativeException(rc, ReadErrorMessage(_handle));
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                _ = sqlite3_close_v2(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    internal sealed partial class SqliteStatement : IDisposable
    {
        private readonly IntPtr _db;
        private IntPtr _stmt;

        internal SqliteStatement(IntPtr db, IntPtr stmt)
        {
            _db = db;
            _stmt = stmt;
        }

        internal void BindInt64(int index, long value)
        {
            ThrowIfDisposed();
            int rc = sqlite3_bind_int64(_stmt, index, value);
            if (rc != SqliteOk)
            {
                throw new SqliteNativeException(rc, ReadErrorMessage(_db));
            }
        }

        internal void BindNullableInt64(int index, long? value)
        {
            ThrowIfDisposed();
            int rc = value.HasValue
                ? sqlite3_bind_int64(_stmt, index, value.Value)
                : sqlite3_bind_null(_stmt, index);
            if (rc != SqliteOk)
            {
                throw new SqliteNativeException(rc, ReadErrorMessage(_db));
            }
        }

        internal void BindNullableInt32(int index, int? value) =>
            BindNullableInt64(index, value.HasValue ? value.Value : null);

        internal void BindText(int index, string? value)
        {
            ThrowIfDisposed();
            int rc;
            if (value is null)
            {
                rc = sqlite3_bind_null(_stmt, index);
            }
            else if (value.Length == 0)
            {
                rc = sqlite3_bind_text(_stmt, index, EmptyUtf8, 0, SqliteTransient);
            }
            else
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(value);
                rc = sqlite3_bind_text(_stmt, index, utf8, utf8.Length, SqliteTransient);
            }

            if (rc != SqliteOk)
            {
                throw new SqliteNativeException(rc, ReadErrorMessage(_db));
            }
        }

        internal bool Step()
        {
            ThrowIfDisposed();
            int rc = sqlite3_step(_stmt);
            if (rc == SqliteRow)
            {
                return true;
            }

            if (rc == SqliteDone)
            {
                return false;
            }

            throw new SqliteNativeException(rc, ReadErrorMessage(_db));
        }

        internal void ExecuteNonQuery()
        {
            while (Step())
            {
            }
        }

        internal void Reset()
        {
            ThrowIfDisposed();
            _ = sqlite3_reset(_stmt);
            _ = sqlite3_clear_bindings(_stmt);
        }

        internal bool IsNull(int column)
        {
            ThrowIfDisposed();
            return sqlite3_column_type(_stmt, column) == SqliteNull;
        }

        internal long GetInt64(int column)
        {
            ThrowIfDisposed();
            return sqlite3_column_int64(_stmt, column);
        }

        internal long? GetNullableInt64(int column) => IsNull(column) ? null : GetInt64(column);

        internal int GetInt32(int column) => checked((int)GetInt64(column));

        internal int? GetNullableInt32(int column) => IsNull(column) ? null : GetInt32(column);

        internal string GetString(int column)
        {
            ThrowIfDisposed();
            if (sqlite3_column_type(_stmt, column) == SqliteNull)
            {
                return string.Empty;
            }

            IntPtr ptr = sqlite3_column_text(_stmt, column);
            return ptr == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUTF8(ptr) ?? string.Empty);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_stmt == IntPtr.Zero, this);
        }

        public void Dispose()
        {
            if (_stmt != IntPtr.Zero)
            {
                _ = sqlite3_finalize(_stmt);
                _stmt = IntPtr.Zero;
            }
        }
    }

    internal sealed class SqliteNativeException : IOException
    {
        internal int ErrorCode { get; }

        internal SqliteNativeException(int errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }
    }

    private static byte[] ToNullTerminatedUtf8(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        byte[] buffer = new byte[byteCount + 1];
        Encoding.UTF8.GetBytes(value, 0, value.Length, buffer, 0);
        buffer[byteCount] = 0;
        return buffer;
    }

    private static string ReadErrorMessage(IntPtr db)
    {
        IntPtr ptr = sqlite3_errmsg(db);
        return ptr == IntPtr.Zero ? "SQLite native error." : (Marshal.PtrToStringUTF8(ptr) ?? "SQLite native error.");
    }

#pragma warning disable SYSLIB1054 // AllowUnsafeBlocks=false requires DllImport instead of LibraryImport.
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr ppDb, int flags, IntPtr zVfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_close_v2(IntPtr db);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr errMsg);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void sqlite3_free(IntPtr ptr);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] zSql, int nByte, out IntPtr ppStmt, out IntPtr pzTail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_reset(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_clear_bindings(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_bind_null(IntPtr stmt, int index);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int nByte, IntPtr destructor);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_column_type(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern long sqlite3_column_int64(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr sqlite3_errmsg(IntPtr db);
#pragma warning restore SYSLIB1054
}
