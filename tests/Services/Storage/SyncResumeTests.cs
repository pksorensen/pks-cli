using FluentAssertions;
using PKS.Infrastructure.Services;
using Xunit;

namespace PKS.CLI.Tests.Services.Storage;

/// <summary>
/// A share sync that dies at file 215 of 216 must not start over on the next run. The decision of
/// what to re-fetch is made entirely by <see cref="AzureFileShareProvider.IsLocalCopyCurrent"/>,
/// so that is where the resume behaviour is pinned.
/// </summary>
public class SyncResumeTests : IDisposable
{
    private readonly string _dir;

    public SyncResumeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pks-sync-resume-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string WriteLocal(string name, string content, DateTime? modifiedUtc = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        if (modifiedUtc is { } m) File.SetLastWriteTimeUtc(path, m);
        return path;
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void SameSize_IsCurrent()
    {
        var path = WriteLocal("a.json", "12345");

        AzureFileShareProvider.IsLocalCopyCurrent(path, 5, null).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void DifferentSize_IsNotCurrent()
    {
        var path = WriteLocal("a.json", "12345");

        AzureFileShareProvider.IsLocalCopyCurrent(path, 6, null).Should().BeFalse();
        AzureFileShareProvider.IsLocalCopyCurrent(path, 4, null).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void MissingLocalFile_IsNotCurrent()
    {
        AzureFileShareProvider.IsLocalCopyCurrent(Path.Combine(_dir, "nope.json"), 0, null).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void UnknownRemoteSize_IsNotCurrent()
    {
        // The listing didn't carry a size; re-fetching is the only safe answer.
        var path = WriteLocal("a.json", "12345");

        AzureFileShareProvider.IsLocalCopyCurrent(path, null, null).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void RemoteModifiedAfterLocal_IsNotCurrent()
    {
        // Same size but the remote file changed since we downloaded it — a JSON doc edited in place
        // is the realistic case, and size alone would miss it.
        var path = WriteLocal("a.json", "12345", DateTime.UtcNow.AddHours(-2));

        AzureFileShareProvider.IsLocalCopyCurrent(path, 5, DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void RemoteModifiedBeforeLocal_IsCurrent()
    {
        var path = WriteLocal("a.json", "12345", DateTime.UtcNow);

        AzureFileShareProvider.IsLocalCopyCurrent(path, 5, DateTimeOffset.UtcNow.AddHours(-2))
            .Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ClockSkewWithinTolerance_IsCurrent()
    {
        // The local mtime is stamped when the download finished, so the remote timestamp is
        // routinely a hair "newer". That must not force an endless re-download.
        var now = DateTime.UtcNow;
        var path = WriteLocal("a.json", "12345", now);

        AzureFileShareProvider.IsLocalCopyCurrent(path, 5, new DateTimeOffset(now, TimeSpan.Zero).AddSeconds(1))
            .Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void PartialDownloadArtifact_IsNotMistakenForTheFile()
    {
        // A killed run leaves '<name>.pks-part', never a short '<name>'. The real file only appears
        // once the rename lands, which is what makes the size check trustworthy.
        var partial = WriteLocal("a.json.pks-part", "123");

        File.Exists(Path.Combine(_dir, "a.json")).Should().BeFalse();
        AzureFileShareProvider.IsLocalCopyCurrent(Path.Combine(_dir, "a.json"), 5, null).Should().BeFalse();
        File.Exists(partial).Should().BeTrue();
    }
}
