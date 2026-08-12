# Contract: IDocumentService

Internal service contract consumed by Blazor pages/components (`Documents.razor`, `ProjectDetails.razor`, `Tasks.razor`, `Index.razor`, `DocumentUploadModal.razor`). Follows the existing `IProjectService`/`ITaskService` convention: every method takes the requesting user's ID and performs authorization internally, returning `null`/`false`/an empty result rather than throwing for permission failures (throwing is reserved for validation failures the caller is expected to have already prevented in the UI, e.g. malformed input).

```csharp
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
    Task<IReadOnlyList<DocumentSummary>> GetRecentAsync(int requestingUserId, int count); // dashboard widget, FR-027
    Task<int> GetAccessibleDocumentCountAsync(int requestingUserId); // dashboard summary card, FR-028

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
```

## Preconditions / postconditions per method

| Method | Preconditions | Postconditions | Authorization rule |
|---|---|---|---|
| `UploadAsync` | File extension in whitelist; size ≤ 25 MB; `Category` in fixed list; malware scan passes (`IMalwareScanner`) | New `Document` row + file on disk exist together, or neither does (FR-006/FR-009, constitution Principle V) | If `request.ProjectId` set, caller must be a member/PM of that project. |
| `GetMyDocumentsAsync` | — | Returns only documents where `UploadedByUserId == requestingUserId` | Implicit — scoped by caller's own ID. |
| `GetProjectDocumentsAsync` | — | Returns all documents for the project | Caller must be a `ProjectMember` of `projectId` (or its Project Manager / an Administrator). |
| `SearchAsync` | — | Results exclude any document the caller can't access (FR-015) | Same visibility rule as `GetMyDocumentsAsync` ∪ `GetProjectDocumentsAsync` ∪ `GetSharedWithMeAsync`, applied as a filter before returning results — never after. |
| `AuthorizeAccessAsync` | — | Returns whether the caller may download/preview, used by `DocumentsController` before streaming bytes | Owner, project member/PM of the document's project, share recipient (direct or department match), or Administrator. **Note**: intentionally does NOT include "Team Lead of the uploader" — FR-024 grants Team Leads metadata view/edit rights only, not download/preview rights. |
| `GetByIdAsync` | `documentId` exists | Returns full document detail (used as the read path before editing) | Caller must be the document owner **or a Team Lead whose team includes the document's uploader** (FR-024). |
| `UpdateMetadataAsync` | `documentId` exists | Metadata updated, `UpdatedDate` bumped, activity log entry written | Caller must be the document owner **or a Team Lead whose team includes the document's uploader** (FR-024). |
| `ReplaceFileAsync` | New file passes same validation as `UploadAsync` | Old file deleted from disk only after new file is confirmed saved and DB row updated (no version history retained, FR-019) | Caller must be the document owner. |
| `DeleteAsync` | `documentId` exists | Document row and disk file both removed; `DocumentActivityLog` rows survive with `DocumentId = null` + title snapshot (data-model.md) | Caller must be the document owner, the Project Manager of the document's project, or an Administrator (FR-020/FR-021). Team Leads are explicitly **not** authorized to delete (FR-024). |
| `ShareAsync` | Target is a valid user ID or a non-empty department string | `DocumentShare` row created, notification queued (FR-023) | Caller must be the document owner. |
| `GenerateActivityReportAsync` | — | Aggregated report (most-uploaded types, most active uploaders, access patterns) | Caller must be an Administrator (FR-031). |

## Errors

All methods use the existing app convention: authorization failures return `null`/`false`/empty collections (never throw), so callers render a generic "not found or no access" state without leaking whether a resource exists to an unauthorized caller (IDOR prevention, constitution Principle II). Validation failures (bad category, oversized file, unsupported extension) are returned via a typed `UploadResult { Success, ErrorMessage }` rather than exceptions, so `DocumentUploadModal.razor` can display FR-005's required success/error messaging directly.
