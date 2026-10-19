using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal static class NativeLinks
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfoByHandle
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint TagMountPoint = 0xA0000003;
    private const uint TagSymlink = 0xA000000C;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoByHandle info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindFirstFileNameW(string fileName, uint flags, ref uint length, StringBuilder linkName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool FindNextFileNameW(IntPtr handle, ref uint length, StringBuilder linkName);

    [DllImport("kernel32.dll")]
    private static extern bool FindClose(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr security);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] inBuffer, int inSize, byte[] outBuffer, int outSize, out int returned, IntPtr overlapped);

    public static int LinkCount(string path)
    {
        using (SafeFileHandle h = CreateFileW(path, 0, ShareAll, IntPtr.Zero, OpenExisting, FlagBackupSemantics, IntPtr.Zero))
        {
            if (h.IsInvalid) return -1;
            FileInfoByHandle info;
            if (!GetFileInformationByHandle(h, out info)) return -1;
            return (int)info.NumberOfLinks;
        }
    }

    public static List<string> Links(string path)
    {
        List<string> result = new List<string>();
        string root = Path.GetPathRoot(path).TrimEnd('\\');
        uint length = 1024;
        StringBuilder sb = new StringBuilder((int)length);
        IntPtr h = FindFirstFileNameW(path, 0, ref length, sb);
        if (h == new IntPtr(-1)) return result;
        try
        {
            do
            {
                result.Add(root + sb.ToString());
                length = 1024;
                sb = new StringBuilder((int)length);
            } while (FindNextFileNameW(h, ref length, sb));
        }
        finally
        {
            FindClose(h);
        }
        return result;
    }

    public static void CreateHardLink(string newPath, string existingPath)
    {
        if (!CreateHardLinkW(newPath, existingPath, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static string JunctionTarget(string path)
    {
        using (SafeFileHandle h = CreateFileW(path, GenericRead, ShareAll, IntPtr.Zero, OpenExisting, FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero))
        {
            if (h.IsInvalid) return null;
            byte[] buffer = new byte[16 * 1024];
            int returned;
            if (!DeviceIoControl(h, FsctlGetReparsePoint, null, 0, buffer, buffer.Length, out returned, IntPtr.Zero)) return null;
            uint tag = BitConverter.ToUInt32(buffer, 0);
            int pathBuffer;
            if (tag == TagMountPoint) pathBuffer = 16;
            else if (tag == TagSymlink) pathBuffer = 20;
            else return null;
            int substOffset = BitConverter.ToUInt16(buffer, 8);
            int substLength = BitConverter.ToUInt16(buffer, 10);
            int printOffset = BitConverter.ToUInt16(buffer, 12);
            int printLength = BitConverter.ToUInt16(buffer, 14);
            string print = Encoding.Unicode.GetString(buffer, pathBuffer + printOffset, printLength);
            if (print.Length > 0) return print;
            string subst = Encoding.Unicode.GetString(buffer, pathBuffer + substOffset, substLength);
            return subst.StartsWith(@"\??\") ? subst.Substring(4) : subst;
        }
    }

    public static void CreateJunction(string linkPath, string targetPath)
    {
        string target = Path.GetFullPath(targetPath).TrimEnd('\\');
        string subst = @"\??\" + target;
        byte[] substBytes = Encoding.Unicode.GetBytes(subst);
        byte[] printBytes = Encoding.Unicode.GetBytes(target);
        int pathBytes = substBytes.Length + 2 + printBytes.Length + 2;
        int dataLength = 8 + pathBytes;
        byte[] buffer = new byte[8 + dataLength];
        BitConverter.GetBytes(TagMountPoint).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)dataLength).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);
        BitConverter.GetBytes((ushort)substBytes.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substBytes.Length + 2)).CopyTo(buffer, 12);
        BitConverter.GetBytes((ushort)printBytes.Length).CopyTo(buffer, 14);
        substBytes.CopyTo(buffer, 16);
        printBytes.CopyTo(buffer, 16 + substBytes.Length + 2);

        Directory.CreateDirectory(linkPath);
        bool ok = false;
        try
        {
            using (SafeFileHandle h = CreateFileW(linkPath, GenericWrite, ShareAll, IntPtr.Zero, OpenExisting, FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero))
            {
                if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                int returned;
                if (!DeviceIoControl(h, FsctlSetReparsePoint, buffer, buffer.Length, null, 0, out returned, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            ok = true;
        }
        finally
        {
            if (!ok) { try { Directory.Delete(linkPath, false); } catch (Exception) { } }
        }
    }
}
