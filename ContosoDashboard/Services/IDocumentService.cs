namespace ContosoDashboard.Services;

public interface IDocumentService
{
    // Upload — FR-001..FR-009. Follows generate-path -> save-file -> save-DB-row sequence internally.
    Task<UploadResult> UploadAsync(int requestingUserId, DocumentUploadRequest request);

    // Browsing — FR-010..FR-015
    Task<PagedResult<DocumentSummary>> GetMyDocumentsAsync(int requestingUserId, DocumentQuery query);
    Task<IReadOnlyList<DocumentSummary>> GetProjectDocumentsAsync(int requestingUserId, int projectId);
    Task<IReadOnlyList<DocumentSummary>> GetTaskDocumentsAsync(int requestingUserId, int taskId);
    Task<PagedResult<DocumentSummary>> SearchAsync(int requestingUserId, string searchTerm, DocumentQuery query);
    Task<IReadOnlyList<DocumentSummary>> GetSharedWithMeAsync(int requestingUserId);
    Task<IReadOnlyList<DocumentSummary>> GetRecentAsync(int requestingUserId, int count);
    Task<int> GetAccessibleDocumentCountAsync(int requestingUserId);

    // Access — FR-016/FR-017 (metadata + authorization check; byte streaming itself happens in DocumentsController)
    Task<DocumentAccessCheck> AuthorizeAccessAsync(int requestingUserId, int documentId);

    // Management — FR-018..FR-021, FR-024
    Task<DocumentDetail?> GetByIdAsync(int requestingUserId, int documentId);
    Task<bool> UpdateMetadataAsync(int requestingUserId, int documentId, DocumentMetadataUpdate update);
    Task<UploadResult> ReplaceFileAsync(int requestingUserId, int documentId, Stream fileStream, string fileName, string contentType);
    Task<bool> DeleteAsync(int requestingUserId, int documentId);

    // Sharing — FR-022/FR-023
    Task<bool> ShareAsync(int requestingUserId, int documentId, ShareTarget target);

    // Audit — FR-030/FR-031 (Administrator-only; enforced inside the implementation)
    Task<IReadOnlyList<DocumentActivitySummary>> GetActivityLogAsync(int requestingUserId, DateRange range);
    Task<DocumentActivityReport> GenerateActivityReportAsync(int requestingUserId);
}

public class DocumentUploadRequest
{
    public Stream FileStream { get; set; } = Stream.Null;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? Tags { get; set; }
    public int? ProjectId { get; set; }
    public int? TaskId { get; set; }
}

public class UploadResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int? DocumentId { get; set; }

    public static UploadResult Ok(int documentId) => new() { Success = true, DocumentId = documentId };
    public static UploadResult Failed(string errorMessage) => new() { Success = false, ErrorMessage = errorMessage };
}

public class DocumentSummary
{
    public int DocumentId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateTime UploadDate { get; set; }
    public long FileSizeBytes { get; set; }
    public string FileType { get; set; } = string.Empty;
    public int? ProjectId { get; set; }
    public string? ProjectName { get; set; }
    public int UploadedByUserId { get; set; }
    public string UploaderName { get; set; } = string.Empty;
}

public class DocumentDetail : DocumentSummary
{
    public string? Description { get; set; }
    public string? Tags { get; set; }
    public string FileName { get; set; } = string.Empty;
    public int? TaskId { get; set; }
    public DateTime UpdatedDate { get; set; }
}

public enum DocumentSortField
{
    Title,
    UploadDate,
    Category,
    FileSizeBytes
}

public class DocumentQuery
{
    public DocumentSortField SortBy { get; set; } = DocumentSortField.UploadDate;
    public bool SortDescending { get; set; } = true;
    public string? CategoryFilter { get; set; }
    public int? ProjectIdFilter { get; set; }
    public DateTime? UploadedFrom { get; set; }
    public DateTime? UploadedTo { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class DocumentAccessCheck
{
    public bool IsAuthorized { get; set; }
    public DocumentDetail? Document { get; set; }

    public static DocumentAccessCheck Denied() => new() { IsAuthorized = false };
    public static DocumentAccessCheck Granted(DocumentDetail document) => new() { IsAuthorized = true, Document = document };
}

public class DocumentMetadataUpdate
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? Tags { get; set; }
}

public class ShareTarget
{
    public int? UserId { get; set; }
    public string? Department { get; set; }
}

public class DocumentActivitySummary
{
    public int DocumentActivityLogId { get; set; }
    public int? DocumentId { get; set; }
    public string DocumentTitle { get; set; } = string.Empty;
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

public class DateRange
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
}

public class DocumentActivityReport
{
    public IReadOnlyDictionary<string, int> MostUploadedFileTypes { get; set; } = new Dictionary<string, int>();
    public IReadOnlyList<(string UserName, int UploadCount)> MostActiveUploaders { get; set; } = Array.Empty<(string, int)>();
    public IReadOnlyDictionary<string, int> ActionCounts { get; set; } = new Dictionary<string, int>();
}
