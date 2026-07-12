using System;
using PCTransfer11.Models;
using Xunit;

namespace PCTransfer11.Tests;

public class FileSelectionItemTests
{
    [Fact]
    public void ResolveKnownFolder_ReturnsNull_ForNullOrEmpty()
    {
        Assert.Null(FileSelectionItem.ResolveKnownFolder(null));
        Assert.Null(FileSelectionItem.ResolveKnownFolder(""));
    }

    [Fact]
    public void ResolveKnownFolder_ReturnsNull_ForUnknownId()
    {
        Assert.Null(FileSelectionItem.ResolveKnownFolder("DitBestaatNiet"));
    }

    [Fact]
    public void ResolveKnownFolder_ResolvesDownloads_UnderUserProfile()
    {
        string? result = FileSelectionItem.ResolveKnownFolder("Downloads");
        Assert.NotNull(result);
        string expectedRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.StartsWith(expectedRoot, result);
        Assert.EndsWith("Downloads", result);
    }

    [Fact]
    public void ResolveKnownFolder_ResolvesMyDocuments_ToSpecialFolderPath()
    {
        string? result = FileSelectionItem.ResolveKnownFolder("MyDocuments");
        string expected = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Photos", Environment.SpecialFolder.MyPictures)]
    [InlineData("Movies", Environment.SpecialFolder.MyVideos)]
    [InlineData("Music", Environment.SpecialFolder.MyMusic)]
    public void ResolveKnownFolder_ResolvesAndroidAliases_ToMatchingWindowsFolder(string androidId, Environment.SpecialFolder expectedFolder)
    {
        string? result = FileSelectionItem.ResolveKnownFolder(androidId);
        Assert.Equal(Environment.GetFolderPath(expectedFolder), result);
    }

    [Fact]
    public void ForSpecialFolder_SetsKnownFolderId_MatchingTheEnumName()
    {
        var item = FileSelectionItem.ForSpecialFolder("Documenten", Environment.SpecialFolder.MyDocuments);
        Assert.Equal("MyDocuments", item.KnownFolderId);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), item.Path);
    }

    [Fact]
    public void ForDownloads_SetsKnownFolderId_ToDownloads()
    {
        var item = FileSelectionItem.ForDownloads("Downloads");
        Assert.Equal("Downloads", item.KnownFolderId);
    }
}
