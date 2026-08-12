using ContosoDashboard.Services;
using ContosoDashboard.Tests.TestHelpers;
using Microsoft.Extensions.Configuration;

namespace ContosoDashboard.Tests.Services;

public class LocalFileStorageServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly LocalFileStorageService _sut;

    public LocalFileStorageServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ContosoDashboardTests_" + Guid.NewGuid());

        var environment = new FakeWebHostEnvironment { ContentRootPath = _tempRoot };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DocumentStorage:BasePath"] = "uploads"
            })
            .Build();

        _sut = new LocalFileStorageService(environment, configuration);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UploadAsync_ProducesGuidBasedPath_MatchingExpectedPattern()
    {
        using var content = new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46 });

        var path = await _sut.UploadAsync(content, "user supplied name.pdf", "application/pdf", userId: 7, projectId: 3);

        var segments = path.Split('/');
        Assert.Equal(3, segments.Length);
        Assert.Equal("7", segments[0]);
        Assert.Equal("3", segments[1]);
        Assert.True(Guid.TryParse(Path.GetFileNameWithoutExtension(segments[2]), out _));
        Assert.EndsWith(".pdf", segments[2]);
    }

    [Fact]
    public async Task UploadAsync_NeverIncorporatesCallerSuppliedFileNameIntoPath()
    {
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });

        var path = await _sut.UploadAsync(content, "../../etc/passwd.pdf", "application/pdf", userId: 1, projectId: null);

        Assert.DoesNotContain("etc", path);
        Assert.DoesNotContain("passwd", path);
        Assert.DoesNotContain("..", path);
        Assert.Contains("personal", path); // null projectId -> "personal" segment
    }

    [Fact]
    public async Task UploadAsync_UsesPersonalSegment_WhenProjectIdIsNull()
    {
        using var content = new MemoryStream(new byte[] { 1 });

        var path = await _sut.UploadAsync(content, "notes.txt", "text/plain", userId: 9, projectId: null);

        Assert.Equal("9/personal", string.Join('/', path.Split('/')[..2]));
    }

    [Fact]
    public async Task DownloadAsync_RoundTripsExactBytesThatWereUploaded()
    {
        var originalBytes = new byte[] { 10, 20, 30, 40, 50 };
        using var content = new MemoryStream(originalBytes);

        var path = await _sut.UploadAsync(content, "data.bin", "application/octet-stream", userId: 2, projectId: null);

        await using var downloaded = await _sut.DownloadAsync(path);
        using var buffer = new MemoryStream();
        await downloaded.CopyToAsync(buffer);

        Assert.Equal(originalBytes, buffer.ToArray());
    }

    [Fact]
    public async Task DeleteAsync_RemovesFile_SoItCanNoLongerBeDownloaded()
    {
        using var content = new MemoryStream(new byte[] { 1, 2 });
        var path = await _sut.UploadAsync(content, "temp.txt", "text/plain", userId: 4, projectId: null);

        await _sut.DeleteAsync(path);

        await Assert.ThrowsAsync<FileNotFoundException>(() => _sut.DownloadAsync(path));
    }
}
