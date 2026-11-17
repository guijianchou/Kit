using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;

namespace Kit.AIHubLib.Services;

public sealed partial class RecycleBinHelper
{
    private static readonly Guid FileOperationClassId = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid FileOperationInterfaceId = new("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8");
    private static readonly Guid ShellItemInterfaceId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    public (bool Success, string? Error) MoveToRecycleBin(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (false, "Path cannot be null or empty");
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return (false, "Path does not exist");
        }

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return TryMoveCore(path);
        }

        // IFileOperation is an apartment-threaded shell component. Background callers
        // normally use MTA threads, so give this synchronous operation its own STA.
        (bool Success, string? Error) result = (false, "The recycle operation did not run.");
        var thread = new Thread(() => result = TryMoveCore(path))
        {
            IsBackground = true,
            Name = "Kit recycle operation",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static (bool Success, string? Error) TryMoveCore(string path)
    {
        try
        {
            return MoveCore(Path.GetFullPath(path));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static unsafe (bool Success, string? Error) MoveCore(string path)
    {
        int initialized = NativeMethods.CoInitializeEx(IntPtr.Zero, 2); // COINIT_APARTMENTTHREADED
        if (initialized < 0)
        {
            Marshal.ThrowExceptionForHR(initialized);
        }

        IntPtr operationPointer = IntPtr.Zero;
        IntPtr itemPointer = IntPtr.Zero;
        object? operationWrapper = null;
        try
        {
            Marshal.ThrowExceptionForHR(NativeMethods.CoCreateInstance(in FileOperationClassId, IntPtr.Zero, 1,
                in FileOperationInterfaceId, out operationPointer));
            operationWrapper = UniqueComInterfaceMarshaller<IFileOperation>.ConvertToManaged((void*)operationPointer)
                ?? throw new InvalidOperationException("The shell file operation is unavailable.");
            var operation = (IFileOperation)operationWrapper;
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(
                FileOperationFlags.AllowUndo | FileOperationFlags.RecycleOnDelete | FileOperationFlags.EarlyFailure |
                FileOperationFlags.NoConfirmation | FileOperationFlags.Silent | FileOperationFlags.NoErrorUI));
            Marshal.ThrowExceptionForHR(NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero,
                in ShellItemInterfaceId, out itemPointer));
            Marshal.ThrowExceptionForHR(operation.DeleteItem(itemPointer, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(operation.PerformOperations());
            Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out int aborted));
            return aborted == 0 ? (true, null) : (false, "The recycle operation was aborted.");
        }
        finally
        {
            if (itemPointer != IntPtr.Zero)
            {
                Marshal.Release(itemPointer);
            }

            (operationWrapper as ComObject)?.FinalRelease();
            if (operationPointer != IntPtr.Zero)
            {
                Marshal.Release(operationPointer);
            }

            if (initialized >= 0)
            {
                NativeMethods.CoUninitialize();
            }
        }
    }

    // The complete method order and IID follow IFileOperation in ShObjIdl_core.h.
    // Unused interface arguments remain raw pointers; generated interop owns only the operation wrapper.
    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper, StringMarshalling = StringMarshalling.Utf16)]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    internal partial interface IFileOperation
    {
        [PreserveSig]
        int Advise(IntPtr sink, out uint cookie);
        [PreserveSig]
        int Unadvise(uint cookie);
        [PreserveSig]
        int SetOperationFlags(FileOperationFlags flags);
        [PreserveSig]
        int SetProgressMessage(string message);
        [PreserveSig]
        int SetProgressDialog(IntPtr dialog);
        [PreserveSig]
        int SetProperties(IntPtr properties);
        [PreserveSig]
        int SetOwnerWindow(IntPtr owner);
        [PreserveSig]
        int ApplyPropertiesToItem(IntPtr item);
        [PreserveSig]
        int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig]
        int RenameItem(IntPtr item, string name, IntPtr sink);
        [PreserveSig]
        int RenameItems(IntPtr items, string name);
        [PreserveSig]
        int MoveItem(IntPtr item, IntPtr destination, string name, IntPtr sink);
        [PreserveSig]
        int MoveItems(IntPtr items, IntPtr destination);
        [PreserveSig]
        int CopyItem(IntPtr item, IntPtr destination, string name, IntPtr sink);
        [PreserveSig]
        int CopyItems(IntPtr items, IntPtr destination);
        [PreserveSig]
        int DeleteItem(IntPtr item, IntPtr sink);
        [PreserveSig]
        int DeleteItems(IntPtr items);
        [PreserveSig]
        int NewItem(IntPtr destination, uint attributes, string name, string template, IntPtr sink);
        [PreserveSig]
        int PerformOperations();
        [PreserveSig]
        int GetAnyOperationsAborted(out int aborted);
    }

    [Flags]
    internal enum FileOperationFlags : uint
    {
        Silent = 0x0004,
        NoConfirmation = 0x0010,
        AllowUndo = 0x0040,
        NoErrorUI = 0x0400,
        EarlyFailure = 0x00100000,
        RecycleOnDelete = 0x00080000,
    }

    private static partial class NativeMethods
    {
        [LibraryImport("ole32.dll")]
        internal static partial int CoInitializeEx(IntPtr reserved, uint flags);

        [LibraryImport("ole32.dll")]
        internal static partial void CoUninitialize();

        [LibraryImport("ole32.dll")]
        internal static partial int CoCreateInstance(in Guid classId, IntPtr outer, uint context, in Guid interfaceId, out IntPtr instance);

        [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int SHCreateItemFromParsingName(string path, IntPtr bindContext, in Guid interfaceId, out IntPtr item);
    }
}
