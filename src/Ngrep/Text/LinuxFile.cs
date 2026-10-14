using System.Runtime.InteropServices;

namespace Ngrep.Text;

/// <summary>
/// Linux file reads via <c>open</c> and <c>pread</c>. <c>File.OpenHandle</c> stats every
/// file; a short <c>pread</c> already means EOF, so the length is asked only when the
/// first buffer comes back full.
/// </summary>
internal static partial class LinuxFile
{
    private const int ORdonly = 0;
    private const int OCloexec = 524288;
    private const int SeekEnd = 2;
    private const int Eintr = 4;

    public static int OpenRead(string path)
    {
        while (true)
        {
            var fd = Open(path, ORdonly | OCloexec, 0);
            if (fd >= 0 || Marshal.GetLastPInvokeError() != Eintr)
            {
                return fd;
            }
        }
    }

    public static unsafe int ReadAt(int fd, byte[] buffer, int offset, int count, long position)
    {
        fixed (byte* pinned = buffer)
        {
            var pointer = pinned + offset;
            while (true)
            {
                var read = Pread(fd, pointer, (nuint)count, position);
                if (read >= 0)
                {
                    return (int)read;
                }

                if (Marshal.GetLastPInvokeError() != Eintr)
                {
                    return -1;
                }
            }
        }
    }

    public static long GetLength(int fd)
    {
        while (true)
        {
            var length = Lseek(fd, 0, SeekEnd);
            if (length >= 0 || Marshal.GetLastPInvokeError() != Eintr)
            {
                return length;
            }
        }
    }

    public static void Close(int fd)
    {
        if (fd >= 0)
        {
            _ = CloseFd(fd);
        }
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags, int mode);

    [LibraryImport("libc", EntryPoint = "pread", SetLastError = true)]
    private static unsafe partial nint Pread(int fd, byte* buffer, nuint count, long offset);

    [LibraryImport("libc", EntryPoint = "lseek", SetLastError = true)]
    private static partial long Lseek(int fd, long offset, int whence);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int CloseFd(int fd);
}
