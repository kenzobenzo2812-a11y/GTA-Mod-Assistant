using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace GTAModAssistant.Core.Security;

public sealed record PeInspectionResult(
    bool IsValidPe,
    bool Is64Bit,
    bool IsManagedDotNetAssembly,
    bool ReferencesScriptHookVDotNet,
    Version? FileVersion);

public static class PeBinaryInspector
{
    private const uint VsFixedFileInfoSignature = 0xFEEF04BD;

    public static PeInspectionResult Inspect(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new PeInspectionResult(false, false, false, false, null);
        }

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 128)
            {
                return new PeInspectionResult(false, false, false, false, null);
            }

            Span<byte> dosHeader = stackalloc byte[64];
            fs.ReadExactly(dosHeader);
            if (dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
            {
                return new PeInspectionResult(false, false, false, false, null);
            }

            int peOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader.Slice(0x3C, 4));
            if (peOffset < 64 || peOffset + 24 > fs.Length)
            {
                return new PeInspectionResult(false, false, false, false, null);
            }

            fs.Position = peOffset;
            Span<byte> peHeader = stackalloc byte[24];
            fs.ReadExactly(peHeader);
            if (peHeader[0] != (byte)'P' || peHeader[1] != (byte)'E' || peHeader[2] != 0 || peHeader[3] != 0)
            {
                return new PeInspectionResult(false, false, false, false, null);
            }

            ushort numberOfSections = BinaryPrimitives.ReadUInt16LittleEndian(peHeader.Slice(6, 2));
            ushort sizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(peHeader.Slice(20, 2));
            if (sizeOfOptionalHeader < 24 || fs.Position + sizeOfOptionalHeader > fs.Length)
            {
                return new PeInspectionResult(true, false, false, false, TryGetWindowsFileVersion(filePath));
            }

            byte[] optHeader = new byte[sizeOfOptionalHeader];
            fs.ReadExactly(optHeader);
            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(optHeader.AsSpan(0, 2));
            bool isPe32Plus = magic == 0x20B;
            bool isPe32 = magic == 0x10B;
            if (!isPe32 && !isPe32Plus)
            {
                return new PeInspectionResult(true, false, false, false, TryGetWindowsFileVersion(filePath));
            }

            // DataDirectory[14] = CLR Runtime Header
            int dataDirOffset = isPe32Plus ? 112 : 96;
            bool isManaged = false;
            if (sizeOfOptionalHeader >= dataDirOffset + 15 * 8)
            {
                uint clrRva = BinaryPrimitives.ReadUInt32LittleEndian(optHeader.AsSpan(dataDirOffset + 14 * 8, 4));
                uint clrSize = BinaryPrimitives.ReadUInt32LittleEndian(optHeader.AsSpan(dataDirOffset + 14 * 8 + 4, 4));
                isManaged = clrRva != 0 && clrSize > 0;
            }

            // Read section headers (40 bytes each) to locate .rsrc and inspect small managed DLL metadata
            Version? resourceVersion = null;
            Span<byte> sec = stackalloc byte[40];
            for (int i = 0; i < numberOfSections && fs.Position + 40 <= fs.Length; i++)
            {
                fs.ReadExactly(sec);
                string secName = Encoding.ASCII.GetString(sec.Slice(0, 8)).TrimEnd('\0');
                uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(sec.Slice(16, 4));
                uint rawPtr = BinaryPrimitives.ReadUInt32LittleEndian(sec.Slice(20, 4));

                if (secName.Equals(".rsrc", StringComparison.OrdinalIgnoreCase) &&
                    rawPtr > 0 && rawSize >= 52 && rawPtr + rawSize <= fs.Length && rawSize <= 16 * 1024 * 1024)
                {
                    long savedPos = fs.Position;
                    resourceVersion = ScanSectionForFixedFileInfo(fs, rawPtr, (int)rawSize);
                    fs.Position = savedPos;
                }
            }

            if (resourceVersion is null)
            {
                resourceVersion = TryGetWindowsFileVersion(filePath);
            }

            // Fallback scan of tail if synthetic or nonstandard section layout (< 4 MB file)
            if (resourceVersion is null && fs.Length <= 4 * 1024 * 1024)
            {
                resourceVersion = ScanSectionForFixedFileInfo(fs, 0, (int)fs.Length);
            }

            bool refsShvdn = false;
            if (isManaged && fs.Length <= 32 * 1024 * 1024)
            {
                fs.Position = 0;
                byte[] allBytes = new byte[fs.Length];
                fs.ReadExactly(allBytes);
                string ascii = Encoding.Latin1.GetString(allBytes);
                refsShvdn = ascii.Contains("ScriptHookVDotNet", StringComparison.OrdinalIgnoreCase) ||
                            ascii.Contains("GTA.Script", StringComparison.Ordinal);
            }

            return new PeInspectionResult(true, isPe32Plus, isManaged, refsShvdn, resourceVersion);
        }
        catch
        {
            return new PeInspectionResult(false, false, false, false, TryGetWindowsFileVersion(filePath));
        }
    }

    private static Version? ScanSectionForFixedFileInfo(FileStream fs, long offset, int length)
    {
        fs.Position = offset;
        byte[] buf = new byte[length];
        fs.ReadExactly(buf);

        for (int i = 0; i <= buf.Length - 52; i += 2)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i, 4)) == VsFixedFileInfoSignature)
            {
                uint strucVersion = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i + 4, 4));
                if (strucVersion != 0x00010000)
                {
                    continue;
                }

                uint fileVersionMs = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i + 8, 4));
                uint fileVersionLs = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i + 12, 4));

                int major = (int)(fileVersionMs >> 16);
                int minor = (int)(fileVersionMs & 0xFFFF);
                int build = (int)(fileVersionLs >> 16);
                int privatePart = (int)(fileVersionLs & 0xFFFF);

                if (major >= 0 && major <= 100 && build >= 0)
                {
                    return new Version(major, minor, build, privatePart);
                }
            }
        }

        return null;
    }

    private static Version? TryGetWindowsFileVersion(string filePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(filePath);
            if (info.FileMajorPart > 0 || info.FileBuildPart > 0)
            {
                return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
            }
        }
        catch
        {
            // Ignore and return null
        }

        return null;
    }
}
