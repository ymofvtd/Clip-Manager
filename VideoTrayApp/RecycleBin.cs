using System.Runtime.InteropServices;

namespace VideoTrayApp;

// IFileOperation's RECYCLEONDELETE requires recycling rather than permanent deletion.
// https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags
internal static class RecycleBin
{
    internal static void Recycle(string path)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            object? operation = null;
            IShellItem? item = null;
            try
            {
                // Network paths do not offer a local Recycle Bin. Fail before requesting any deletion.
                string root = Path.GetPathRoot(Path.GetFullPath(path))!;
                if (new DriveInfo(root).DriveType != DriveType.Fixed)
                    throw new IOException("Recycle Bin unavailable on this drive; file left untouched.");
                operation = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
                var api = (IFileOperation)operation;
                api.SetOperationFlags(0x00080000 | 0x00100000 | 0x20000000 | 0x00000400 | 0x00000004 | 0x00000010);
                var iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
                api.DeleteItem(item, IntPtr.Zero);
                api.PerformOperations();
                api.GetAnyOperationsAborted(out bool aborted);
                if (aborted || File.Exists(path)) throw new IOException("Recycling failed or was aborted; file left untouched.");
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                if (item is not null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
                if (operation is not null && Marshal.IsComObject(operation)) Marshal.FinalReleaseComObject(operation);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error is not null) throw new IOException("Recycle Bin operation failed: " + error.Message, error);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr binding, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }
    // Method order is the native IFileOperation vtable. Unused methods retain their slots.
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(uint window);
        void ApplyPropertiesToItem([MarshalAs(UnmanagedType.Interface)] object item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.Interface)] object items);
        void RenameItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void RenameItems([MarshalAs(UnmanagedType.Interface)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void MoveItems([MarshalAs(UnmanagedType.Interface)] object items, [MarshalAs(UnmanagedType.Interface)] object folder);
        void CopyItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void CopyItems([MarshalAs(UnmanagedType.Interface)] object items, [MarshalAs(UnmanagedType.Interface)] object folder);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems([MarshalAs(UnmanagedType.Interface)] object items);
        void NewItem([MarshalAs(UnmanagedType.Interface)] object folder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
