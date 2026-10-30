using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace GTAModAssistant.Core.Archives;

public sealed record DlcListUpdateOutcome(
    bool WasModified,
    bool AlreadyPresent,
    string CanonicalEntry,
    string ValidatedXmlContent,
    IReadOnlyList<string> AllEntries);

public static class DlcListXmlService
{
    public const string RpfInternalDlcListPath = "common/data/dlclist.xml";

    public static string NormalizeDlcEntry(string dlcNameOrEntry)
    {
        if (string.IsNullOrWhiteSpace(dlcNameOrEntry))
        {
            throw new ArgumentException("DLC entry or pack name cannot be empty.", nameof(dlcNameOrEntry));
        }

        string s = dlcNameOrEntry.Trim().Replace('\\', '/');
        if (s.StartsWith("dlcpacks:/", StringComparison.OrdinalIgnoreCase))
        {
            string namePart = s["dlcpacks:/".Length..].Trim('/');
            if (string.IsNullOrWhiteSpace(namePart) || namePart.Contains('/'))
            {
                throw new ArgumentException($"Invalid DLC pack identifier: '{dlcNameOrEntry}'.", nameof(dlcNameOrEntry));
            }
            return $"dlcpacks:/{namePart}/";
        }

        if (s.StartsWith("platform:/", StringComparison.OrdinalIgnoreCase))
        {
            return s.EndsWith('/') ? s : s + "/";
        }

        string cleanName = s.Trim('/');
        if (string.IsNullOrWhiteSpace(cleanName) || cleanName.Contains('/') || cleanName.Contains(".."))
        {
            throw new ArgumentException($"Invalid DLC pack folder name: '{dlcNameOrEntry}'.", nameof(dlcNameOrEntry));
        }

        return $"dlcpacks:/{cleanName}/";
    }

    public static string ExtractDlcPackFolderName(string dlcEntry)
    {
        string canonical = NormalizeDlcEntry(dlcEntry);
        if (canonical.StartsWith("dlcpacks:/", StringComparison.OrdinalIgnoreCase))
        {
            return canonical["dlcpacks:/".Length..].Trim('/');
        }
        return canonical.Trim('/').Split('/').Last();
    }

    public static bool ValidateGtaXml(ReadOnlySpan<byte> utf8XmlBytes, out string? validationError)
    {
        try
        {
            using var ms = new MemoryStream(utf8XmlBytes.ToArray());
            var settings = CreateSafeXmlReaderSettings();
            using var reader = XmlReader.Create(ms, settings);
            var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            if (doc.Root is null)
            {
                validationError = "XML document has no root element.";
                return false;
            }

            validationError = null;
            return true;
        }
        catch (Exception ex)
        {
            validationError = ex.Message;
            return false;
        }
    }

    public static bool ValidateDlcListXml(string xmlContent, out XDocument? document, out string? error)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            error = "dlclist.xml content is empty.";
            return false;
        }

        try
        {
            using var sr = new StringReader(xmlContent);
            using var reader = XmlReader.Create(sr, CreateSafeXmlReaderSettings());
            var doc = XDocument.Load(reader);
            if (doc.Root is null || !string.Equals(doc.Root.Name.LocalName, "SMandatoryPacksData", StringComparison.Ordinal))
            {
                error = $"Expected root <SMandatoryPacksData>, found '<{doc.Root?.Name.LocalName ?? "null"}>'.";
                return false;
            }

            var pathsEl = doc.Root.Element("Paths");
            if (pathsEl is null)
            {
                error = "Missing required <Paths> element under <SMandatoryPacksData>.";
                return false;
            }

            foreach (var item in pathsEl.Elements("Item"))
            {
                if (string.IsNullOrWhiteSpace(item.Value))
                {
                    error = "dlclist.xml contains an empty <Item> element.";
                    return false;
                }
            }

            document = doc;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Malformed dlclist.xml: {ex.Message}";
            return false;
        }
    }

    public static string CreateBaselineDlcListXml()
    {
        return """
               <?xml version="1.0" encoding="UTF-8"?>
               <SMandatoryPacksData>
                 <Paths>
                   <Item>platform:/dlcPacks/mpBeach/</Item>
                   <Item>platform:/dlcPacks/mpBusiness/</Item>
                   <Item>dlcpacks:/mpheist/</Item>
                   <Item>dlcpacks:/patchday28ng/</Item>
                 </Paths>
               </SMandatoryPacksData>
               """;
    }

    public static DlcListUpdateOutcome AddEntryIdempotent(string existingXml, string dlcNameOrEntry)
    {
        string canonical = NormalizeDlcEntry(dlcNameOrEntry);
        if (!ValidateDlcListXml(existingXml, out var doc, out var err) || doc is null)
        {
            throw new InvalidDataException(err ?? "Invalid dlclist.xml");
        }

        var pathsEl = doc.Root!.Element("Paths")!;
        var seenCanonical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicatesToRemove = new List<XElement>();

        foreach (var item in pathsEl.Elements("Item"))
        {
            string norm = CanonicalComparisonKey(item.Value);
            if (!seenCanonical.Add(norm))
            {
                duplicatesToRemove.Add(item);
            }
        }

        bool cleanedExistingDuplicates = duplicatesToRemove.Count > 0;
        foreach (var dup in duplicatesToRemove)
        {
            dup.Remove();
        }

        string targetKey = CanonicalComparisonKey(canonical);
        bool alreadyPresent = seenCanonical.Contains(targetKey);
        if (!alreadyPresent)
        {
            pathsEl.Add(new XElement("Item", canonical));
        }

        string serialized = SerializeXml(doc);
        if (!ValidateDlcListXml(serialized, out var verifiedDoc, out var postErr) || verifiedDoc is null)
        {
            throw new InvalidDataException($"Post-update dlclist.xml validation failed: {postErr}");
        }

        var allItems = verifiedDoc.Root!.Element("Paths")!
            .Elements("Item")
            .Select(x => x.Value.Trim())
            .ToList();

        return new DlcListUpdateOutcome(
            WasModified: !alreadyPresent || cleanedExistingDuplicates,
            AlreadyPresent: alreadyPresent,
            CanonicalEntry: canonical,
            ValidatedXmlContent: serialized,
            AllEntries: allItems);
    }

    public static DlcListUpdateOutcome RemoveEntry(string existingXml, string dlcNameOrEntry)
    {
        string canonical = NormalizeDlcEntry(dlcNameOrEntry);
        if (!ValidateDlcListXml(existingXml, out var doc, out var err) || doc is null)
        {
            throw new InvalidDataException(err ?? "Invalid dlclist.xml");
        }

        string targetKey = CanonicalComparisonKey(canonical);
        var pathsEl = doc.Root!.Element("Paths")!;
        var matching = pathsEl.Elements("Item")
            .Where(e => string.Equals(CanonicalComparisonKey(e.Value), targetKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var el in matching)
        {
            el.Remove();
        }

        string serialized = SerializeXml(doc);
        if (!ValidateDlcListXml(serialized, out var verifiedDoc, out var postErr) || verifiedDoc is null)
        {
            throw new InvalidDataException($"Post-remove dlclist.xml validation failed: {postErr}");
        }

        var allItems = verifiedDoc.Root!.Element("Paths")!
            .Elements("Item")
            .Select(x => x.Value.Trim())
            .ToList();

        return new DlcListUpdateOutcome(
            WasModified: matching.Count > 0,
            AlreadyPresent: matching.Count > 0,
            CanonicalEntry: canonical,
            ValidatedXmlContent: serialized,
            AllEntries: allItems);
    }

    /// <summary>
    /// Updates common/data/dlclist.xml directly inside an OPEN RPF7 archive and verifies the result by re-reading from disk.
    /// </summary>
    public static DlcListUpdateOutcome UpdateOpenUpdateRpfDlcList(string modsUpdateRpfPath, string dlcEntry, bool remove = false)
    {
        var archive = Rpf7Archive.Open(modsUpdateRpfPath);
        string currentXml = archive.ContainsFile(RpfInternalDlcListPath)
            ? Encoding.UTF8.GetString(archive.ExtractFile(RpfInternalDlcListPath))
            : CreateBaselineDlcListXml();

        var outcome = remove
            ? RemoveEntry(currentXml, dlcEntry)
            : AddEntryIdempotent(currentXml, dlcEntry);

        byte[] utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(outcome.ValidatedXmlContent);
        archive.PutFile(RpfInternalDlcListPath, utf8NoBom, compressIfBinary: false);
        archive.SaveAndVerify(modsUpdateRpfPath);

        // Re-open from disk and verify XML entry presence/absence!
        var reopened = Rpf7Archive.Open(modsUpdateRpfPath);
        string readBackXml = Encoding.UTF8.GetString(reopened.ExtractFile(RpfInternalDlcListPath));
        if (!ValidateDlcListXml(readBackXml, out var verifiedDoc, out var err) || verifiedDoc is null)
        {
            throw new InvalidDataException($"Re-opened RPF7 dlclist.xml failed validation: {err}");
        }

        string key = CanonicalComparisonKey(outcome.CanonicalEntry);
        bool existsNow = verifiedDoc.Root!.Element("Paths")!.Elements("Item")
            .Any(i => string.Equals(CanonicalComparisonKey(i.Value), key, StringComparison.OrdinalIgnoreCase));

        if (!remove && !existsNow)
        {
            throw new InvalidOperationException($"Verification failed: '{outcome.CanonicalEntry}' missing after RPF7 save.");
        }
        if (remove && existsNow)
        {
            throw new InvalidOperationException($"Verification failed: '{outcome.CanonicalEntry}' still present after RPF7 removal.");
        }

        return outcome;
    }

    private static string CanonicalComparisonKey(string rawItem)
    {
        string s = (rawItem ?? string.Empty).Trim().Replace('\\', '/');
        if (!s.EndsWith('/'))
        {
            s += "/";
        }
        return s.ToLowerInvariant();
    }

    private static string SerializeXml(XDocument doc)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = false
        };

        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            doc.Save(writer);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static XmlReaderSettings CreateSafeXmlReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,
        MaxCharactersInDocument = 32 * 1024 * 1024
    };
}
