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

    /// <summary>
    /// Reads file bytes for search. The <see cref="ValueTask{TResult}"/> is already complete:
    /// <see cref="RandomAccess.Read"/> runs on the calling worker, so a warm small file does not
    /// allocate an async state machine or hop to another pool thread. <paramref name="stop"/> is
    /// consulted after each chunk, once the leading bytes have passed the NUL probe. Returning
    /// true ends the read before EOF. A miss still reads the whole file. Files at or above
    /// <see cref="MmapThreshold"/> map once the length is known.
    /// </summary>
    public static ValueTask<PooledBytes> ReadAsync(string path, ReadStop? stop, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        SafeFileHandle? handle = null;
        try
        {
            handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                OpenOptions);
            return new ValueTask<PooledBytes>(ReadFrom(handle, stop, cancellation));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ValueTask<PooledBytes>(PooledBytes.Unreadable());
        }
        finally
        {
            handle?.Dispose();
        }
    }

    /// <summary>
    /// Unix <c>SequentialScan</c> is <c>posix_fadvise</c>, a syscall that does not help one
    /// small read. Windows stores the hint in the open call, so it stays on.
    /// </summary>
    private static readonly FileOptions OpenOptions =
        OperatingSystem.IsWindows() ? FileOptions.SequentialScan : FileOptions.None;

    /// <summary>
    /// Files smaller than the first buffer are one read and no length syscall.
    /// A full buffer means the file may be larger, so the length is checked then.
    /// </summary>
    private static PooledBytes ReadFrom(SafeFileHandle handle, ReadStop? stop, CancellationToken cancellation)
    {
        var first = 64 * 1024;
        byte[]? buffer = null;
        var owned = false;
        try
        {
            buffer = ArrayPool<byte>.Shared.Rent(first);
            var offset = 0;
            var checkedBinary = false;
            var knownLength = -1L;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (offset == buffer.Length)
                {
                    var lengthLong = knownLength >= 0 ? knownLength : RandomAccess.GetLength(handle);
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
                        && TryMap(handle, (int)lengthLong, stop, out var mapped))
                    {
                        return mapped;
                    }

                    var target = stop == null
                        ? (int)lengthLong
                        : (int)Math.Min(lengthLong, offset + (1L << 20));
                    var grown = ArrayPool<byte>.Shared.Rent(target);
                    buffer.AsSpan(0, offset).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
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

                var read = RandomAccess.Read(handle, buffer.AsSpan(offset, want), offset);
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
                    return PooledBytes.Ok(buffer, offset);
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
            return PooledBytes.Ok(buffer, offset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PooledBytes.Unreadable();
        }
        finally
        {
            if (!owned && buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

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
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private unsafe byte* _pointer;
    private int _held;

    private PooledBytes(ReadKind kind, byte[]? buffer, int length)
    {
        Kind = kind;
        _buffer = buffer;
        Length = length;
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

    public static PooledBytes Ok(byte[]? buffer, int length) => new(ReadKind.Ok, buffer, length);

    public static unsafe PooledBytes Mapped(MemoryMappedFile map, MemoryMappedViewAccessor view, byte* pointer, int length)
    {
        var pooled = new PooledBytes(ReadKind.Ok, null, length)
        {
            _map = map,
            _view = view,
            _pointer = pointer,
            _held = 1,
        };
        return pooled;
    }

    public static PooledBytes TooLarge() => new(ReadKind.TooLarge, null, 0);

    public static PooledBytes Binary() => new(ReadKind.Binary, null, 0);

    public static PooledBytes Unreadable() => new(ReadKind.Unreadable, null, 0);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer != null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
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
