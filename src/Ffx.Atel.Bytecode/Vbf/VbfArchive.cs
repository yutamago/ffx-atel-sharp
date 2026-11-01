// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ffx.Atel.Bytecode.Vbf;

/// <summary>One file stored in a VBF archive.</summary>
/// <param name="PathDigest">MD5 of <paramref name="Path"/>; the key the game looks files up by.</param>
/// <param name="Path">Path inside the archive, such as <c>ffx_ps2/ffx/master/jppc/event/...</c>.</param>
/// <param name="FirstBlock">Index of the file's first block in the block-size table.</param>
/// <param name="Size">Size once decompressed.</param>
/// <param name="Offset">Absolute file offset of the first block (junk for empty files).</param>
public sealed record VbfEntry(byte[] PathDigest, string Path, uint FirstBlock, ulong Size, ulong Offset) {
    /// <summary>Number of 64 KiB blocks the file is split into.</summary>
    public ulong BlockCount => (Size + VbfArchive.BlockSize - 1) / VbfArchive.BlockSize;
}

/// <summary>
///     Reader for the VBF ("SRYK") archives of the FFX/X-2 HD Remaster (<c>data/FFX_Data.vbf</c>).
/// </summary>
/// <remarks>
///     Ported from ffx-rust's <c>ffx-vbf</c> crate; the layout is described in its
///     <c>docs/formats/vbf.md</c> (recovered from <c>BigFileHandle_initialize</c>, <c>0x0061dac0</c>).
///     A header lists every file's path digest, size, first block and offset; files are cut into
///     64 KiB blocks, each stored raw or as a zlib stream. The archive ends with the MD5 of the header.
///     Reads are positioned, so one archive can serve several threads.
/// </remarks>
public sealed class VbfArchive : IDisposable {
    /// <summary><c>"SRYK"</c> as a little-endian <c>u32</c>.</summary>
    public const uint Magic = 0x4b59_5253;

    /// <summary>Files are cut into blocks of this many bytes before compression.</summary>
    public const int BlockSize = 0x1_0000;

    private const int DigestsOffset = 0x10;
    private const int DigestLength  = 16;
    private const int RecordLength  = 32;

    private readonly SafeFileHandle                  _file;
    private readonly ushort[]                        _blockSizes;
    private readonly Dictionary<string, VbfEntry>    _byDigest;

    /// <summary>All entries, in the order the header stores them (the order of the file data).</summary>
    public IReadOnlyList<VbfEntry> Entries { get; }

    private VbfArchive(SafeFileHandle file, List<VbfEntry> entries, ushort[] blockSizes) {
        _file       = file;
        Entries     = entries;
        _blockSizes = blockSizes;
        _byDigest   = entries.ToDictionary(e => Convert.ToHexString(e.PathDigest));
    }

    /// <summary>Opens an archive and checks the MD5 of its header.</summary>
    /// <exception cref="InvalidDataException">Not a VBF archive, or a corrupt header.</exception>
    public static VbfArchive Open(string path) {
        SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try {
            long fileLength = RandomAccess.GetLength(file);
            Span<byte> prefix = stackalloc byte[8];
            ReadExact(file, prefix, 0);
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
            if (magic != Magic) {
                throw new InvalidDataException($"not a VBF archive (magic 0x{magic:x8}, expected \"SRYK\")");
            }
            long headerLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix[4..]);
            if (headerLength > fileLength - 16) {
                throw new InvalidDataException("VBF header length exceeds the file size");
            }
            byte[] header = new byte[headerLength];
            ReadExact(file, header, 0);
            byte[] stored = new byte[16];
            ReadExact(file, stored, fileLength - 16);
            if (!MD5.HashData(header).AsSpan().SequenceEqual(stored)) {
                throw new InvalidDataException("VBF header digest mismatch");
            }
            (List<VbfEntry> entries, ushort[] blockSizes) = ParseHeader(header);
            return new VbfArchive(file, entries, blockSizes);
        }
        catch {
            file.Dispose();
            throw;
        }
    }

    private static (List<VbfEntry>, ushort[]) ParseHeader(byte[] header) {
        ReadOnlySpan<byte> h = header;
        int count = checked((int)U32(h, 0x08));
        int recordsOffset = checked(DigestsOffset + count * DigestLength);
        int pathTableLengthOffset = checked(recordsOffset + count * RecordLength);
        int pathTableLength = checked((int)U32(h, pathTableLengthOffset));
        int pathsOffset = pathTableLengthOffset + 4;
        int blockTableOffset = checked(pathTableLengthOffset + pathTableLength);
        if (pathTableLength < 4 || blockTableOffset > header.Length) {
            throw new InvalidDataException("VBF path table outside the header");
        }
        ReadOnlySpan<byte> paths = h[pathsOffset..blockTableOffset];

        List<VbfEntry> entries = new(count);
        ulong blockCount = 0;
        for (int i = 0; i < count; i++) {
            int rec = recordsOffset + i * RecordLength;
            int pathOffset = checked((int)U64(h, rec + 0x18));
            VbfEntry entry = new(
                PathDigest: h.Slice(DigestsOffset + i * DigestLength, DigestLength).ToArray(),
                Path:       CString(paths, pathOffset),
                FirstBlock: U32(h, rec),
                Size:       U64(h, rec + 0x08),
                Offset:     U64(h, rec + 0x10));
            blockCount = Math.Max(blockCount, entry.FirstBlock + entry.BlockCount);
            entries.Add(entry);
        }
        int blocks = checked((int)blockCount);
        if (blockTableOffset + blocks * 2 > header.Length) {
            throw new InvalidDataException("VBF block table extends past the header");
        }
        ushort[] blockSizes = new ushort[blocks];
        for (int i = 0; i < blocks; i++) {
            blockSizes[i] = BinaryPrimitives.ReadUInt16LittleEndian(h[(blockTableOffset + 2 * i)..]);
        }
        return (entries, blockSizes);
    }

    /// <summary>
    ///     Normalizes a path the way <c>File_Probe</c> (<c>0x00607e00</c>) does before hashing:
    ///     <c>\</c> becomes <c>/</c> and ASCII <c>A</c>–<c>Z</c> become lowercase.
    /// </summary>
    public static string NormalizePath(string path) {
        StringBuilder sb = new(path.Length);
        foreach (char c in path) {
            sb.Append(c == '\\' ? '/' : c is >= 'A' and <= 'Z' ? (char)(c + 32) : c);
        }
        return sb.ToString();
    }

    /// <summary>Looks up a file by its archive path, normalized as the game does.</summary>
    public VbfEntry? Find(string path) {
        byte[] digest = MD5.HashData(Encoding.UTF8.GetBytes(NormalizePath(path)));
        return _byDigest.GetValueOrDefault(Convert.ToHexString(digest));
    }

    /// <summary>Reads and decompresses a whole file.</summary>
    public byte[] Read(VbfEntry entry) {
        byte[] output = new byte[checked((int)entry.Size)];
        byte[] buffer = new byte[BlockSize];
        long offset = checked((long)entry.Offset);
        int written = 0;
        for (ulong i = 0; i < entry.BlockCount; i++) {
            int index = checked((int)(entry.FirstBlock + i));
            int size = (int)Math.Min(entry.Size - i * BlockSize, BlockSize);
            if (index >= _blockSizes.Length) {
                throw new InvalidDataException($"{entry.Path}: block index outside the block table");
            }
            int stored = _blockSizes[index] == 0 ? BlockSize : _blockSizes[index];
            ReadExact(_file, buffer.AsSpan(0, stored), offset);
            offset += stored;
            if (stored == size) {
                buffer.AsSpan(0, stored).CopyTo(output.AsSpan(written));
            }
            else {
                using MemoryStream source = new(buffer, 0, stored, writable: false);
                using ZLibStream zlib = new(source, CompressionMode.Decompress);
                int got = 0;
                while (got < size) {
                    int n = zlib.Read(output, written + got, size - got);
                    if (n == 0) break;
                    got += n;
                }
                if (got != size || zlib.ReadByte() != -1) {
                    throw new InvalidDataException($"{entry.Path}: block {index} decompressed to the wrong size");
                }
            }
            written += size;
        }
        return output;
    }

    /// <summary>Reads a file by its archive path.</summary>
    /// <exception cref="FileNotFoundException">The archive has no such file.</exception>
    public byte[] Read(string path) =>
        Read(Find(path) ?? throw new FileNotFoundException($"{path} is not in the archive"));

    public void Dispose() => _file.Dispose();

    private static void ReadExact(SafeFileHandle file, Span<byte> buffer, long offset) {
        while (!buffer.IsEmpty) {
            int n = RandomAccess.Read(file, buffer, offset);
            if (n == 0) throw new EndOfStreamException();
            buffer = buffer[n..];
            offset += n;
        }
    }

    private static uint U32(ReadOnlySpan<byte> b, int o) {
        if (o < 0 || o + 4 > b.Length) throw new InvalidDataException("VBF table extends past the header");
        return BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    }

    private static ulong U64(ReadOnlySpan<byte> b, int o) {
        if (o < 0 || o + 8 > b.Length) throw new InvalidDataException("VBF table extends past the header");
        return BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
    }

    private static string CString(ReadOnlySpan<byte> table, int offset) {
        if (offset >= table.Length) throw new InvalidDataException("VBF path offset outside the path table");
        ReadOnlySpan<byte> rest = table[offset..];
        int end = rest.IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException("unterminated VBF path");
        return Encoding.UTF8.GetString(rest[..end]);
    }
}
