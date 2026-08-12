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

    private async Task<bool> CanViewProjectDocumentsAsync(int userId, int projectId)
    {
        return await IsAdministratorAsync(userId) || await CanUploadToProjectAsync(userId, projectId);
    }

    private async Task<bool> IsAdministratorAsync(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        return user?.Role == UserRole.Administrator;
    }

    /// <summary>
    /// Documents visible to the caller via ownership, project membership/management, or a direct/department
    /// share (FR-015). Administrators see every document (FR-021). Used by SearchAsync so inaccessible
    /// documents are filtered out of the query itself, never after the fact.
    /// </summary>
    private async Task<IQueryable<Document>> BuildAccessibleDocumentsQueryAsync(int userId)
    {
        if (await IsAdministratorAsync(userId))
        {
            return _context.Documents;
        }

        var department = await _context.Users
            .Where(u => u.UserId == userId)
            .Select(u => u.Department)
            .FirstOrDefaultAsync();

        return _context.Documents.Where(d =>
            d.UploadedByUserId == userId ||
            (d.ProjectId != null && (
                _context.Projects.Any(p => p.ProjectId == d.ProjectId && p.ProjectManagerId == userId) ||
                _context.ProjectMembers.Any(pm => pm.ProjectId == d.ProjectId && pm.UserId == userId))) ||
            _context.DocumentShares.Any(s => s.DocumentId == d.DocumentId && (
                s.SharedWithUserId == userId ||
                (department != null && s.SharedWithDepartment == department))));
    }

    private async Task<PagedResult<DocumentSummary>> ExecutePagedQueryAsync(IQueryable<Document> query, DocumentQuery documentQuery)
    {
        query = ApplyFilters(query, documentQuery);
        var totalCount = await query.CountAsync();
        var sorted = ApplySort(query, documentQuery);

        var items = await ProjectToSummary(sorted)
            .Skip((documentQuery.Page - 1) * documentQuery.PageSize)
            .Take(documentQuery.PageSize)
            .ToListAsync();

        return new PagedResult<DocumentSummary>
        {
            Items = items,
            TotalCount = totalCount,
            Page = documentQuery.Page,
            PageSize = documentQuery.PageSize
        };
    }

    private static IQueryable<Document> ApplyFilters(IQueryable<Document> query, DocumentQuery documentQuery)
    {
        if (!string.IsNullOrWhiteSpace(documentQuery.CategoryFilter))
        {
            query = query.Where(d => d.Category == documentQuery.CategoryFilter);
        }

        if (documentQuery.ProjectIdFilter.HasValue)
        {
            query = query.Where(d => d.ProjectId == documentQuery.ProjectIdFilter);
        }

        if (documentQuery.UploadedFrom.HasValue)
        {
            query = query.Where(d => d.UploadDate >= documentQuery.UploadedFrom.Value);
        }

        if (documentQuery.UploadedTo.HasValue)
        {
            query = query.Where(d => d.UploadDate <= documentQuery.UploadedTo.Value);
        }

        return query;
    }

    private static IQueryable<Document> ApplySort(IQueryable<Document> query, DocumentQuery documentQuery)
    {
        return documentQuery.SortBy switch
        {
            DocumentSortField.Title => documentQuery.SortDescending ? query.OrderByDescending(d => d.Title) : query.OrderBy(d => d.Title),
            DocumentSortField.Category => documentQuery.SortDescending ? query.OrderByDescending(d => d.Category) : query.OrderBy(d => d.Category),
            DocumentSortField.FileSizeBytes => documentQuery.SortDescending ? query.OrderByDescending(d => d.FileSizeBytes) : query.OrderBy(d => d.FileSizeBytes),
            _ => documentQuery.SortDescending ? query.OrderByDescending(d => d.UploadDate) : query.OrderBy(d => d.UploadDate)
        };
    }

    private static IQueryable<DocumentSummary> ProjectToSummary(IQueryable<Document> query) => query.Select(d => new DocumentSummary
    {
        DocumentId = d.DocumentId,
        Title = d.Title,
        Category = d.Category,
        UploadDate = d.UploadDate,
        FileSizeBytes = d.FileSizeBytes,
        FileType = d.FileType,
        ProjectId = d.ProjectId,
        ProjectName = d.Project != null ? d.Project.Name : null,
        UploadedByUserId = d.UploadedByUserId,
        UploaderName = d.Uploader.DisplayName
    });

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

    public async Task<PagedResult<DocumentSummary>> GetMyDocumentsAsync(int requestingUserId, DocumentQuery query)
    {
        var baseQuery = _context.Documents.Where(d => d.UploadedByUserId == requestingUserId);
        return await ExecutePagedQueryAsync(baseQuery, query);
    }

    public async Task<IReadOnlyList<DocumentSummary>> GetProjectDocumentsAsync(int requestingUserId, int projectId)
    {
        if (!await CanViewProjectDocumentsAsync(requestingUserId, projectId))
        {
            return Array.Empty<DocumentSummary>();
        }

        return await ProjectToSummary(_context.Documents.Where(d => d.ProjectId == projectId))
            .OrderByDescending(d => d.UploadDate)
            .ToListAsync();
    }

    public Task<IReadOnlyList<DocumentSummary>> GetTaskDocumentsAsync(int requestingUserId, int taskId)
        => throw new NotImplementedException();

    public async Task<PagedResult<DocumentSummary>> SearchAsync(int requestingUserId, string searchTerm, DocumentQuery query)
    {
        var accessibleQuery = await BuildAccessibleDocumentsQueryAsync(requestingUserId);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim();
            accessibleQuery = accessibleQuery.Where(d =>
                d.Title.Contains(term) ||
                (d.Description != null && d.Description.Contains(term)) ||
                (d.Tags != null && d.Tags.Contains(term)) ||
                d.Uploader.DisplayName.Contains(term) ||
                (d.Project != null && d.Project.Name.Contains(term)));
        }

        return await ExecutePagedQueryAsync(accessibleQuery, query);
    }

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
