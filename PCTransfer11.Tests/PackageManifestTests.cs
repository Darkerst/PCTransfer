using System.Text.Json;
using PCTransfer11.Models;
using Xunit;

namespace PCTransfer11.Tests;

public class PackageManifestTests
{
    [Fact]
    public void FileEntry_RoundTripsThroughJson_IncludingKnownFolderId()
    {
        var manifest = new PackageManifest();
        manifest.Files.Add(new PackageManifest.FileEntry
        {
            PackagePath = "Documenten",
            OriginalPath = @"C:\Users\Test\Documents",
            DisplayName = "Documenten",
            KnownFolderId = "MyDocuments"
        });

        string json = JsonSerializer.Serialize(manifest);
        var roundTripped = JsonSerializer.Deserialize<PackageManifest>(json);

        Assert.NotNull(roundTripped);
        var entry = Assert.Single(roundTripped!.Files);
        Assert.Equal("MyDocuments", entry.KnownFolderId);
        Assert.Equal("Documenten", entry.PackagePath);
    }

    [Fact]
    public void FileEntry_KnownFolderId_IsNull_ForCustomFolders()
    {
        var entry = new PackageManifest.FileEntry
        {
            PackagePath = "MijnEigenMap",
            OriginalPath = @"D:\Projecten",
            DisplayName = "MijnEigenMap"
        };

        Assert.Null(entry.KnownFolderId);
    }

    [Fact]
    public void SettingsEntry_DefaultsToNoDataOrRegistry()
    {
        var entry = new PackageManifest.SettingsEntry
        {
            AppId = "test_app",
            DisplayName = "Test App"
        };

        Assert.False(entry.HasDataFolder);
        Assert.False(entry.HasRegistryExport);
        Assert.False(entry.HasCustomData);
    }
}
