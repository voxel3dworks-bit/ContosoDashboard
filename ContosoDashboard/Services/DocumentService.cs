using ContosoDashboard.Data;
using ContosoDashboard.Models;
using Microsoft.EntityFrameworkCore;

namespace ContosoDashboard.Services;

public class DocumentService : IDocumentService
{
    private static readonly string[] FallbackAllowedExtensions =
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".jpg", ".jpeg", ".png"
    };
    private const long FallbackMaxFileSizeBytes = 26_214_400; // 25 MB

    private readonly ApplicationDbContext _context;
    private readonly IFileStorageService _fileStorageService;
    private readonly IMalwareScanner _malwareScanner;
    private readonly INotificationService _notificationService;
    private readonly IConfiguration _configuration;

    public DocumentService(
        ApplicationDbContext context,
        IFileStorageService fileStorageService,
        IMalwareScanner malwareScanner,
        INotificationService notificationService,
        IConfiguration configuration)
    {
        _context = context;
        _fileStorageService = fileStorageService;
        _malwareScanner = malwareScanner;
        _notificationService = notificationService;
        _configuration = configuration;
    }

    public async Task<UploadResult> UploadAsync(int requestingUserId, DocumentUploadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return UploadResult.Failed("Title is required.");
        }

        if (!DocumentCategories.All.Contains(request.Category))
        {
            return UploadResult.Failed("Category is not valid.");
        }

        var extension = Path.GetExtension(request.FileName).ToLowerInvariant();
        if (string.IsNullOrEmpty(extension) || !GetAllowedExtensions().Contains(extension))
        {
            return UploadResult.Failed($"File type '{extension}' is not supported.");
        }

        var maxFileSizeBytes = GetMaxFileSizeBytes();
        if (request.FileSizeBytes <= 0 || request.FileSizeBytes > maxFileSizeBytes)
        {
            return UploadResult.Failed($"File exceeds the maximum allowed size of {maxFileSizeBytes / (1024 * 1024)} MB.");
        }

        if (request.ProjectId.HasValue && !await CanUploadToProjectAsync(requestingUserId, request.ProjectId.Value))
        {
            return UploadResult.Failed("You do not have permission to upload documents to this project.");
        }

        if (request.TaskId.HasValue)
        {
            var task = await _context.Tasks.FindAsync(request.TaskId.Value);
            if (task == null)
            {
                return UploadResult.Failed("The specified task was not found.");
            }

            if (request.ProjectId.HasValue && task.ProjectId != request.ProjectId)
            {
                return UploadResult.Failed("The task does not belong to the specified project.");
            }
        }

        var scanResult = await _malwareScanner.ScanAsync(request.FileStream, request.FileName, request.ContentType);
        if (!scanResult.IsClean)
        {
            return UploadResult.Failed($"File failed security scan: {scanResult.ThreatDescription}");
        }

        string filePath;
        try
        {
            filePath = await _fileStorageService.UploadAsync(request.FileStream, request.FileName, request.ContentType, requestingUserId, request.ProjectId);
        }
        catch (Exception ex)
        {
            return UploadResult.Failed($"Failed to save file: {ex.Message}");
        }

        var document = new Document
        {
            Title = request.Title,
            Description = request.Description,
            Category = request.Category,
            Tags = request.Tags,
            FileName = request.FileName,
            FilePath = filePath,
            FileType = request.ContentType,
            FileSizeBytes = request.FileSizeBytes,
            UploadedByUserId = requestingUserId,
            ProjectId = request.ProjectId,
            TaskId = request.TaskId,
            UploadDate = DateTime.UtcNow,
            UpdatedDate = DateTime.UtcNow
        };

        try
        {
            _context.Documents.Add(document);
            _context.DocumentActivityLogs.Add(new DocumentActivityLog
            {
                Document = document,
                DocumentTitleSnapshot = document.Title,
                UserId = requestingUserId,
                Action = DocumentActivityType.Upload,
                Timestamp = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();
        }
        catch (Exception)
        {
            // Roll back the file we just wrote so a DB failure never leaves an orphaned file
            // (constitution Principle V: file save and DB save must succeed or fail together).
            await _fileStorageService.DeleteAsync(filePath);
            return UploadResult.Failed("Failed to save document metadata.");
        }

        return UploadResult.Ok(document.DocumentId);
    }

    private async Task<bool> CanUploadToProjectAsync(int userId, int projectId)
    {
        var project = await _context.Projects.FindAsync(projectId);
        if (project == null)
        {
            return false;
        }

        if (project.ProjectManagerId == userId)
        {
            return true;
        }

        return await _context.ProjectMembers.AnyAsync(pm => pm.ProjectId == projectId && pm.UserId == userId);
    }

    private HashSet<string> GetAllowedExtensions()
    {
        var configured = _configuration.GetSection("DocumentStorage:AllowedExtensions").Get<string[]>();
        var extensions = configured is { Length: > 0 } ? configured : FallbackAllowedExtensions;
        return new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
    }

    private long GetMaxFileSizeBytes()
    {
        var configured = _configuration.GetValue<long?>("DocumentStorage:MaxFileSizeBytes");
        return configured is > 0 ? configured.Value : FallbackMaxFileSizeBytes;
    }

    public Task<PagedResult<DocumentSummary>> GetMyDocumentsAsync(int requestingUserId, DocumentQuery query)
        => throw new NotImplementedException();

    public Task<IReadOnlyList<DocumentSummary>> GetProjectDocumentsAsync(int requestingUserId, int projectId)
        => throw new NotImplementedException();

    public Task<IReadOnlyList<DocumentSummary>> GetTaskDocumentsAsync(int requestingUserId, int taskId)
        => throw new NotImplementedException();

    public Task<PagedResult<DocumentSummary>> SearchAsync(int requestingUserId, string searchTerm, DocumentQuery query)
        => throw new NotImplementedException();

    public Task<IReadOnlyList<DocumentSummary>> GetSharedWithMeAsync(int requestingUserId)
        => throw new NotImplementedException();

    public Task<IReadOnlyList<DocumentSummary>> GetRecentAsync(int requestingUserId, int count)
        => throw new NotImplementedException();

    public Task<int> GetAccessibleDocumentCountAsync(int requestingUserId)
        => throw new NotImplementedException();

    public Task<DocumentAccessCheck> AuthorizeAccessAsync(int requestingUserId, int documentId)
        => throw new NotImplementedException();

    public Task<DocumentDetail?> GetByIdAsync(int requestingUserId, int documentId)
        => throw new NotImplementedException();

    public Task<bool> UpdateMetadataAsync(int requestingUserId, int documentId, DocumentMetadataUpdate update)
        => throw new NotImplementedException();

    public Task<UploadResult> ReplaceFileAsync(int requestingUserId, int documentId, Stream fileStream, string fileName, string contentType)
        => throw new NotImplementedException();

    public Task<bool> DeleteAsync(int requestingUserId, int documentId)
        => throw new NotImplementedException();

    public Task<bool> ShareAsync(int requestingUserId, int documentId, ShareTarget target)
        => throw new NotImplementedException();

    public Task<IReadOnlyList<DocumentActivitySummary>> GetActivityLogAsync(int requestingUserId, DateRange range)
        => throw new NotImplementedException();

    public Task<DocumentActivityReport> GenerateActivityReportAsync(int requestingUserId)
        => throw new NotImplementedException();
}
