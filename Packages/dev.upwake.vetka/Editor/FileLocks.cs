using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Upwake.Vetka
{
    internal static class FileLocks
    {
        private const uint GenericWrite = 0x40000000;
        private const uint DeleteAccess = 0x00010000;
        private const uint ShareAll = 0x00000007;
        private const uint OpenExisting = 3;
        private const int SharingViolation = 32;
        private const int LockViolation = 33;

        public static List<string> InUse(string root, IEnumerable<string> paths, int limit)
        {
            if (!GitExecutable.IsWindows)
            {
                return new List<string>();
            }

            return paths.Select(path => (path, full: Path.Combine(root, path)))
                .Where(file => File.Exists(file.full))
                .Take(limit)
                .Where(file => Denied(file.full, GenericWrite) || Denied(file.full, DeleteAccess))
                .Select(file => file.path)
                .ToList();
        }

        private static bool Denied(string path, uint access)
        {
            try
            {
                using var handle = CreateFileW(path, access, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (!handle.IsInvalid)
                {
                    return false;
                }

                var error = Marshal.GetLastWin32Error();
                return error == SharingViolation || error == LockViolation;
            }
            catch (Exception)
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);
    }
}
