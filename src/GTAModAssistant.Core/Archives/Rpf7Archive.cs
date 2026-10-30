using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using GTAModAssistant.Core.Models;

namespace GTAModAssistant.Core.Archives;

public sealed record Rpf7HeaderInfo(
    bool IsValidRpf7,
    uint EntryCount,
    uint NamesLength,
    uint RawEncryptionFlag,
    RpfEncryptionKind EncryptionKind)
{
    public bool CanDirectEditWithoutNgKeys =>
        IsValidRpf7 && (EncryptionKind == RpfEncryptionKind.Unencrypted || EncryptionKind == RpfEncryptionKind.OpenFlag);
}

public sealed record OpaqueEncryptedEntryBlob(
    byte[] RawSectorBytes,
    uint OriginalCompressedSize,
    uint OriginalUncompressedSize,
    uint EncryptionFlag);

public sealed record Rpf7EntryInfo(
    string FullInternalPath,
    string Name,
    bool IsDirectory,
    bool IsResource,
    uint CompressedSize,
    uint UncompressedSize,
    uint SystemFlags,
    uint GraphicsFlags,
    uint EncryptionFlag = 0);

/// <summary>
/// Binary reader, extractor, mutator, and builder for Rockstar RAGE Package File v7 (RPF7) archives.
/// Directly reads and writes unencrypted / OPEN (0x4E45504F) RPF7 archives (including nested .rpf containers and RSC7 .ydd/.ytd/.yft resources),
/// and explicitly identifies AES / NG-encrypted RPF7 archives so encrypted vanilla containers are never corrupted.
/// </summary>
public sealed class Rpf7Archive
{
    public const uint Rpf7Magic = 0x52504637;   // "RPF7"
    public const uint OpenMagic = 0x4E45504F;   // "OPEN"
    public const uint AesMagic  = 0x0FFFFFF9;
    public const uint NgMagic   = 0x0FEFFFFF;
    public const uint Rsc7Magic = 0x37435352;   // "RSC7"
    public const uint DirectoryMarker = 0x7FFFFF00;
    private const int SectorSize = 512;

    private readonly Dictionary<string, InMemoryRpfFile> _files = new(StringComparer.OrdinalIgnoreCase);

    private sealed record InMemoryRpfFile(
        string NormalizedPath,
        byte[] RawFileBytes,
        bool IsResource,
        uint SystemFlags,
        uint GraphicsFlags,
        bool CompressBinary,
        bool IsOpaqueEncrypted = false,
        uint OriginalCompressedSize = 0,
        uint OriginalUncompressedSize = 0,
        uint EncryptionFlag = 0);

    public static Rpf7HeaderInfo InspectHeader(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new Rpf7HeaderInfo(false, 0, 0, 0, RpfEncryptionKind.Unknown);
        }

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return InspectHeader(fs);
    }

    public static Rpf7HeaderInfo InspectHeader(Stream stream)
    {
        if (stream.Length < 16)
        {
            return new Rpf7HeaderInfo(false, 0, 0, 0, RpfEncryptionKind.Unknown);
        }

        long origPos = stream.Position;
        stream.Position = 0;
        Span<byte> hdr = stackalloc byte[16];
        stream.ReadExactly(hdr);
        stream.Position = origPos;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr[..4]);
        if (magic != Rpf7Magic)
        {
            return new Rpf7HeaderInfo(false, 0, 0, 0, RpfEncryptionKind.Unknown);
        }

        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(4, 4));
        uint namesLen = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(8, 4));
        uint enc = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(12, 4));

        var kind = enc switch
        {
            0 => RpfEncryptionKind.Unencrypted,
            OpenMagic => RpfEncryptionKind.OpenFlag,
            AesMagic => RpfEncryptionKind.Aes,
            NgMagic => RpfEncryptionKind.Ng,
            _ => RpfEncryptionKind.Ng
        };

        return new Rpf7HeaderInfo(true, entryCount, namesLen, enc, kind);
    }

    public static Rpf7Archive CreateNewOpenArchive() => new();

    /// <summary>
    /// Reads only the RPF7 TOC and Name Table (plus uncompressed nested .rpf TOCs up to maxNestedDepth)
    /// without loading large resource payloads into memory. Returns Empty if encrypted or unreadable.
    /// </summary>
    public static IReadOnlyList<string> ListEntryPathsFast(string filePath, int maxNestedDepth = 2)
    {
        if (!File.Exists(filePath))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var results = new List<string>();
            CollectEntryPathsFast(fs, 0, fs.Length, string.Empty, maxNestedDepth, results);
            return results;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void CollectEntryPathsFast(
        Stream baseStream,
        long archiveBaseOffset,
        long archiveLength,
        string pathPrefix,
        int remainingNestedDepth,
        List<string> output)
    {
        if (archiveLength < 16 || archiveBaseOffset + archiveLength > baseStream.Length)
        {
            return;
        }

        baseStream.Position = archiveBaseOffset;
        Span<byte> hdr = stackalloc byte[16];
        baseStream.ReadExactly(hdr);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr[..4]);
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(4, 4));
        uint namesLen = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(8, 4));
        uint enc = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(12, 4));

        if (magic != Rpf7Magic || (enc != 0 && enc != OpenMagic) ||
            entryCount == 0 || entryCount > 250_000 || namesLen > 32 * 1024 * 1024)
        {
            return;
        }

        long metaBytes = 16L + entryCount * 16L + namesLen;
        if (metaBytes > archiveLength)
        {
            return;
        }

        byte[] tocBytes = new byte[entryCount * 16];
        baseStream.ReadExactly(tocBytes);
        byte[] namesBytes = new byte[namesLen];
        baseStream.ReadExactly(namesBytes);

        var rawEntries = ParseRawToc(tocBytes, namesBytes, entryCount);
        TraverseTocPathsOnly(baseStream, archiveBaseOffset, archiveLength, rawEntries, 0, pathPrefix, remainingNestedDepth, output);
    }

    private static RawTocEntry[] ParseRawToc(byte[] tocBytes, byte[] namesBytes, uint entryCount)
    {
        var rawEntries = new RawTocEntry[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            var span = tocBytes.AsSpan(i * 16, 16);
            uint w0 = BinaryPrimitives.ReadUInt32LittleEndian(span[..4]);
            uint w1 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
            uint w2 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4));
            uint w3 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12, 4));
            ulong q0 = BinaryPrimitives.ReadUInt64LittleEndian(span[..8]);

            if (w1 == DirectoryMarker)
            {
                string name = ReadNullTerminatedString(namesBytes, (int)w0);
                rawEntries[i] = new RawTocEntry(true, false, name, w2, w3, 0, 0, 0, 0, 0, 0);
            }
            else
            {
                ushort nameOffset = (ushort)(q0 & 0xFFFF);
                uint compSize = (uint)((q0 >> 16) & 0xFFFFFF);
                uint rawBlockOffset = (uint)((q0 >> 40) & 0xFFFFFF);
                bool isResource = (rawBlockOffset & 0x800000) != 0;
                uint blockOffset = rawBlockOffset & 0x7FFFFF;
                string name = ReadNullTerminatedString(namesBytes, nameOffset);

                rawEntries[i] = isResource
                    ? new RawTocEntry(false, true, name, 0, 0, blockOffset, compSize, compSize, w2, w3, 0)
                    : new RawTocEntry(false, false, name, 0, 0, blockOffset, compSize, w2, 0, 0, w3);
            }
        }
        return rawEntries;
    }

    private static void TraverseTocPathsOnly(
        Stream baseStream,
        long archiveBaseOffset,
        long archiveLength,
        RawTocEntry[] entries,
        int dirIndex,
        string prefix,
        int remainingNestedDepth,
        List<string> output)
    {
        if (dirIndex < 0 || dirIndex >= entries.Length || !entries[dirIndex].IsDirectory)
        {
            return;
        }

        var dir = entries[dirIndex];
        long start = dir.EntriesIndex;
        long end = start + dir.EntriesCount;
        if (start < 0 || end > entries.Length)
        {
            return;
        }

        for (long idx = start; idx < end; idx++)
        {
            var child = entries[idx];
            string childPath = string.IsNullOrEmpty(prefix) ? child.Name : $"{prefix}/{child.Name}";
            if (child.IsDirectory)
            {
                TraverseTocPathsOnly(baseStream, archiveBaseOffset, archiveLength, entries, (int)idx, childPath, remainingNestedDepth, output);
            }
            else
            {
                output.Add(NormalizeInternalPath(childPath));
                if (remainingNestedDepth > 0 &&
                    !child.IsResource &&
                    child.CompressedSize == 0 &&
                    child.EncryptionFlag == 0 &&
                    child.Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                {
                    long subOffset = archiveBaseOffset + (long)child.FileOffsetBlocks * SectorSize;
                    long subLen = child.UncompressedSize;
                    if (subLen >= 16 && subOffset + subLen <= archiveBaseOffset + archiveLength)
                    {
                        long savedPos = baseStream.Position;
                        CollectEntryPathsFast(baseStream, subOffset, subLen, childPath, remainingNestedDepth - 1, output);
                        baseStream.Position = savedPos;
                    }
                }
            }
        }
    }

    public static Rpf7Archive Open(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Open(fs);
    }

    public static Rpf7Archive Open(Stream stream)
    {
        var header = InspectHeader(stream);
        if (!header.IsValidRpf7)
        {
            throw new InvalidDataException("Stream is not a valid RPF7 archive (missing 0x52504637 magic).");
        }

        if (!header.CanDirectEditWithoutNgKeys)
        {
            throw new InvalidOperationException(
                $"RPF7 archive uses {header.EncryptionKind} (0x{header.RawEncryptionFlag:X8}) encryption. " +
                "Direct TOC editing without NG cipher keys is blocked to prevent archive corruption; use OpenIV .oiv package integration.");
        }

        if (header.EntryCount == 0 || header.EntryCount > 250_000 || header.NamesLength > 32 * 1024 * 1024)
        {
            throw new InvalidDataException($"RPF7 header bounds invalid (Entries={header.EntryCount}, Names={header.NamesLength}).");
        }

        stream.Position = 16;
        byte[] tocBytes = new byte[header.EntryCount * 16];
        stream.ReadExactly(tocBytes);

        byte[] namesBytes = new byte[header.NamesLength];
        stream.ReadExactly(namesBytes);

        var rawEntries = new RawTocEntry[header.EntryCount];
        for (int i = 0; i < header.EntryCount; i++)
        {
            var span = tocBytes.AsSpan(i * 16, 16);
            uint w0 = BinaryPrimitives.ReadUInt32LittleEndian(span[..4]);
            uint w1 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
            uint w2 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4));
            uint w3 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12, 4));
            ulong q0 = BinaryPrimitives.ReadUInt64LittleEndian(span[..8]);

            if (w1 == DirectoryMarker)
            {
                string name = ReadNullTerminatedString(namesBytes, (int)w0);
                rawEntries[i] = new RawTocEntry(
                    IsDirectory: true,
                    IsResource: false,
                    Name: name,
                    EntriesIndex: w2,
                    EntriesCount: w3,
                    FileOffsetBlocks: 0,
                    CompressedSize: 0,
                    UncompressedSize: 0,
                    SystemFlags: 0,
                    GraphicsFlags: 0,
                    EncryptionFlag: 0);
            }
            else
            {
                ushort nameOffset = (ushort)(q0 & 0xFFFF);
                uint compSize = (uint)((q0 >> 16) & 0xFFFFFF);
                uint rawBlockOffset = (uint)((q0 >> 40) & 0xFFFFFF);
                bool isResource = (rawBlockOffset & 0x800000) != 0;
                uint blockOffset = rawBlockOffset & 0x7FFFFF;
                string name = ReadNullTerminatedString(namesBytes, nameOffset);

                if (isResource)
                {
                    rawEntries[i] = new RawTocEntry(
                        IsDirectory: false,
                        IsResource: true,
                        Name: name,
                        EntriesIndex: 0,
                        EntriesCount: 0,
                        FileOffsetBlocks: blockOffset,
                        CompressedSize: compSize,
                        UncompressedSize: compSize,
                        SystemFlags: w2,
                        GraphicsFlags: w3,
                        EncryptionFlag: 0);
                }
                else
                {
                    rawEntries[i] = new RawTocEntry(
                        IsDirectory: false,
                        IsResource: false,
                        Name: name,
                        EntriesIndex: 0,
                        EntriesCount: 0,
                        FileOffsetBlocks: blockOffset,
                        CompressedSize: compSize,
                        UncompressedSize: w2,
                        SystemFlags: 0,
                        GraphicsFlags: 0,
                        EncryptionFlag: w3);
                }
            }
        }

        var archive = new Rpf7Archive();
        TraverseDirectory(stream, rawEntries, 0, string.Empty, archive);
        return archive;
    }

    private static void TraverseDirectory(
        Stream stream,
        RawTocEntry[] entries,
        int dirIndex,
        string prefix,
        Rpf7Archive target)
    {
        if (dirIndex < 0 || dirIndex >= entries.Length || !entries[dirIndex].IsDirectory)
        {
            return;
        }

        var dir = entries[dirIndex];
        long start = dir.EntriesIndex;
        long end = start + dir.EntriesCount;
        if (start < 0 || end > entries.Length)
        {
            throw new InvalidDataException($"RPF7 directory '{prefix}' child slice [{start}..{end}) exceeds TOC.");
        }

        for (long idx = start; idx < end; idx++)
        {
            var child = entries[idx];
            string childPath = string.IsNullOrEmpty(prefix) ? child.Name : $"{prefix}/{child.Name}";
            if (child.IsDirectory)
            {
                TraverseDirectory(stream, entries, (int)idx, childPath, target);
            }
            else if (!child.IsResource && child.EncryptionFlag != 0)
            {
                // Preserve individually NG/AES-encrypted Rockstar binary entries (e.g. branches.meta inside OPEN update.rpf)
                // verbatim by storing their exact 512-byte sector payload and original TOC metadata untouched!
                byte[] rawEncryptedSectors = ExtractOpaqueEncryptedSectors(stream, child);
                target._files[NormalizeInternalPath(childPath)] = new InMemoryRpfFile(
                    NormalizeInternalPath(childPath),
                    rawEncryptedSectors,
                    IsResource: false,
                    SystemFlags: 0,
                    GraphicsFlags: 0,
                    CompressBinary: false,
                    IsOpaqueEncrypted: true,
                    OriginalCompressedSize: child.CompressedSize,
                    OriginalUncompressedSize: child.UncompressedSize,
                    EncryptionFlag: child.EncryptionFlag);
            }
            else
            {
                byte[] extracted = ExtractEntryPayload(stream, child);
                target._files[NormalizeInternalPath(childPath)] = new InMemoryRpfFile(
                    NormalizeInternalPath(childPath),
                    extracted,
                    child.IsResource,
                    child.SystemFlags,
                    child.GraphicsFlags,
                    CompressBinary: !child.IsResource && child.CompressedSize != 0 && !child.Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    private static byte[] ExtractOpaqueEncryptedSectors(Stream stream, RawTocEntry entry)
    {
        long byteOffset = (long)entry.FileOffsetBlocks * SectorSize;
        uint logicalLen = entry.CompressedSize != 0 ? entry.CompressedSize : entry.UncompressedSize;
        long sectorAlignedLen = Math.Max(1L, (logicalLen + SectorSize - 1L) / SectorSize) * SectorSize;
        long available = stream.Length - byteOffset;
        if (available < logicalLen)
        {
            throw new InvalidDataException($"RPF7 encrypted entry '{entry.Name}' exceeds archive length.");
        }

        int readLen = (int)Math.Min(sectorAlignedLen, available);
        stream.Position = byteOffset;
        byte[] raw = new byte[readLen];
        stream.ReadExactly(raw);
        return raw;
    }

    private static byte[] ExtractEntryPayload(Stream stream, RawTocEntry entry)
    {
        long byteOffset = (long)entry.FileOffsetBlocks * SectorSize;
        if (entry.IsResource)
        {
            uint size = entry.CompressedSize;
            if (size == 0xFFFFFF)
            {
                if (byteOffset + 32 > stream.Length)
                {
                    throw new InvalidDataException($"RPF7 resource '{entry.Name}' is missing its extended size header.");
                }

                stream.Position = byteOffset + 16;
                Span<byte> sizeHeader = stackalloc byte[16];
                stream.ReadExactly(sizeHeader);
                // Oversized resource lengths are split across four bytes in the resource payload header.
                size = (uint)sizeHeader[7]
                       | ((uint)sizeHeader[14] << 8)
                       | ((uint)sizeHeader[5] << 16)
                       | ((uint)sizeHeader[2] << 24);
                if (size < 0xFFFFFF)
                {
                    throw new InvalidDataException($"RPF7 resource '{entry.Name}' has an invalid extended size.");
                }
            }

            if (byteOffset + size > stream.Length)
            {
                throw new InvalidDataException($"RPF7 resource '{entry.Name}' exceeds archive length.");
            }

            stream.Position = byteOffset;
            byte[] data = new byte[size];
            stream.ReadExactly(data);
            return data;
        }
        else
        {
            if (entry.EncryptionFlag != 0)
            {
                throw new InvalidOperationException($"RPF7 binary entry '{entry.Name}' is individually encrypted (flag={entry.EncryptionFlag}).");
            }

            if (entry.CompressedSize == 0)
            {
                // Uncompressed binary file
                if (byteOffset + entry.UncompressedSize > stream.Length)
                {
                    throw new InvalidDataException($"RPF7 binary '{entry.Name}' exceeds archive length.");
                }
                stream.Position = byteOffset;
                byte[] plain = new byte[entry.UncompressedSize];
                stream.ReadExactly(plain);
                return plain;
            }
            else
            {
                if (byteOffset + entry.CompressedSize > stream.Length)
                {
                    throw new InvalidDataException($"RPF7 compressed binary '{entry.Name}' exceeds archive length.");
                }
                stream.Position = byteOffset;
                byte[] comp = new byte[entry.CompressedSize];
                stream.ReadExactly(comp);

                using var msIn = new MemoryStream(comp);
                using var deflate = new DeflateStream(msIn, CompressionMode.Decompress);
                using var msOut = new MemoryStream((int)entry.UncompressedSize);
                deflate.CopyTo(msOut);
                return msOut.ToArray();
            }
        }
    }

    public IReadOnlyList<Rpf7EntryInfo> ListEntries()
    {
        return _files.Values
            .OrderBy(f => f.NormalizedPath, StringComparer.OrdinalIgnoreCase)
            .Select(f => new Rpf7EntryInfo(
                f.NormalizedPath,
                Path.GetFileName(f.NormalizedPath),
                IsDirectory: false,
                IsResource: f.IsResource,
                CompressedSize: f.IsOpaqueEncrypted ? f.OriginalCompressedSize : (uint)f.RawFileBytes.Length,
                UncompressedSize: f.IsOpaqueEncrypted ? f.OriginalUncompressedSize : (uint)f.RawFileBytes.Length,
                SystemFlags: f.SystemFlags,
                GraphicsFlags: f.GraphicsFlags,
                EncryptionFlag: f.EncryptionFlag))
            .ToList();
    }

    public bool IsEntryIndividuallyEncrypted(string internalPath)
    {
        var (nestedRpfPath, innerRemainder) = SplitNestedRpfPath(internalPath);
        if (nestedRpfPath is not null)
        {
            if (!_files.TryGetValue(nestedRpfPath, out var nestedEntry))
            {
                return false;
            }
            if (nestedEntry.IsOpaqueEncrypted)
            {
                return true;
            }
            using var ms = new MemoryStream(nestedEntry.RawFileBytes, writable: false);
            var nestedArchive = Open(ms);
            return nestedArchive.IsEntryIndividuallyEncrypted(innerRemainder!);
        }

        return _files.TryGetValue(NormalizeInternalPath(internalPath), out var file) && file.IsOpaqueEncrypted;
    }

    public OpaqueEncryptedEntryBlob? ExportOpaqueEncryptedEntry(string internalPath)
    {
        var (nestedRpfPath, innerRemainder) = SplitNestedRpfPath(internalPath);
        if (nestedRpfPath is not null)
        {
            if (!_files.TryGetValue(nestedRpfPath, out var nestedEntry) || nestedEntry.IsOpaqueEncrypted)
            {
                return null;
            }
            using var ms = new MemoryStream(nestedEntry.RawFileBytes, writable: false);
            return Open(ms).ExportOpaqueEncryptedEntry(innerRemainder!);
        }

        if (_files.TryGetValue(NormalizeInternalPath(internalPath), out var f) && f.IsOpaqueEncrypted)
        {
            return new OpaqueEncryptedEntryBlob(
                f.RawFileBytes.ToArray(),
                f.OriginalCompressedSize,
                f.OriginalUncompressedSize,
                f.EncryptionFlag);
        }
        return null;
    }

    public void RestoreOpaqueEncryptedEntry(string internalPath, OpaqueEncryptedEntryBlob blob)
    {
        var (nestedRpfPath, innerRemainder) = SplitNestedRpfPath(internalPath);
        if (nestedRpfPath is not null)
        {
            Rpf7Archive nestedArchive;
            if (_files.TryGetValue(nestedRpfPath, out var existingNested))
            {
                using var msIn = new MemoryStream(existingNested.RawFileBytes, writable: false);
                nestedArchive = Open(msIn);
            }
            else
            {
                nestedArchive = CreateNewOpenArchive();
            }

            nestedArchive.RestoreOpaqueEncryptedEntry(innerRemainder!, blob);
            using var msOut = new MemoryStream();
            nestedArchive.SaveToStream(msOut);
            PutDirectFile(nestedRpfPath, msOut.ToArray(), compressIfBinary: false);
            return;
        }

        string norm = NormalizeInternalPath(internalPath);
        _files[norm] = new InMemoryRpfFile(
            norm,
            blob.RawSectorBytes.ToArray(),
            IsResource: false,
            SystemFlags: 0,
            GraphicsFlags: 0,
            CompressBinary: false,
            IsOpaqueEncrypted: true,
            OriginalCompressedSize: blob.OriginalCompressedSize,
            OriginalUncompressedSize: blob.OriginalUncompressedSize,
            EncryptionFlag: blob.EncryptionFlag);
    }

    public bool ContainsFile(string internalPath)
    {
        var (nestedRpfPath, innerRemainder) = SplitNestedRpfPath(internalPath);
        if (nestedRpfPath is not null)
        {
            if (!_files.TryGetValue(nestedRpfPath, out var nestedBytesEntry))
            {
                return false;
            }
            using var ms = new MemoryStream(nestedBytesEntry.RawFileBytes, writable: false);
            var nestedArchive = Open(ms);
            return nestedArchive.ContainsFile(innerRemainder!);
        }

        return _files.ContainsKey(NormalizeInternalPath(internalPath));
    }

    public byte[] ExtractFile(string internalPath)
    {
        var (nestedRpfPath, innerRemainder) = SplitNestedRpfPath(internalPath);
        if (nestedRpfPath is not null)
        {
            if (!_files.TryGetValue(nestedRpfPath, out var nestedBytesEntry))
            {
                throw new FileNotFoundException($"Nested RPF '{nestedRpfPath}' not found inside archive.", nestedRpfPath);
            }
            using var ms = new MemoryStream(nestedBytesEntry.RawFileBytes, writable: false);
            var nestedArchive = Open(ms);
            return nestedArchive.ExtractFile(innerRemainder!);
        }

        var norm = NormalizeInternalPath(internalPath);
        if (!_files.TryGetValue(norm, out var file))
        {
            throw new FileNotFoundException($"Entry '{norm}' not found in RPF7 archive.", norm);
        }

        if (file.IsOpaqueEncrypted)
        {
            throw new InvalidOperationException(
                $"Entry '{norm}' is individually Rockstar NG-encrypted (flag={file.EncryptionFlag}) and requires OpenIV to read plaintext.");
        }

        return file.RawFileBytes.ToArray();
    }

    public void PutFile(string internalPath, byte[] content, bool compressIfBinary = false)
    {
        ArgumentNullException.ThrowIfNull(content);

        var (nestedRpfPath, innerRemainder) = SplitNestedRpfPath(internalPath);
        if (nestedRpfPath is not null)
        {
            Rpf7Archive nestedArchive;
            if (_files.TryGetValue(nestedRpfPath, out var existingNested))
            {
                using var msIn = new MemoryStream(existingNested.RawFileBytes, writable: false);
                nestedArchive = Open(msIn);
            }
            else
            {
                nestedArchive = CreateNewOpenArchive();
            }

            nestedArchive.PutFile(innerRemainder!, content, compressIfBinary);
            using var msOut = new MemoryStream();
            nestedArchive.SaveToStream(msOut);
            PutDirectFile(nestedRpfPath, msOut.ToArray(), compressIfBinary: false);
            return;
        }

        PutDirectFile(NormalizeInternalPath(internalPath), content, compressIfBinary);
    }

    public bool RemoveFile(string internalPath)
    {
        var (nestedRpfPath, innerRemainder) = SplitNestedRpfPath(internalPath);
        if (nestedRpfPath is not null)
        {
            if (!_files.TryGetValue(nestedRpfPath, out var existingNested))
            {
                return false;
            }

            using var msIn = new MemoryStream(existingNested.RawFileBytes, writable: false);
            var nestedArchive = Open(msIn);
            if (!nestedArchive.RemoveFile(innerRemainder!))
            {
                return false;
            }

            using var msOut = new MemoryStream();
            nestedArchive.SaveToStream(msOut);
            PutDirectFile(nestedRpfPath, msOut.ToArray(), compressIfBinary: false);
            return true;
        }

        return _files.Remove(NormalizeInternalPath(internalPath));
    }

    private void PutDirectFile(string normPath, byte[] content, bool compressIfBinary)
    {
        if (string.IsNullOrWhiteSpace(normPath))
        {
            throw new ArgumentException("Internal RPF path cannot be empty.", nameof(normPath));
        }

        bool isRsc7 = content.Length >= 16 &&
                      BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(0, 4)) == Rsc7Magic;

        uint sysFlags = 0;
        uint gfxFlags = 0;
        if (isRsc7)
        {
            sysFlags = BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(8, 4));
            gfxFlags = BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(12, 4));
        }

        bool shouldCompress = !isRsc7 &&
                              compressIfBinary &&
                              content.Length >= 128 &&
                              !normPath.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase);

        _files[normPath] = new InMemoryRpfFile(
            normPath,
            content.ToArray(),
            isRsc7,
            sysFlags,
            gfxFlags,
            shouldCompress);
    }

    /// <summary>
    /// Atomically writes the RPF7 archive to disk and immediately re-opens and verifies TOC integrity.
    /// </summary>
    public void SaveAndVerify(string destinationPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = destinationPath + ".gtama_tmp_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                SaveToStream(fs);
                fs.Flush(flushToDisk: true);
            }

            // Re-open from temp file and verify every entry exists before swapping into place
            var reopened = Open(tempPath);
            if (reopened._files.Count != _files.Count)
            {
                throw new InvalidDataException(
                    $"Post-write RPF7 verification failed: expected {_files.Count} entries, read back {reopened._files.Count}.");
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* ignore cleanup error */ }
            }
        }
    }

    public void SaveToStream(Stream output)
    {
        // Build directory tree from _files
        var rootNode = new DirBuildNode("");
        foreach (var file in _files.Values.OrderBy(f => f.NormalizedPath, StringComparer.OrdinalIgnoreCase))
        {
            var parts = file.NormalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var cur = rootNode;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!cur.Subdirectories.TryGetValue(parts[i], out var sub))
                {
                    sub = new DirBuildNode(parts[i]);
                    cur.Subdirectories[parts[i]] = sub;
                }
                cur = sub;
            }
            cur.Files.Add((parts[^1], file));
        }

        // BFS layout of TOC entries (entry 0 = root directory; each directory's children are contiguous!)
        var tocNodes = new List<TocBuildItem>();
        var queue = new Queue<(DirBuildNode Node, int TocIndex)>();
        tocNodes.Add(new TocBuildItem { IsDirectory = true, Name = "" });
        queue.Enqueue((rootNode, 0));

        while (queue.Count > 0)
        {
            var (dirNode, dirTocIdx) = queue.Dequeue();
            int childrenStart = tocNodes.Count;

            var childDirs = dirNode.Subdirectories.Values
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var childFiles = dirNode.Files
                .OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var enqueuedDirs = new List<(DirBuildNode, int)>();
            foreach (var sub in childDirs)
            {
                int idx = tocNodes.Count;
                tocNodes.Add(new TocBuildItem { IsDirectory = true, Name = sub.Name });
                enqueuedDirs.Add((sub, idx));
            }

            foreach (var (fileName, fileData) in childFiles)
            {
                tocNodes.Add(new TocBuildItem
                {
                    IsDirectory = false,
                    Name = fileName,
                    File = fileData
                });
            }

            int childrenCount = tocNodes.Count - childrenStart;
            tocNodes[dirTocIdx].EntriesIndex = (uint)childrenStart;
            tocNodes[dirTocIdx].EntriesCount = (uint)childrenCount;

            foreach (var item in enqueuedDirs)
            {
                queue.Enqueue(item);
            }
        }

        // Build deduplicated string table (padded to 16 bytes)
        var nameOffsets = new Dictionary<string, ushort>(StringComparer.Ordinal);
        using var nameStream = new MemoryStream();
        ushort GetOrAddName(string name)
        {
            if (nameOffsets.TryGetValue(name, out ushort existing))
            {
                return existing;
            }
            if (nameStream.Position > ushort.MaxValue - 260)
            {
                throw new InvalidOperationException("RPF7 string table exceeded 64 KB offset limit.");
            }
            ushort pos = (ushort)nameStream.Position;
            byte[] bytes = Encoding.ASCII.GetBytes(name);
            nameStream.Write(bytes, 0, bytes.Length);
            nameStream.WriteByte(0);
            nameOffsets[name] = pos;
            return pos;
        }

        foreach (var node in tocNodes)
        {
            node.NameOffset = GetOrAddName(node.Name);
        }

        while (nameStream.Position % 16 != 0)
        {
            nameStream.WriteByte(0);
        }
        byte[] nameTableBytes = nameStream.ToArray();

        // Calculate starting sector for file payloads
        long metadataEnd = 16L + (tocNodes.Count * 16L) + nameTableBytes.Length;
        long currentBlock = (metadataEnd + SectorSize - 1) / SectorSize;
        if (currentBlock == 0)
        {
            currentBlock = 1;
        }

        var preparedPayloads = new List<(long OffsetBlocks, byte[] Bytes)>();
        foreach (var node in tocNodes)
        {
            if (node.IsDirectory || node.File is null)
            {
                continue;
            }

            var f = node.File;
            byte[] diskBytes;
            if (f.IsOpaqueEncrypted)
            {
                diskBytes = f.RawFileBytes;
                node.CompressedSize = f.OriginalCompressedSize;
                node.UncompressedSize = f.OriginalUncompressedSize;
            }
            else if (f.IsResource)
            {
                diskBytes = f.RawFileBytes;
                node.CompressedSize = diskBytes.Length >= 0xFFFFFF ? 0xFFFFFFu : (uint)diskBytes.Length;
                node.UncompressedSize = (uint)diskBytes.Length;
            }
            else if (f.CompressBinary)
            {
                using var msComp = new MemoryStream();
                using (var def = new DeflateStream(msComp, CompressionLevel.Optimal, leaveOpen: true))
                {
                    def.Write(f.RawFileBytes, 0, f.RawFileBytes.Length);
                }
                byte[] comp = msComp.ToArray();
                if (comp.Length > 0 && comp.Length < f.RawFileBytes.Length && comp.Length < 0xFFFFFF)
                {
                    diskBytes = comp;
                    node.CompressedSize = (uint)comp.Length;
                }
                else
                {
                    diskBytes = f.RawFileBytes;
                    node.CompressedSize = 0; // 0 = stored uncompressed in RPF7
                }
                node.UncompressedSize = (uint)f.RawFileBytes.Length;
            }
            else
            {
                diskBytes = f.RawFileBytes;
                node.CompressedSize = 0;
                node.UncompressedSize = (uint)f.RawFileBytes.Length;
            }

            if (currentBlock >= 0x7FFFFF)
            {
                throw new InvalidOperationException("RPF7 archive exceeded maximum 512-byte sector count.");
            }

            node.BlockOffset = (uint)currentBlock;
            preparedPayloads.Add((currentBlock, diskBytes));

            long blocksUsed = Math.Max(1L, (diskBytes.Length + SectorSize - 1L) / SectorSize);
            currentBlock += blocksUsed;
        }

        // Write 16-byte RPF7 Header with OPEN (0x4E45504F) flag
        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header[..4], Rpf7Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), (uint)tocNodes.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, 4), (uint)nameTableBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(12, 4), OpenMagic);
        output.Write(header);

        // Write TOC entries
        Span<byte> entryBuf = stackalloc byte[16];
        foreach (var node in tocNodes)
        {
            entryBuf.Clear();
            if (node.IsDirectory)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(entryBuf[..4], node.NameOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(entryBuf.Slice(4, 4), DirectoryMarker);
                BinaryPrimitives.WriteUInt32LittleEndian(entryBuf.Slice(8, 4), node.EntriesIndex);
                BinaryPrimitives.WriteUInt32LittleEndian(entryBuf.Slice(12, 4), node.EntriesCount);
            }
            else if (node.File!.IsResource)
            {
                uint rawBlock = node.BlockOffset | 0x800000u;
                ulong q0 = node.NameOffset
                           | ((ulong)(node.CompressedSize & 0xFFFFFFu) << 16)
                           | ((ulong)(rawBlock & 0xFFFFFFu) << 40);
                BinaryPrimitives.WriteUInt64LittleEndian(entryBuf[..8], q0);
                BinaryPrimitives.WriteUInt32LittleEndian(entryBuf.Slice(8, 4), node.File.SystemFlags);
                BinaryPrimitives.WriteUInt32LittleEndian(entryBuf.Slice(12, 4), node.File.GraphicsFlags);
            }
            else
            {
                ulong q0 = node.NameOffset
                           | ((ulong)(node.CompressedSize & 0xFFFFFFu) << 16)
                           | ((ulong)(node.BlockOffset & 0x7FFFFFu) << 40);
                BinaryPrimitives.WriteUInt64LittleEndian(entryBuf[..8], q0);
                BinaryPrimitives.WriteUInt32LittleEndian(entryBuf.Slice(8, 4), node.UncompressedSize);
                BinaryPrimitives.WriteUInt32LittleEndian(entryBuf.Slice(12, 4), node.File!.EncryptionFlag);
            }

            output.Write(entryBuf);
        }

        // Write string table
        output.Write(nameTableBytes, 0, nameTableBytes.Length);

        // Write sector-aligned file payloads
        byte[] zeroPad = new byte[SectorSize];
        foreach (var (offsetBlocks, payload) in preparedPayloads)
        {
            long targetPos = offsetBlocks * SectorSize;
            while (output.Position < targetPos)
            {
                int chunk = (int)Math.Min(SectorSize, targetPos - output.Position);
                output.Write(zeroPad, 0, chunk);
            }
            output.Write(payload, 0, payload.Length);
        }

        // Pad final sector
        int remainder = (int)(output.Position % SectorSize);
        if (remainder != 0)
        {
            output.Write(zeroPad, 0, SectorSize - remainder);
        }
    }

    public static (string? NestedRpfPath, string? InnerRemainder) SplitNestedRpfPath(string internalPath)
    {
        var norm = NormalizeInternalPath(internalPath);
        int idx = norm.IndexOf(".rpf/", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return (null, null);
        }

        string nested = norm[..(idx + 4)];
        string remainder = norm[(idx + 5)..];
        if (string.IsNullOrWhiteSpace(remainder))
        {
            return (null, null);
        }

        return (nested, remainder);
    }

    public static string NormalizeInternalPath(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim('/');

    private static string ReadNullTerminatedString(byte[] table, int offset)
    {
        if (offset < 0 || offset >= table.Length)
        {
            return string.Empty;
        }
        int end = offset;
        while (end < table.Length && table[end] != 0)
        {
            end++;
        }
        return Encoding.ASCII.GetString(table, offset, end - offset);
    }

    private sealed record RawTocEntry(
        bool IsDirectory,
        bool IsResource,
        string Name,
        uint EntriesIndex,
        uint EntriesCount,
        uint FileOffsetBlocks,
        uint CompressedSize,
        uint UncompressedSize,
        uint SystemFlags,
        uint GraphicsFlags,
        uint EncryptionFlag);

    private sealed class DirBuildNode(string name)
    {
        public string Name { get; } = name;
        public Dictionary<string, DirBuildNode> Subdirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string FileName, InMemoryRpfFile File)> Files { get; } = new();
    }

    private sealed class TocBuildItem
    {
        public bool IsDirectory { get; init; }
        public string Name { get; init; } = string.Empty;
        public ushort NameOffset { get; set; }
        public uint EntriesIndex { get; set; }
        public uint EntriesCount { get; set; }
        public uint BlockOffset { get; set; }
        public uint CompressedSize { get; set; }
        public uint UncompressedSize { get; set; }
        public InMemoryRpfFile? File { get; init; }
    }
}
