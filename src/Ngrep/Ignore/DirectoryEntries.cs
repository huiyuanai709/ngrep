using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace Ngrep.Ignore;

internal enum DirentKind : byte
{
    File,
    Directory,
    Symlink,
    Other,
}

internal interface IDirentSink
{
    void OnEntry(string name, DirentKind kind);
}

/// <summary>
/// Lists one directory. On Linux, <c>d_type</c> distinguishes files, directories, and
/// symlinks without an <c>lstat</c>. Other systems stat each name.
/// </summary>
internal static class DirectoryEntries
{
    public static bool TryList<TSink>(string directory, ref TSink sink)
        where TSink : IDirentSink
    {
        if (OperatingSystem.IsLinux())
        {
            return LinuxDirectory.TryList(directory, ref sink);
        }

        return ListManaged(directory, ref sink);
    }

    private static bool ListManaged<TSink>(string directory, ref TSink sink)
        where TSink : IDirentSink
    {
        try
        {
            foreach (var full in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(full);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                sink.OnEntry(Path.GetFileName(full), KindOf(attributes));
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static DirentKind KindOf(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return DirentKind.Symlink;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            return DirentKind.Directory;
        }

        return DirentKind.File;
    }
}
