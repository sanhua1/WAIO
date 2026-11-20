using System.Runtime.InteropServices;
using System.Text;

namespace Waio.Feature.Launcher.Index;

internal sealed partial class VolumeIndex
{
    private const ulong Magic = 0x3158444F49414157; // "WAIOIDX1"
    private const int FormatVersion = 1;

    /// <summary>
    /// 存盘:先压缩名称块(死字节超过四分之一时),再把各数组原样写出(写临时文件再改名,不会留下半个文件)。
    /// 文件头记下卷序列号、USN 日志 ID 和下一次该读的 USN:下次启动读回来后从这里继续读日志,不用重新扫描(同 Everything 的 Everything.db)。
    /// </summary>
    public void Save(string path)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_names.Dead > _names.Length / 4 || _originals.Dead > _originals.Length / 4)
            {
                Compact();
            }

            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(Serial);
                writer.Write(JournalId);
                writer.Write(NextUsn);
                writer.Write(ScannedAt.ToUnixTimeMilliseconds());
                writer.Write(Count);
                writer.Write(Entries);
                writer.Write(_names.Length);
                writer.Write(_originals.Length);
                writer.Write(_alt.Length);
                writer.Write(_altOf.Count);
                writer.Flush();
                Write(stream, _seq, Count);
                Write(stream, _parent, Count);
                Write(stream, _flags, Count);
                Write(stream, _depth, Count);
                Write(stream, _size, Count);
                Write(stream, _mtime, Count);
                Write(stream, _name, Count);
                Write(stream, _nameLength, Count);
                Write(stream, _original, Count);
                stream.Write(_names.Span);
                stream.Write(_originals.Span);
                stream.Write(_alt.Span);
                foreach (var (owner, start) in _altOf)
                {
                    writer.Write(owner);
                    writer.Write(start);
                }

                writer.Flush();
            }

            File.Move(temp, path, overwrite: true);
            Dirty = false;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>读盘;文件不对(版本、卷序列号不符、长度不够、偏移越界)返回 null,调用方重新扫描。</summary>
    public static VolumeIndex? Load(string path, string root, uint serial, string? home)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt64() != Magic || reader.ReadInt32() != FormatVersion || reader.ReadUInt32() != serial)
            {
                return null;
            }

            var journal = reader.ReadUInt64();
            var next = reader.ReadInt64();
            var scanned = DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64());
            var count = reader.ReadInt32();
            var entries = reader.ReadInt32();
            var namesLength = reader.ReadInt32();
            var originalsLength = reader.ReadInt32();
            var altLength = reader.ReadInt32();
            var altCount = reader.ReadInt32();
            if (count < 0 || namesLength < 0 || originalsLength < 0 || altLength < 0 || altCount < 0)
            {
                return null;
            }

            var index = new VolumeIndex(root, serial, count, home, forLoad: true)
            {
                JournalId = journal, NextUsn = next, ScannedAt = scanned, Count = count, Entries = entries,
            };
            Read(stream, index._seq, count);
            Read(stream, index._parent, count);
            Read(stream, index._flags, count);
            Read(stream, index._depth, count);
            Read(stream, index._size, count);
            Read(stream, index._mtime, count);
            Read(stream, index._name, count);
            Read(stream, index._nameLength, count);
            Read(stream, index._original, count);
            index._names = new NameBlob(ReadBytes(stream, namesLength), namesLength);
            index._originals = new NameBlob(ReadBytes(stream, originalsLength), originalsLength);
            index._alt = new NameBlob(ReadBytes(stream, altLength), altLength);
            for (var i = 0; i < altCount; i++)
            {
                index._altOf[reader.ReadInt32()] = reader.ReadInt32();
            }

            return index.Validate() ? index : null;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>读回来的数据自洽:名称位置都在块内、父目录在范围内。</summary>
    private bool Validate()
    {
        for (var r = 0; r < Count; r++)
        {
            if ((_flags[r] & InUse) == 0)
            {
                continue;
            }

            if (_parent[r] < 0 || _parent[r] >= Math.Max(Count, RootRecord + 1)
                || (_name[r] >= 0 && (long)_name[r] + _nameLength[r] + 5 > _names.Length)
                || (_original[r] >= 0 && _original[r] >= _originals.Length))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>建库完成后调用:名称块收紧到实际大小(扩容时多留了一半),旧的大数组交给 GC。</summary>
    public void TrimExcess()
    {
        _lock.EnterWriteLock();
        try
        {
            _names = _names.Trimmed();
            _originals = _originals.Trimmed();
            _alt = _alt.Trimmed();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>压缩名称块:按记录号顺序把还在用的名称重新排一遍(调用方持有写锁)。</summary>
    internal void Compact()
    {
        var names = new NameBlob(_names.Length - (int)Math.Min(_names.Length, _names.Dead) + 1024);
        var originals = new NameBlob(_originals.Length - (int)Math.Min(_originals.Length, _originals.Dead) + 1024);
        for (var r = 0; r < Count; r++)
        {
            if (_name[r] >= 0)
            {
                _name[r] = names.Append(_names.At(_name[r], _nameLength[r]), r);
            }

            if (_original[r] >= 0 && _originals.OwnerAt(_original[r], out var end) == r)
            {
                _original[r] = originals.Append(_originals.At(_original[r], end - _original[r]), r);
            }
            else
            {
                _original[r] = -1;
            }
        }

        _names = names;
        _originals = originals;
    }

    private static void Write<T>(Stream stream, T[] array, int count)
        where T : unmanaged => stream.Write(MemoryMarshal.AsBytes(array.AsSpan(0, count)));

    private static void Read<T>(Stream stream, T[] array, int count)
        where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(array.AsSpan(0, count));
        stream.ReadExactly(bytes);
    }

    private static byte[] ReadBytes(Stream stream, int length)
    {
        var data = GC.AllocateUninitializedArray<byte>(length + 16);
        stream.ReadExactly(data.AsSpan(0, length));
        return data;
    }
}
