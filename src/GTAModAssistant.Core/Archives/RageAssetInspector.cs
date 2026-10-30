using System.Buffers.Binary;

namespace GTAModAssistant.Core.Archives;

public enum RageAssetKind
{
    NotRageModelOrTexture = 0,
    FragmentModelYft = 1,
    FragmentHighLodYft = 2,
    TextureDictionaryYtd = 3,
    TextureDictionaryHighResYtd = 4,
    DrawableModelYdd = 5,
    OtherRageResource = 6
}

public enum RageAssetDomainHint
{
    Unknown = 0,
    Vehicle = 1,
    Weapon = 2,
    PedOrComponent = 3,
    SharedVehicleTexture = 4
}

public sealed record RageAssetAnalysis(
    string FileName,
    string Extension,
    RageAssetKind Kind,
    RageAssetDomainHint DomainHint,
    string CanonicalModelStem,
    bool IsHighDetailVariant,
    bool HasValidRsc7Header,
    uint RscVersion,
    uint SystemFlags,
    uint GraphicsFlags,
    string FormatDisplayLabel);

/// <summary>
/// Recognizes and inspects GTA V RAGE .yft (including _hi.yft) model fragments and .ytd (including +hi.ytd / _hi.ytd) texture dictionaries.
/// </summary>
public static class RageAssetInspector
{
    private static readonly string[] WeaponPrefixes =
    {
        "w_pi_", "w_ar_", "w_sb_", "w_sg_", "w_sr_", "w_mg_", "w_lr_", "w_me_", "w_ex_", "w_am_", "w_at_"
    };

    private static readonly string[] PedComponentPrefixes =
    {
        "head_", "berd_", "hair_", "uppr_", "lowr_", "hand_", "feet_", "teef_", "accs_", "task_", "decl_", "jbib_",
        "p_head_", "p_eyes_", "p_ears_", "p_lwrist_", "p_rwrist_",
        "s_m_", "s_f_", "a_m_", "a_f_", "g_m_", "g_f_", "u_m_", "u_f_", "csb_", "ig_", "mp_m_", "mp_f_", "player_"
    };

    private static readonly HashSet<string> SharedVehicleTextureNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "vehshare.ytd",
        "vehshare_army.ytd",
        "vehshare_truck.ytd"
    };

    public static bool IsModelOrTextureAssetExtension(string? extension) =>
        extension is not null &&
        (extension.Equals(".yft", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".ytd", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".ydd", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".ymt", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".ymap", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".ytyp", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".awc", StringComparison.OrdinalIgnoreCase));

    public static bool IsYftOrYtd(string? pathOrExtension)
    {
        if (string.IsNullOrWhiteSpace(pathOrExtension))
        {
            return false;
        }

        string ext = pathOrExtension.StartsWith('.')
            ? pathOrExtension
            : Path.GetExtension(pathOrExtension);

        return ext.Equals(".yft", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".ytd", StringComparison.OrdinalIgnoreCase);
    }

    public static RageAssetAnalysis Inspect(string filePath, string? logicalRelativePath = null)
    {
        string displayPath = string.IsNullOrWhiteSpace(logicalRelativePath) ? filePath : logicalRelativePath;
        string fileName = Path.GetFileName(displayPath.Replace('\\', '/'));
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        string baseName = Path.GetFileNameWithoutExtension(fileName);

        var (kind, isHiVariant, stem, label) = ClassifyByNameAndExtension(fileName, baseName, ext);
        var domainHint = InferDomainHint(fileName, stem);

        bool hasRsc7 = false;
        uint rscVer = 0;
        uint sysFlags = 0;
        uint gfxFlags = 0;

        if (File.Exists(filePath))
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length >= 16)
                {
                    Span<byte> hdr = stackalloc byte[16];
                    fs.ReadExactly(hdr);
                    uint magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr[..4]);
                    if (magic == Rpf7Archive.Rsc7Magic)
                    {
                        hasRsc7 = true;
                        rscVer = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(4, 4));
                        sysFlags = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(8, 4));
                        gfxFlags = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(12, 4));
                    }
                }
            }
            catch
            {
                // Leave hasRsc7 = false if unreadable
            }
        }

        return new RageAssetAnalysis(
            fileName,
            ext,
            kind,
            domainHint,
            stem,
            isHiVariant,
            hasRsc7,
            rscVer,
            sysFlags,
            gfxFlags,
            label);
    }

    public static string ExtractCanonicalStem(string fileName)
    {
        string nameNoExt = Path.GetFileNameWithoutExtension(fileName.Replace('\\', '/'));
        if (nameNoExt.EndsWith("_hi", StringComparison.OrdinalIgnoreCase) && nameNoExt.Length > 3)
        {
            return nameNoExt[..^3].ToLowerInvariant();
        }
        if (nameNoExt.EndsWith("+hi", StringComparison.OrdinalIgnoreCase) && nameNoExt.Length > 3)
        {
            return nameNoExt[..^3].ToLowerInvariant();
        }
        if (nameNoExt.EndsWith("+hired", StringComparison.OrdinalIgnoreCase) && nameNoExt.Length > 6)
        {
            return nameNoExt[..^6].ToLowerInvariant();
        }
        return nameNoExt.ToLowerInvariant();
    }

    private static (RageAssetKind Kind, bool IsHi, string Stem, string Label) ClassifyByNameAndExtension(
        string fileName,
        string baseName,
        string ext)
    {
        string stem = ExtractCanonicalStem(fileName);

        if (ext == ".yft")
        {
            bool isHi = baseName.EndsWith("_hi", StringComparison.OrdinalIgnoreCase);
            return isHi
                ? (RageAssetKind.FragmentHighLodYft, true, stem, "Vehicle/Model High-LOD Fragment (_hi.yft)")
                : (RageAssetKind.FragmentModelYft, false, stem, "Vehicle/Model Base Fragment (.yft)");
        }

        if (ext == ".ytd")
        {
            bool isHi = baseName.EndsWith("+hi", StringComparison.OrdinalIgnoreCase) ||
                        baseName.EndsWith("_hi", StringComparison.OrdinalIgnoreCase) ||
                        baseName.EndsWith("+hired", StringComparison.OrdinalIgnoreCase);
            return isHi
                ? (RageAssetKind.TextureDictionaryHighResYtd, true, stem, "High-Res Texture Dictionary (+hi.ytd / _hi.ytd)")
                : (RageAssetKind.TextureDictionaryYtd, false, stem, "Texture Dictionary (.ytd)");
        }

        if (ext == ".ydd")
        {
            return (RageAssetKind.DrawableModelYdd, false, stem, "Drawable Dictionary (.ydd)");
        }

        if (IsModelOrTextureAssetExtension(ext))
        {
            return (RageAssetKind.OtherRageResource, false, stem, $"RAGE Asset ({ext})");
        }

        return (RageAssetKind.NotRageModelOrTexture, false, stem, ext);
    }

    private static RageAssetDomainHint InferDomainHint(string fileName, string stem)
    {
        if (SharedVehicleTextureNames.Contains(fileName))
        {
            return RageAssetDomainHint.SharedVehicleTexture;
        }

        foreach (var wp in WeaponPrefixes)
        {
            if (stem.StartsWith(wp, StringComparison.OrdinalIgnoreCase))
            {
                return RageAssetDomainHint.Weapon;
            }
        }

        foreach (var pp in PedComponentPrefixes)
        {
            if (stem.StartsWith(pp, StringComparison.OrdinalIgnoreCase))
            {
                return RageAssetDomainHint.PedOrComponent;
            }
        }

        return RageAssetDomainHint.Unknown;
    }
}
