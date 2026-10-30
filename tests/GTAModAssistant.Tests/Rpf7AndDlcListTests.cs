using System.Buffers.Binary;
using System.Text;
using Xunit;
using GTAModAssistant.Core.Archives;
using GTAModAssistant.Core.Models;

namespace GTAModAssistant.Tests;

public sealed class Rpf7AndDlcListTests
{
    [Fact]
    public void Rpf7Archive_RoundTrips_Binary_Compressed_Resource_And_NestedRpf_Entries()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            string rpfPath = Path.Combine(tempDir, "test_outer.rpf");
            var archive = Rpf7Archive.CreateNewOpenArchive();

            byte[] xmlBytes = Encoding.UTF8.GetBytes(DlcListXmlService.CreateBaselineDlcListXml());
            byte[] compressibleText = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("HANDLING_META_LINE_1234567890\n", 150)));
            byte[] rsc7Ydd = SyntheticGtaFixture.CreateSyntheticRsc7Asset("PED_MODEL_YDD_DATA", 0x50000100, 0x20000200);
            byte[] nestedVehicleYtd = SyntheticGtaFixture.CreateSyntheticRsc7Asset("POLICE_TEXTURE_YTD_DATA", 0x50000333, 0x20000444);

            archive.PutFile("common/data/dlclist.xml", xmlBytes, compressIfBinary: false);
            archive.PutFile("common/data/handling.meta", compressibleText, compressIfBinary: true);
            archive.PutFile("models/player.ydd", rsc7Ydd, compressIfBinary: false);
            archive.PutFile("levels/gta5/vehicles.rpf/police.ytd", nestedVehicleYtd, compressIfBinary: false);

            archive.SaveAndVerify(rpfPath);

            var header = Rpf7Archive.InspectHeader(rpfPath);
            Assert.True(header.IsValidRpf7);
            Assert.Equal(RpfEncryptionKind.OpenFlag, header.EncryptionKind);
            Assert.True(header.CanDirectEditWithoutNgKeys);

            var reopened = Rpf7Archive.Open(rpfPath);
            Assert.Equal(xmlBytes, reopened.ExtractFile("common/data/dlclist.xml"));
            Assert.Equal(compressibleText, reopened.ExtractFile("common/data/handling.meta"));
            Assert.Equal(rsc7Ydd, reopened.ExtractFile("models/player.ydd"));
            Assert.Equal(nestedVehicleYtd, reopened.ExtractFile("levels/gta5/vehicles.rpf/police.ytd"));

            var yddMeta = reopened.ListEntries().Single(e => e.FullInternalPath == "models/player.ydd");
            Assert.True(yddMeta.IsResource);
            Assert.Equal(0x50000100u, yddMeta.SystemFlags);
            Assert.Equal(0x20000200u, yddMeta.GraphicsFlags);
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void Rpf7Archive_RoundTrips_NestedResource_LargerThan24BitSize()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            string rpfPath = Path.Combine(tempDir, "large_nested.rpf");
            byte[] largeResource = new byte[0x1000000];
            BinaryPrimitives.WriteUInt32LittleEndian(largeResource.AsSpan(0, 4), Rpf7Archive.Rsc7Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(largeResource.AsSpan(4, 4), 165u);
            BinaryPrimitives.WriteUInt32LittleEndian(largeResource.AsSpan(8, 4), 0x50000010u);
            BinaryPrimitives.WriteUInt32LittleEndian(largeResource.AsSpan(12, 4), 0x20000004u);
            largeResource[16 + 2] = 1;

            var archive = Rpf7Archive.CreateNewOpenArchive();
            archive.PutFile(
                "dlcpacks/patchday/dlc.rpf/x64/models/cdimages/weapons.rpf/w_ar_carbinerifle.ytd",
                largeResource);
            archive.SaveAndVerify(rpfPath);

            var reopened = Rpf7Archive.Open(rpfPath);
            Assert.Equal(
                largeResource,
                reopened.ExtractFile("dlcpacks/patchday/dlc.rpf/x64/models/cdimages/weapons.rpf/w_ar_carbinerifle.ytd"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Rpf7Archive_BlocksDirectMutation_WhenNgEncrypted()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            string ngPath = Path.Combine(tempDir, "encrypted_update.rpf");
            byte[] raw = new byte[512];
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0, 4), Rpf7Archive.Rpf7Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4, 4), 2u);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(8, 4), 16u);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(12, 4), Rpf7Archive.NgMagic);
            File.WriteAllBytes(ngPath, raw);

            var hdr = Rpf7Archive.InspectHeader(ngPath);
            Assert.True(hdr.IsValidRpf7);
            Assert.Equal(RpfEncryptionKind.Ng, hdr.EncryptionKind);
            Assert.False(hdr.CanDirectEditWithoutNgKeys);

            Assert.Throws<InvalidOperationException>(() => Rpf7Archive.Open(ngPath));
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void DlcListXmlService_PreventsDuplicates_ValidatesXml_AndUpdatesInsideBinaryRpf()
    {
        string tempDir = SyntheticGtaFixture.CreateTempDirectory();
        try
        {
            string baseXml = DlcListXmlService.CreateBaselineDlcListXml();

            var first = DlcListXmlService.AddEntryIdempotent(baseXml, "elegyrh7");
            Assert.True(first.WasModified);
            Assert.False(first.AlreadyPresent);
            Assert.Equal("dlcpacks:/elegyrh7/", first.CanonicalEntry);

            // Attempt duplicate with Windows backslashes and mixed case
            var duplicateAttempt = DlcListXmlService.AddEntryIdempotent(first.ValidatedXmlContent, @"dlcpacks:\ELEGYRH7\");
            Assert.False(duplicateAttempt.WasModified);
            Assert.True(duplicateAttempt.AlreadyPresent);
            Assert.Equal(1, duplicateAttempt.AllEntries.Count(e => e.Contains("elegyrh7", StringComparison.OrdinalIgnoreCase)));

            // Reject malformed XML and XXE DTD
            Assert.False(DlcListXmlService.ValidateDlcListXml("<SMandatoryPacksData><Paths><Item>unclosed", out _, out _));
            string xxeXml = "<?xml version=\"1.0\"?><!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><SMandatoryPacksData><Paths><Item>&xxe;</Item></Paths></SMandatoryPacksData>";
            Assert.False(DlcListXmlService.ValidateDlcListXml(xxeXml, out _, out _));

            // Direct binary RPF7 update & removal
            string updateRpf = Path.Combine(tempDir, "update.rpf");
            var rpf = Rpf7Archive.CreateNewOpenArchive();
            rpf.PutFile(DlcListXmlService.RpfInternalDlcListPath, Encoding.UTF8.GetBytes(baseXml));
            rpf.SaveAndVerify(updateRpf);

            DlcListXmlService.UpdateOpenUpdateRpfDlcList(updateRpf, "supra98", remove: false);
            var checkAdded = Rpf7Archive.Open(updateRpf);
            string xmlAfterAdd = Encoding.UTF8.GetString(checkAdded.ExtractFile(DlcListXmlService.RpfInternalDlcListPath));
            Assert.Contains("dlcpacks:/supra98/", xmlAfterAdd);

            DlcListXmlService.UpdateOpenUpdateRpfDlcList(updateRpf, "supra98", remove: true);
            var checkRemoved = Rpf7Archive.Open(updateRpf);
            string xmlAfterRem = Encoding.UTF8.GetString(checkRemoved.ExtractFile(DlcListXmlService.RpfInternalDlcListPath));
            Assert.DoesNotContain("supra98", xmlAfterRem);
        }
        finally
        {
            SyntheticGtaFixture.DeleteTempDirectory(tempDir);
        }
    }
}
