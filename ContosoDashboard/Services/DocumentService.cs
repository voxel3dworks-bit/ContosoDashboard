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

            // Documents uploaded from a task are automatically associated with the task's project (FR-026).
            request.ProjectId ??= task.ProjectId;
        }

        if (request.ProjectId.HasValue && !await CanUploadToProjectAsync(requestingUserId, request.ProjectId.Value))
        {
            return UploadResult.Failed("You do not have permission to upload documents to this project.");
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

        await NotifyProjectMembersOfNewDocumentAsync(document);

        return UploadResult.Ok(document.DocumentId);
    }

    /// <summary>
    /// Notifies a project's members and manager when a document is uploaded to their project (FR-029),
    /// excluding the uploader themselves.
    /// </summary>
    private async Task NotifyProjectMembersOfNewDocumentAsync(Document document)
    {
        if (!document.ProjectId.HasValue)
        {
            return;
        }

        var project = await _context.Projects.FindAsync(document.ProjectId.Value);
        if (project == null)
        {
            return;
        }

        var memberIds = await _context.ProjectMembers
            .Where(pm => pm.ProjectId == document.ProjectId.Value)
            .Select(pm => pm.UserId)
            .ToListAsync();

        var recipientIds = memberIds
            .Append(project.ProjectManagerId)
            .Distinct()
            .Where(id => id != document.UploadedByUserId);

        foreach (var recipientId in recipientIds)
        {
            await _notificationService.CreateNotificationAsync(new Notification
            {
                UserId = recipientId,
                Title = "New project document",
                Message = $"A new document \"{document.Title}\" was added to {project.Name}.",
                Type = NotificationType.DocumentAddedToProject,
                Priority = NotificationPriority.Informational
            });
        }
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

    private async Task<bool> IsProjectManagerOfAsync(int userId, int projectId)
    {
        var project = await _context.Projects.FindAsync(projectId);
        return project != null && project.ProjectManagerId == userId;
    }

    /// <summary>
    /// FR-024's "team" has no dedicated entity in this schema — it's grounded in the existing
    /// ProjectMember.Role == "TeamLead" relationship: requestingUserId leads a project's team if they
    /// hold that role on it, and uploaderUserId is a "member of their team" if they're on the same project.
    /// </summary>
    private async Task<bool> IsTeamLeadOfUploaderAsync(int requestingUserId, int uploaderUserId)
    {
        if (requestingUserId == uploaderUserId)
        {
            return false;
        }

        var requestingUser = await _context.Users.FindAsync(requestingUserId);
        if (requestingUser?.Role != UserRole.TeamLead)
        {
            return false;
        }

        var ledProjectIds = await _context.ProjectMembers
            .Where(pm => pm.UserId == requestingUserId && pm.Role == "TeamLead")
            .Select(pm => pm.ProjectId)
            .ToListAsync();

        if (ledProjectIds.Count == 0)
        {
            return false;
        }

        return await _context.ProjectMembers
            .AnyAsync(pm => pm.UserId == uploaderUserId && ledProjectIds.Contains(pm.ProjectId));
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

    public async Task<IReadOnlyList<DocumentSummary>> GetTaskDocumentsAsync(int requestingUserId, int taskId)
    {
        var task = await _context.Tasks.FindAsync(taskId);
        if (task == null)
        {
            return Array.Empty<DocumentSummary>();
        }

        // A task with a project follows project-level visibility; a task with no project is only
        // visible to its assignee/creator or an Administrator.
        var isAuthorized = task.ProjectId.HasValue
            ? await CanViewProjectDocumentsAsync(requestingUserId, task.ProjectId.Value)
            : task.AssignedUserId == requestingUserId || task.CreatedByUserId == requestingUserId || await IsAdministratorAsync(requestingUserId);

        if (!isAuthorized)
        {
            return Array.Empty<DocumentSummary>();
        }

        return await ProjectToSummary(_context.Documents.Where(d => d.TaskId == taskId))
            .OrderByDescending(d => d.UploadDate)
            .ToListAsync();
    }

    public async Task<bool> AttachToTaskAsync(int requestingUserId, int documentId, int taskId)
    {
        var document = await _context.Documents.FindAsync(documentId);
        if (document == null || document.UploadedByUserId != requestingUserId)
        {
            return false;
        }

        var task = await _context.Tasks.FindAsync(taskId);
        if (task == null)
        {
            return false;
        }

        // Don't silently reassign a document that already belongs to a different project than the task.
        if (document.ProjectId.HasValue && task.ProjectId.HasValue && document.ProjectId != task.ProjectId)
        {
            return false;
        }

        document.TaskId = taskId;
        document.ProjectId ??= task.ProjectId;
        document.UpdatedDate = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

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

    public async Task<IReadOnlyList<DocumentSummary>> GetSharedWithMeAsync(int requestingUserId)
    {
        var department = await _context.Users
            .Where(u => u.UserId == requestingUserId)
            .Select(u => u.Department)
            .FirstOrDefaultAsync();

        var sharedDocumentIds = await _context.DocumentShares
            .Where(s => s.SharedWithUserId == requestingUserId || (department != null && s.SharedWithDepartment == department))
            .Select(s => s.DocumentId)
            .Distinct()
            .ToListAsync();

        return await ProjectToSummary(_context.Documents.Where(d => sharedDocumentIds.Contains(d.DocumentId)))
            .OrderByDescending(d => d.UploadDate)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<DocumentSummary>> GetRecentAsync(int requestingUserId, int count)
    {
        return await ProjectToSummary(_context.Documents.Where(d => d.UploadedByUserId == requestingUserId))
            .OrderByDescending(d => d.UploadDate)
            .Take(count)
            .ToListAsync();
    }

    public async Task<int> GetAccessibleDocumentCountAsync(int requestingUserId)
    {
        var query = await BuildAccessibleDocumentsQueryAsync(requestingUserId);
        return await query.CountAsync();
    }

    public async Task<DocumentAccessCheck> AuthorizeAccessAsync(int requestingUserId, int documentId)
    {
        var document = await _context.Documents
            .Include(d => d.Uploader)
            .Include(d => d.Project)
            .FirstOrDefaultAsync(d => d.DocumentId == documentId);

        if (document == null || !await CanAccessDocumentAsync(requestingUserId, document))
        {
            return DocumentAccessCheck.Denied();
        }

        return DocumentAccessCheck.Granted(ToDetail(document));
    }

    public async Task RecordDownloadAsync(int requestingUserId, int documentId)
    {
        var title = await _context.Documents
            .Where(d => d.DocumentId == documentId)
            .Select(d => d.Title)
            .FirstOrDefaultAsync();

        _context.DocumentActivityLogs.Add(new DocumentActivityLog
        {
            DocumentId = documentId,
            DocumentTitleSnapshot = title,
            UserId = requestingUserId,
            Action = DocumentActivityType.Download,
            Timestamp = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Owner, project member/PM, share recipient (direct or department match), or Administrator (FR-016/FR-017).
    /// Deliberately excludes "Team Lead of the uploader" — FR-024 grants Team Leads metadata view/edit rights
    /// only, not file download/preview rights (see contracts/document-service-contract.md).
    /// </summary>
    private async Task<bool> CanAccessDocumentAsync(int userId, Document document)
    {
        if (document.UploadedByUserId == userId)
        {
            return true;
        }

        if (await IsAdministratorAsync(userId))
        {
            return true;
        }

        if (document.ProjectId.HasValue && await CanUploadToProjectAsync(userId, document.ProjectId.Value))
        {
            return true;
        }

        var department = await _context.Users
            .Where(u => u.UserId == userId)
            .Select(u => u.Department)
            .FirstOrDefaultAsync();

        return await _context.DocumentShares.AnyAsync(s => s.DocumentId == document.DocumentId && (
            s.SharedWithUserId == userId ||
            (department != null && s.SharedWithDepartment == department)));
    }

    private static DocumentDetail ToDetail(Document d) => new()
    {
        DocumentId = d.DocumentId,
        Title = d.Title,
        Category = d.Category,
        UploadDate = d.UploadDate,
        FileSizeBytes = d.FileSizeBytes,
        FileType = d.FileType,
        ProjectId = d.ProjectId,
        ProjectName = d.Project?.Name,
        UploadedByUserId = d.UploadedByUserId,
        UploaderName = d.Uploader.DisplayName,
        Description = d.Description,
        Tags = d.Tags,
        FileName = d.FileName,
        FilePath = d.FilePath,
        TaskId = d.TaskId,
        UpdatedDate = d.UpdatedDate
    };

    public async Task<DocumentDetail?> GetByIdAsync(int requestingUserId, int documentId)
    {
        var document = await _context.Documents
            .Include(d => d.Uploader)
            .Include(d => d.Project)
            .FirstOrDefaultAsync(d => d.DocumentId == documentId);

        if (document == null)
        {
            return null;
        }

        var canManage = document.UploadedByUserId == requestingUserId
            || await IsTeamLeadOfUploaderAsync(requestingUserId, document.UploadedByUserId);

        return canManage ? ToDetail(document) : null;
    }

    public async Task<bool> UpdateMetadataAsync(int requestingUserId, int documentId, DocumentMetadataUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.Title) || !DocumentCategories.All.Contains(update.Category))
        {
            return false;
        }

        var document = await _context.Documents.FindAsync(documentId);
        if (document == null)
        {
            return false;
        }

        var canManage = document.UploadedByUserId == requestingUserId
            || await IsTeamLeadOfUploaderAsync(requestingUserId, document.UploadedByUserId);

        if (!canManage)
        {
            return false;
        }

        document.Title = update.Title;
        document.Description = update.Description;
        document.Category = update.Category;
        document.Tags = update.Tags;
        document.UpdatedDate = DateTime.UtcNow;

        _context.DocumentActivityLogs.Add(new DocumentActivityLog
        {
            DocumentId = documentId,
            DocumentTitleSnapshot = document.Title,
            UserId = requestingUserId,
            Action = DocumentActivityType.MetadataEdit,
            Timestamp = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<UploadResult> ReplaceFileAsync(int requestingUserId, int documentId, Stream fileStream, string fileName, string contentType)
    {
        var document = await _context.Documents.FindAsync(documentId);
        if (document == null)
        {
            return UploadResult.Failed("Document not found.");
        }

        // Only the owner can replace the file — distinct from metadata edit rights, which FR-024 also
        // grants to Team Leads; file replacement was not extended to Team Leads.
        if (document.UploadedByUserId != requestingUserId)
        {
            return UploadResult.Failed("Only the document owner can replace its file.");
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (string.IsNullOrEmpty(extension) || !GetAllowedExtensions().Contains(extension))
        {
            return UploadResult.Failed($"File type '{extension}' is not supported.");
        }

        var maxFileSizeBytes = GetMaxFileSizeBytes();
        if (fileStream.Length <= 0 || fileStream.Length > maxFileSizeBytes)
        {
            return UploadResult.Failed($"File exceeds the maximum allowed size of {maxFileSizeBytes / (1024 * 1024)} MB.");
        }

        var scanResult = await _malwareScanner.ScanAsync(fileStream, fileName, contentType);
        if (!scanResult.IsClean)
        {
            return UploadResult.Failed($"File failed security scan: {scanResult.ThreatDescription}");
        }

        string newFilePath;
        try
        {
            newFilePath = await _fileStorageService.UploadAsync(fileStream, fileName, contentType, requestingUserId, document.ProjectId);
        }
        catch (Exception ex)
        {
            return UploadResult.Failed($"Failed to save file: {ex.Message}");
        }

        var oldFilePath = document.FilePath;

        try
        {
            document.FileName = fileName;
            document.FilePath = newFilePath;
            document.FileType = contentType;
            document.FileSizeBytes = fileStream.Length;
            document.UpdatedDate = DateTime.UtcNow;

            _context.DocumentActivityLogs.Add(new DocumentActivityLog
            {
                DocumentId = documentId,
                DocumentTitleSnapshot = document.Title,
                UserId = requestingUserId,
                Action = DocumentActivityType.FileReplace,
                Timestamp = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();
        }
        catch (Exception)
        {
            // Roll back the newly-written file; the old file and DB row are untouched.
            await _fileStorageService.DeleteAsync(newFilePath);
            return UploadResult.Failed("Failed to update document metadata.");
        }

        // Old file is deleted only now that the new file and DB row are both confirmed saved
        // (no version history retained, FR-019).
        await _fileStorageService.DeleteAsync(oldFilePath);

        return UploadResult.Ok(document.DocumentId);
    }

    public async Task<bool> DeleteAsync(int requestingUserId, int documentId)
    {
        var document = await _context.Documents.FindAsync(documentId);
        if (document == null)
        {
            return false;
        }

        // Owner, the project's Project Manager, or an Administrator — explicitly NOT Team Leads (FR-024).
        var canDelete = document.UploadedByUserId == requestingUserId
            || await IsAdministratorAsync(requestingUserId)
            || (document.ProjectId.HasValue && await IsProjectManagerOfAsync(requestingUserId, document.ProjectId.Value));

        if (!canDelete)
        {
            return false;
        }

        var filePath = document.FilePath;
        var title = document.Title;

        // DocumentShare rows cascade-delete and DocumentActivityLog.DocumentId is set-null at the database
        // level (configured in ApplicationDbContext.OnModelCreating) — no manual cleanup needed here.
        _context.Documents.Remove(document);

        _context.DocumentActivityLogs.Add(new DocumentActivityLog
        {
            DocumentId = null,
            DocumentTitleSnapshot = title,
            UserId = requestingUserId,
            Action = DocumentActivityType.Delete,
            Timestamp = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();
        await _fileStorageService.DeleteAsync(filePath);

        return true;
    }

    public async Task<bool> ShareAsync(int requestingUserId, int documentId, ShareTarget target)
    {
        var document = await _context.Documents.FindAsync(documentId);
        if (document == null || document.UploadedByUserId != requestingUserId)
        {
            return false;
        }

        var hasUserTarget = target.UserId.HasValue;
        var hasDepartmentTarget = !string.IsNullOrWhiteSpace(target.Department);
        if (hasUserTarget == hasDepartmentTarget)
        {
            // Exactly one of UserId/Department must be set (data-model.md validation rule).
            return false;
        }

        if (hasUserTarget && !await _context.Users.AnyAsync(u => u.UserId == target.UserId!.Value))
        {
            return false;
        }

        var sharer = await _context.Users.FindAsync(requestingUserId);

        var share = new DocumentShare
        {
            DocumentId = documentId,
            SharedByUserId = requestingUserId,
            SharedWithUserId = target.UserId,
            SharedWithDepartment = hasDepartmentTarget ? target.Department!.Trim() : null,
            SharedDate = DateTime.UtcNow
        };
        _context.DocumentShares.Add(share);

        _context.DocumentActivityLogs.Add(new DocumentActivityLog
        {
            DocumentId = documentId,
            DocumentTitleSnapshot = document.Title,
            UserId = requestingUserId,
            Action = DocumentActivityType.Share,
            Timestamp = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();

        // A department share notifies every current member of that department (FR-022), excluding the
        // sharer themselves; a direct share notifies only that one recipient.
        var recipientUserIds = hasUserTarget
            ? new List<int> { target.UserId!.Value }
            : await _context.Users
                .Where(u => u.Department == share.SharedWithDepartment && u.UserId != requestingUserId)
                .Select(u => u.UserId)
                .ToListAsync();

        foreach (var recipientId in recipientUserIds)
        {
            await _notificationService.CreateNotificationAsync(new Notification
            {
                UserId = recipientId,
                Title = "Document shared with you",
                Message = $"{sharer?.DisplayName ?? "A colleague"} shared \"{document.Title}\" with you.",
                Type = NotificationType.DocumentShared,
                Priority = NotificationPriority.Informational
            });
        }

        share.NotificationSent = true;
        await _context.SaveChangesAsync();

        return true;
    }

    public Task<IReadOnlyList<DocumentActivitySummary>> GetActivityLogAsync(int requestingUserId, DateRange range)
        => throw new NotImplementedException();

    public Task<DocumentActivityReport> GenerateActivityReportAsync(int requestingUserId)
        => throw new NotImplementedException();
}
