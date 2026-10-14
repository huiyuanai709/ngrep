using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ngrep.Text;

internal static class TextFiles
{
    public const int BinaryProbeLength = 8192;
    public const long MaxFileBytes = 32L * 1024L * 1024L;

    /// <summary>At this size a warm file is cheaper to map than to copy into a pooled buffer.</summary>
    public const int MmapThreshold = 256 * 1024;

    public static bool IsBinary(ReadOnlySpan<byte> bytes)
    {
        var n = Math.Min(bytes.Length, BinaryProbeLength);
        return bytes[..n].Contains((byte)0);
    }

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        return Encoding.UTF8.GetString(bytes);
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static PooledBytes Read(string path) => Read(path, stop: null);

    /// <summary>
    /// <paramref name="stop"/> is consulted after each chunk, once the leading bytes
    /// have passed the NUL probe. Returning true ends the read before EOF. A miss
    /// still reads the whole file. The span passed to <paramref name="stop"/> is the
    /// prefix read so far; <c>endOfFile</c> is true on the last chunk.
    /// </summary>
    public static PooledBytes Read(string path, ReadStop? stop)
    {
        if (OperatingSystem.IsLinux())
        {
            var fd = LinuxFile.OpenRead(path);
            if (fd < 0)
            {
                return PooledBytes.Unreadable();
            }

            var source = new LinuxBytes(fd);
            try
            {
                return ReadFrom(stop, ref source);
            }
            finally
            {
                LinuxFile.Close(fd);
            }
        }

        SafeFileHandle? handle = null;
        try
        {
            handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.SequentialScan);
            var source = new HandleBytes(handle);
            return ReadFrom(stop, ref source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PooledBytes.Unreadable();
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private interface IFileBytes
    {
        int ReadAt(byte[] buffer, int offset, int count, long position);

        long GetLength();

        bool TryMap(int length, ReadStop? stop, out PooledBytes mapped);
    }

    private struct LinuxBytes : IFileBytes
    {
        private readonly int _fd;

        public LinuxBytes(int fd) => _fd = fd;

        public int ReadAt(byte[] buffer, int offset, int count, long position) =>
            LinuxFile.ReadAt(_fd, buffer, offset, count, position);

        public long GetLength() => LinuxFile.GetLength(_fd);

        public bool TryMap(int length, ReadStop? stop, out PooledBytes mapped)
        {
            using var handle = new SafeFileHandle((IntPtr)_fd, ownsHandle: false);
            return TextFiles.TryMap(handle, length, stop, out mapped);
        }
    }

    private struct HandleBytes : IFileBytes
    {
        private readonly SafeFileHandle _handle;

        public HandleBytes(SafeFileHandle handle) => _handle = handle;

        public int ReadAt(byte[] buffer, int offset, int count, long position) =>
            RandomAccess.Read(_handle, buffer.AsSpan(offset, count), position);

        public long GetLength() => RandomAccess.GetLength(_handle);

        public bool TryMap(int length, ReadStop? stop, out PooledBytes mapped) =>
            TextFiles.TryMap(_handle, length, stop, out mapped);
    }

    /// <summary>
    /// Files smaller than the first buffer are one read and no length syscall.
    /// A full buffer means the file may be larger, so the length is checked then.
    /// </summary>
    private static PooledBytes ReadFrom<TSource>(ReadStop? stop, ref TSource source)
        where TSource : IFileBytes
    {
        var first = 64 * 1024;
        byte[]? buffer = null;
        var scratch = false;
        var owned = false;
        try
        {
            buffer = TakeBuffer(first, out scratch);
            var offset = 0;
            var checkedBinary = false;
            var knownLength = -1L;
            while (true)
            {
                if (offset == buffer.Length)
                {
                    var lengthLong = knownLength >= 0 ? knownLength : source.GetLength();
                    if (lengthLong < 0)
                    {
                        return PooledBytes.Unreadable();
                    }

                    knownLength = lengthLong;
                    if (lengthLong > MaxFileBytes)
                    {
                        return PooledBytes.TooLarge();
                    }

                    if (lengthLong <= offset)
                    {
                        break;
                    }

                    if (lengthLong >= MmapThreshold
                        && lengthLong <= int.MaxValue
                        && source.TryMap((int)lengthLong, stop, out var mapped))
                    {
                        return mapped;
                    }

                    var target = stop == null
                        ? (int)lengthLong
                        : (int)Math.Min(lengthLong, offset + (1L << 20));
                    var grown = TakeBuffer(target, out var grownScratch);
                    buffer.AsSpan(0, offset).CopyTo(grown);
                    Release(ref buffer, scratch);
                    buffer = grown;
                    scratch = grownScratch;
                }

                var want = buffer.Length - offset;
                if (knownLength >= 0)
                {
                    var remain = (int)Math.Min(want, knownLength - offset);
                    want = remain;
                }

                if (want <= 0)
                {
                    break;
                }

                var read = source.ReadAt(buffer, offset, want, offset);
                if (read < 0)
                {
                    return PooledBytes.Unreadable();
                }

                if (read == 0)
                {
                    break;
                }

                offset += read;
                var endOfFile = read < buffer.Length - (offset - read) || (knownLength >= 0 && offset >= knownLength);
                if (offset < buffer.Length)
                {
                    endOfFile = true;
                }

                if (!checkedBinary && (offset >= BinaryProbeLength || endOfFile))
                {
                    checkedBinary = true;
                    if (IsBinary(buffer.AsSpan(0, offset)))
                    {
                        return PooledBytes.Binary();
                    }
                }

                if (stop != null && checkedBinary && stop(buffer.AsSpan(0, offset), endOfFile))
                {
                    owned = true;
                    return PooledBytes.Ok(buffer, offset, scratch);
                }

                if (endOfFile)
                {
                    break;
                }
            }

            if (!checkedBinary && IsBinary(buffer.AsSpan(0, Math.Min(offset, BinaryProbeLength))))
            {
                return PooledBytes.Binary();
            }

            owned = true;
            return PooledBytes.Ok(buffer, offset, scratch);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PooledBytes.Unreadable();
        }
        finally
        {
            if (!owned)
            {
                Release(ref buffer, scratch);
            }
        }
    }

    [ThreadStatic]
    private static byte[]? t_scratch;

    [ThreadStatic]
    private static bool t_held;

    private static byte[] TakeBuffer(int need, out bool scratch)
    {
        if (!t_held)
        {
            var buf = t_scratch;
            if (buf == null || buf.Length < need)
            {
                var size = 64 * 1024;
                while (size < need)
                {
                    size *= 2;
                }

                buf = GC.AllocateUninitializedArray<byte>(size);
                t_scratch = buf;
            }

            t_held = true;
            scratch = true;
            return buf;
        }

        scratch = false;
        return ArrayPool<byte>.Shared.Rent(need);
    }

    private static void Release(ref byte[]? buffer, bool scratch)
    {
        if (buffer == null)
        {
            return;
        }

        if (scratch)
        {
            t_held = false;
        }
        else
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        buffer = null;
    }

    internal static void ReleaseScratch() => t_held = false;

    private static bool TryMap(SafeFileHandle handle, int length, ReadStop? stop, out PooledBytes mapped)
    {
        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        var acquired = false;
        try
        {
            file = MemoryMappedFile.CreateFromFile(
                handle,
                mapName: null,
                capacity: length,
                access: MemoryMappedFileAccess.Read,
                inheritability: HandleInheritability.None,
                leaveOpen: true);
            view = file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
            unsafe
            {
                byte* pointer = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                acquired = true;
                var span = new ReadOnlySpan<byte>(pointer, length);
                if (IsBinary(span))
                {
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
                    acquired = false;
                    view.Dispose();
                    file.Dispose();
                    mapped = PooledBytes.Binary();
                    return true;
                }

                stop?.Invoke(span, true);
                mapped = PooledBytes.Mapped(file, view, pointer, length);
                file = null;
                view = null;
                acquired = false;
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (acquired && view != null)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            view?.Dispose();
            file?.Dispose();
            mapped = null!;
            return false;
        }
    }
}

internal delegate bool ReadStop(ReadOnlySpan<byte> contents, bool endOfFile);

internal enum ReadKind
{
    Ok,
    TooLarge,
    Binary,
    Unreadable,
}

internal sealed class PooledBytes : IDisposable
{
    private byte[]? _buffer;
    private readonly bool _scratch;
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private unsafe byte* _pointer;
    private int _held;

    private PooledBytes(ReadKind kind, byte[]? buffer, int length, bool scratch)
    {
        Kind = kind;
        _buffer = buffer;
        Length = length;
        _scratch = scratch;
    }

    public ReadKind Kind { get; }

    public int Length { get; }

    public ReadOnlySpan<byte> Span
    {
        get
        {
            if (_buffer != null)
            {
                return _buffer.AsSpan(0, Length);
            }

            unsafe
            {
                if (_pointer == null || _held == 0)
                {
                    return [];
                }

                return new ReadOnlySpan<byte>(_pointer, Length);
            }
        }
    }

    public static PooledBytes Ok(byte[]? buffer, int length, bool scratch = false) => new(ReadKind.Ok, buffer, length, scratch);

    public static unsafe PooledBytes Mapped(MemoryMappedFile map, MemoryMappedViewAccessor view, byte* pointer, int length)
    {
        var pooled = new PooledBytes(ReadKind.Ok, null, length, scratch: false)
        {
            _map = map,
            _view = view,
            _pointer = pointer,
            _held = 1,
        };
        return pooled;
    }

    public static PooledBytes TooLarge() => new(ReadKind.TooLarge, null, 0, false);

    public static PooledBytes Binary() => new(ReadKind.Binary, null, 0, false);

    public static PooledBytes Unreadable() => new(ReadKind.Unreadable, null, 0, false);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer != null)
        {
            if (_scratch)
            {
                TextFiles.ReleaseScratch();
            }
            else
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        if (Interlocked.Exchange(ref _held, 0) == 1 && _view != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            unsafe
            {
                _pointer = null;
            }
        }

        _view?.Dispose();
        _view = null;
        _map?.Dispose();
        _map = null;
    }
}

internal static class LineMap
{
    public static int[] Build(string text)
    {
        var starts = new List<int> { 0 };
        SimdNewline.Append(text, starts);
        return starts.ToArray();
    }

    /// <summary>
    /// Real lines in <paramref name="text"/>. A trailing newline does not create an extra empty line.
    /// An empty file has zero lines.
    /// </summary>
    public static int LineCount(string text, int[] lineStarts)
    {
        if (text.Length == 0 || lineStarts.Length == 0)
        {
            return 0;
        }

        if (text[^1] == '\n')
        {
            return lineStarts.Length - 1;
        }

        return lineStarts.Length;
    }

    public static (int Line, int Column) Map(int[] lineStarts, int index)
    {
        if (lineStarts.Length == 0)
        {
            return (1, index + 1);
        }

        var lo = 0;
        var hi = lineStarts.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lineStarts[mid] <= index)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        var lineIndex = Math.Max(0, hi);
        return (lineIndex + 1, index - lineStarts[lineIndex] + 1);
    }

    public static string LineText(string text, int[] lineStarts, int line)
    {
        if (line < 1 || line > lineStarts.Length)
        {
            return "";
        }

        var start = lineStarts[line - 1];
        var exclusive = line < lineStarts.Length ? lineStarts[line] - 1 : text.Length;
        if (exclusive > start && text[exclusive - 1] == '\r')
        {
            exclusive--;
        }

        if (exclusive < start)
        {
            exclusive = start;
        }

        return text[start..exclusive];
    }
}
