namespace ContosoDashboard.Services;

public class LocalFileStorageService : IFileStorageService
{
    private readonly string _basePath;

    public LocalFileStorageService(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var configuredBasePath = configuration["DocumentStorage:BasePath"] ?? "AppData/uploads";
        _basePath = Path.IsPathRooted(configuredBasePath)
            ? configuredBasePath
            : Path.Combine(environment.ContentRootPath, configuredBasePath);
    }

    public async Task<string> UploadAsync(Stream fileStream, string fileName, string contentType, int userId, int? projectId)
    {
        var extension = Path.GetExtension(fileName);
        var projectSegment = projectId.HasValue ? projectId.Value.ToString() : "personal";
        var relativePath = Path.Combine(userId.ToString(), projectSegment, $"{Guid.NewGuid()}{extension}");
        var fullPath = Path.Combine(_basePath, relativePath);

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using (var destination = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true))
        {
            await fileStream.CopyToAsync(destination);
        }

        // Store with forward slashes so the path is portable (matches Azure blob name conventions)
        return relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    public Task DeleteAsync(string filePath)
    {
        var fullPath = ResolveFullPath(filePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Task.CompletedTask;
    }

    public Task<Stream> DownloadAsync(string filePath)
    {
        var fullPath = ResolveFullPath(filePath);
        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task<string> GetUrlAsync(string filePath, TimeSpan expiration)
    {
        // Local files have no directly-servable URL by design (constitution Principle V) — downloads
        // are always mediated by the authenticated DocumentsController route, keyed by DocumentId.
        // This method exists for interface parity with a future AzureBlobStorageService (which would
        // return a time-limited SAS URL) and is intentionally unsupported for local storage.
        throw new NotSupportedException(
            "LocalFileStorageService does not produce direct URLs. Use the authenticated DocumentsController download/preview routes instead.");
    }

    private string ResolveFullPath(string filePath)
    {
        var normalized = filePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(_basePath, normalized);
    }
}
