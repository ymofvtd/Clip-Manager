using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VideoTrayApp;

internal sealed record PhysicalFileId(uint Volume, ulong Index, uint Links);
internal sealed record ArchiveFileStamp(PhysicalFileId File, long Size, long Created, long Written, long Changed);
internal static class PhysicalFile
{
    // Metadata from the same protected handle as the content read. Unsupported filesystems
    // fall back to full verification rather than treating length/write time as proof.
    internal static ArchiveFileStamp? ArchiveStamp(SafeFileHandle handle, long length)
    {
        if (!GetFileInformationByHandleEx(handle, 0, out var basic, (uint)Marshal.SizeOf<FileBasicInfo>())
            || basic.ChangeTime <= 0) return null;
        try
        {
            var id = Read(handle);
            return id.Index == 0 ? null : new(id, length, basic.CreationTime, basic.LastWriteTime, basic.ChangeTime);
        }
        catch (Win32Exception) { return null; }
    }
    internal static PhysicalFileId Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return Read(stream.SafeFileHandle);
    }
    internal static PhysicalFileId Read(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.NumberOfLinks);
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileBasicInfo info, uint size);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint Attributes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
}
