using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using GTAModAssistant.Core.Archives;

namespace GTAModAssistant.Tests;

internal static class SyntheticGtaFixture
{
    public static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "gtama_tests_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Builds a valid minimal PE32+ (x86-64) binary containing a real .rsrc section with VS_FIXEDFILEINFO (0xFEEF04BD)
    /// encoding the exact version (e.g. 1.0.3411.0).
    /// </summary>
    public static byte[] CreateSyntheticPe64WithVersion(int major, int minor, int build, int revision)
    {
        byte[] pe = new byte[1024];
        pe[0] = (byte)'M';
        pe[1] = (byte)'Z';
        int peOffset = 0x80;
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(0x3C, 4), peOffset);

        // PE\0\0 signature
        pe[peOffset + 0] = (byte)'P';
        pe[peOffset + 1] = (byte)'E';
        // Machine = AMD64 (0x8664), NumberOfSections = 1, SizeOfOptionalHeader = 240 (0xF0)
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(peOffset + 4, 2), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(peOffset + 6, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(peOffset + 20, 2), 240);

        int optOffset = peOffset + 24;
        // PE32+ Magic = 0x20B
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(optOffset, 2), 0x020B);

        // 1 section header (.rsrc) at optOffset + 240
        int secOffset = optOffset + 240;
        Encoding.ASCII.GetBytes(".rsrc").CopyTo(pe.AsSpan(secOffset, 5));
        uint rsrcRawPtr = 512;
        uint rsrcRawSize = 256;
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(secOffset + 16, 4), rsrcRawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(secOffset + 20, 4), rsrcRawPtr);

        // Write VS_FIXEDFILEINFO at offset 544 inside .rsrc
        int vsOffset = 544;
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(vsOffset + 0, 4), 0xFEEF04BDu);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(vsOffset + 4, 4), 0x00010000u);
        uint ms = ((uint)major << 16) | ((uint)minor & 0xFFFFu);
        uint ls = ((uint)build << 16) | ((uint)revision & 0xFFFFu);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(vsOffset + 8, 4), ms);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(vsOffset + 12, 4), ls);

        return pe;
    }

    public static (string GtaRoot, string OpenIvExe) CreateSyntheticEpicGtaEnvironment(
        string rootTempDir,
        int buildNumber = 3411,
        bool openUpdateRpf = true)
    {
        string gtaRoot = Path.Combine(rootTempDir, "Epic Games", "GTAV");
        Directory.CreateDirectory(gtaRoot);
        Directory.CreateDirectory(Path.Combine(gtaRoot, "update"));

        File.WriteAllBytes(Path.Combine(gtaRoot, "GTA5.exe"), CreateSyntheticPe64WithVersion(1, 0, buildNumber, 0));
        File.WriteAllBytes(Path.Combine(gtaRoot, "EOSSDK-Win64-Shipping.dll"), CreateSyntheticPe64WithVersion(1, 16, 0, 0));
        File.WriteAllBytes(Path.Combine(gtaRoot, "dinput8.dll"), CreateSyntheticPe64WithVersion(1, 0, 0, 0));
        File.WriteAllBytes(Path.Combine(gtaRoot, "OpenIV.asi"), CreateSyntheticPe64WithVersion(1, 2, 0, 0));
        File.WriteAllBytes(Path.Combine(gtaRoot, "ScriptHookV.dll"), CreateSyntheticPe64WithVersion(1, 0, buildNumber, 0));

        // Create vanilla update/update.rpf
        string vanillaUpdateRpf = Path.Combine(gtaRoot, "update", "update.rpf");
        if (openUpdateRpf)
        {
            var rpf = Rpf7Archive.CreateNewOpenArchive();
            rpf.PutFile(DlcListXmlService.RpfInternalDlcListPath, Encoding.UTF8.GetBytes(DlcListXmlService.CreateBaselineDlcListXml()));
            rpf.PutFile("common/data/gameconfig.xml", Encoding.UTF8.GetBytes("<CGameConfig><Version>Vanilla</Version></CGameConfig>"));
            rpf.SaveAndVerify(vanillaUpdateRpf);
        }
        else
        {
            // Create an NG-encrypted header stub RPF7 (0x0FEFFFFF)
            byte[] ngStub = new byte[1024];
            BinaryPrimitives.WriteUInt32LittleEndian(ngStub.AsSpan(0, 4), Rpf7Archive.Rpf7Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(ngStub.AsSpan(4, 4), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(ngStub.AsSpan(8, 4), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(ngStub.AsSpan(12, 4), Rpf7Archive.NgMagic);
            File.WriteAllBytes(vanillaUpdateRpf, ngStub);
        }

        // Create vanilla x64e.rpf with nested levels/gta5/vehicles.rpf
        var x64e = Rpf7Archive.CreateNewOpenArchive();
        x64e.PutFile("levels/gta5/vehicles.rpf/police.yft", CreateSyntheticRsc7Asset("VANILLA_POLICE_YFT"));
        x64e.PutFile("levels/gta5/vehicles.rpf/police_hi.yft", CreateSyntheticRsc7Asset("VANILLA_POLICE_HI_YFT"));
        x64e.PutFile("levels/gta5/vehicles.rpf/police.ytd", CreateSyntheticRsc7Asset("VANILLA_POLICE_YTD"));
        x64e.PutFile("levels/gta5/vehicles.rpf/vehshare.ytd", CreateSyntheticRsc7Asset("VANILLA_VEHSHARE_YTD"));
        x64e.SaveAndVerify(Path.Combine(gtaRoot, "x64e.rpf"));

        string openIvDir = Path.Combine(rootTempDir, "OpenIV");
        Directory.CreateDirectory(openIvDir);
        string openIvExe = Path.Combine(openIvDir, "OpenIV.exe");
        File.WriteAllBytes(openIvExe, CreateSyntheticPe64WithVersion(4, 1, 0, 1502));

        return (gtaRoot, openIvExe);
    }

    public static byte[] CreateSyntheticRsc7Asset(string markerPayload, uint sysFlags = 0x50000010, uint gfxFlags = 0x20000004)
    {
        byte[] body = Encoding.UTF8.GetBytes(markerPayload);
        byte[] total = new byte[16 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(total.AsSpan(0, 4), Rpf7Archive.Rsc7Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(total.AsSpan(4, 4), 165u); // GTA V vehicle/drawable RSC version
        BinaryPrimitives.WriteUInt32LittleEndian(total.AsSpan(8, 4), sysFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(total.AsSpan(12, 4), gfxFlags);
        body.CopyTo(total.AsSpan(16));
        return total;
    }

    public static string CreateZipArchive(string zipPath, params (string EntryPath, byte[] Content)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            var e = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
            using var s = e.Open();
            s.Write(content, 0, content.Length);
        }
        return zipPath;
    }

    /// <summary>
    /// Builds a spec-compliant RAR 4.x archive (Rar!\x1A\x07\x00) with IEEE CRC-32/CRC-16 headers and stored (0x30) file entries
    /// so SharpCompress RarArchive extraction is genuinely exercised end-to-end.
    /// </summary>
    public static string CreateRar4Archive(string rarPath, params (string EntryPath, byte[] Content)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(rarPath)!);
        using var fs = new FileStream(rarPath, FileMode.Create, FileAccess.Write);

        // 1. Marker Block
        fs.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 });

        // 2. Archive Header Block (13 bytes)
        Span<byte> mainHdr = stackalloc byte[13];
        mainHdr.Clear();
        mainHdr[2] = 0x73; // HEAD_TYPE = MAIN_HEAD
        BinaryPrimitives.WriteUInt16LittleEndian(mainHdr.Slice(3, 2), 0x0000);
        BinaryPrimitives.WriteUInt16LittleEndian(mainHdr.Slice(5, 2), 13);
        BinaryPrimitives.WriteUInt16LittleEndian(mainHdr[..2], ComputeRarHeaderCrc16(mainHdr[2..]));
        fs.Write(mainHdr);

        // 3. File Header Blocks (HEAD_TYPE = 0x74, METHOD = 0x30 Store)
        foreach (var (entryPath, content) in entries)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(entryPath.Replace('/', '\\'));
            ushort headSize = (ushort)(32 + nameBytes.Length);
            byte[] fileHdr = new byte[headSize];

            fileHdr[2] = 0x74; // FILE_HEAD
            BinaryPrimitives.WriteUInt16LittleEndian(fileHdr.AsSpan(3, 2), 0x8000); // LONG_BLOCK
            BinaryPrimitives.WriteUInt16LittleEndian(fileHdr.AsSpan(5, 2), headSize);
            BinaryPrimitives.WriteUInt32LittleEndian(fileHdr.AsSpan(7, 4), (uint)content.Length);  // PACK_SIZE
            BinaryPrimitives.WriteUInt32LittleEndian(fileHdr.AsSpan(11, 4), (uint)content.Length); // UNP_SIZE
            fileHdr[15] = 2; // HOST_OS = Win32
            BinaryPrimitives.WriteUInt32LittleEndian(fileHdr.AsSpan(16, 4), ComputeCrc32(content));
            BinaryPrimitives.WriteUInt32LittleEndian(fileHdr.AsSpan(20, 4), 0x595D6000u); // DOS time
            fileHdr[24] = 29;   // UNP_VER = 2.9
            fileHdr[25] = 0x30; // METHOD = Store
            BinaryPrimitives.WriteUInt16LittleEndian(fileHdr.AsSpan(26, 2), (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(fileHdr.AsSpan(28, 4), 0x00000020u); // Archive attr
            nameBytes.CopyTo(fileHdr.AsSpan(32));

            BinaryPrimitives.WriteUInt16LittleEndian(fileHdr.AsSpan(0, 2), ComputeRarHeaderCrc16(fileHdr.AsSpan(2)));
            fs.Write(fileHdr);
            fs.Write(content);
        }

        // 4. End of Archive Block
        Span<byte> endHdr = stackalloc byte[7];
        endHdr[2] = 0x7B;
        BinaryPrimitives.WriteUInt16LittleEndian(endHdr.Slice(3, 2), 0x4000);
        BinaryPrimitives.WriteUInt16LittleEndian(endHdr.Slice(5, 2), 7);
        BinaryPrimitives.WriteUInt16LittleEndian(endHdr[..2], ComputeRarHeaderCrc16(endHdr[2..]));
        fs.Write(endHdr);

        return rarPath;
    }

    private static ushort ComputeRarHeaderCrc16(ReadOnlySpan<byte> data) =>
        (ushort)(ComputeCrc32(data) & 0xFFFFu);

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return ~crc;
    }
}
