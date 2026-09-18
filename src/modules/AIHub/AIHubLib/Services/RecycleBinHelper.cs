using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Kit.AIHubLib.Services;

public sealed class RecycleBinHelper
{
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

        try
        {
            IFileOperation? fileOperation = null;
            IShellItem? shellItem = null;

            try
            {
                fileOperation = (IFileOperation)new FileOperation();
                fileOperation.SetOperationFlags(
                    FileOperationFlags.FOF_ALLOWUNDO |
                    FileOperationFlags.FOF_NOCONFIRMATION |
                    FileOperationFlags.FOF_SILENT |
                    FileOperationFlags.FOF_NOERRORUI);

                int hr = NativeMethods.SHCreateItemFromParsingName(
                    path,
                    IntPtr.Zero,
                    typeof(IShellItem).GUID,
                    out shellItem);

                if (hr != 0 || shellItem == null)
                {
                    return (false, $"Failed to create shell item: HRESULT=0x{hr:X8}");
                }

                fileOperation.DeleteItem(shellItem, IntPtr.Zero);
                hr = fileOperation.PerformOperations();

                if (hr != 0)
                {
                    return (false, $"Failed to perform operations: HRESULT=0x{hr:X8}");
                }

                return (true, null);
            }
            finally
            {
                if (shellItem != null)
                {
                    Marshal.ReleaseComObject(shellItem);
                }
                if (fileOperation != null)
                {
                    Marshal.ReleaseComObject(fileOperation);
                }
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    [ComImport]
    [Guid("3AD05575-8857-4850-9277-11B85BDB8E09")]
    [ClassInterface(ClassInterfaceType.None)]
    private class FileOperation { }

    [ComImport]
    [Guid("947a214a-80e4-451b-a642-b491d8e20f50")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        uint Advise(IntPtr pfops, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOperationFlags(FileOperationFlags dwOperationFlags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
        void SetProgressDialog(IntPtr popd);
        void SetOwnerWindow(uint hwndOwner);
        void ApplyPropertiesToItem(IShellItem psi);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);
        void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);
        void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszCopyName, IntPtr pfopsItem);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);
        void DeleteItem(IShellItem psiItem, IntPtr pfopsItem);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);
        void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName, IntPtr pfopsItem);
        [PreserveSig]
        int PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)]
        bool GetAnyOperationsAborted();
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IBindCtx pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [Flags]
    private enum FileOperationFlags : uint
    {
        FOF_SILENT = 0x0004,
        FOF_NOCONFIRMATION = 0x0010,
        FOF_ALLOWUNDO = 0x0040,
        FOF_NOERRORUI = 0x0400
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            out IShellItem? ppv);
    }
}
