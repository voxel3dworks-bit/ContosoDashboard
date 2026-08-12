using ContosoDashboard.Services;

namespace ContosoDashboard.Tests.TestHelpers;

public class FakeFileStorageService : IFileStorageService
{
    public bool ThrowOnUpload { get; set; }
    public List<string> UploadedPaths { get; } = new();
    public List<string> DeletedPaths { get; } = new();

    private readonly Dictionary<string, byte[]> _store = new();

    public async Task<string> UploadAsync(Stream fileStream, string fileName, string contentType, int userId, int? projectId)
    {
        if (ThrowOnUpload)
        {
            throw new IOException("Simulated disk write failure");
        }

        var extension = Path.GetExtension(fileName);
        var projectSegment = projectId.HasValue ? projectId.Value.ToString() : "personal";
        var path = $"{userId}/{projectSegment}/{Guid.NewGuid()}{extension}";

        using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer);
        _store[path] = buffer.ToArray();
        UploadedPaths.Add(path);

        return path;
    }

    public Task DeleteAsync(string filePath)
    {
        DeletedPaths.Add(filePath);
        _store.Remove(filePath);
        return Task.CompletedTask;
    }

    public Task<Stream> DownloadAsync(string filePath)
    {
        if (!_store.TryGetValue(filePath, out var bytes))
        {
            throw new FileNotFoundException("No such fake file", filePath);
        }

        return Task.FromResult<Stream>(new MemoryStream(bytes));
    }

    public Task<string> GetUrlAsync(string filePath, TimeSpan expiration) => Task.FromResult($"/fake/{filePath}");
}
