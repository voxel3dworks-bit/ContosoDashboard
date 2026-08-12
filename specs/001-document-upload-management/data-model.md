# Phase 1 Data Model: Document Upload and Management

Conventions match the existing `ApplicationDbContext` model: integer `[Key]` primary keys named `{Entity}Id`, `[ForeignKey]` navigation properties, `DateTime` UTC timestamps defaulted with `DateTime.UtcNow`, indexes added in `OnModelCreating`. All new entities live in `ContosoDashboard/Models/`.

## Document

Represents an uploaded file and its metadata (spec.md Key Entities → Document; FR-001–FR-021).

| Field | Type | Constraints | Notes |
|---|---|---|---|
| `DocumentId` | `int` | PK, identity | Matches existing integer-key convention (constitution constraint). |
| `Title` | `string` | Required, `MaxLength(255)` | FR-007. |
| `Description` | `string?` | Optional, `MaxLength(2000)` | FR-007. |
| `Category` | `string` | Required, `MaxLength(50)` | Free text, service-layer-validated against the fixed list (Project Documents, Team Resources, Personal Files, Reports, Presentations, Other) — not an enum, per constitution constraint and research.md §7. |
| `Tags` | `string?` | Optional, `MaxLength(500)` | Stored as a comma-delimited string (no separate tag entity — spec has no requirement for tag reuse/autocomplete across documents). |
| `FileName` | `string` | Required, `MaxLength(255)` | Original filename as selected by the user, stored for display only — never used to build a filesystem path (constitution Principle V). |
| `FilePath` | `string` | Required, `MaxLength(500)` | GUID-based relative path: `{userId}/{projectId or "personal"}/{guid}.{extension}`. Generated before the file is written to disk (research.md §6). |
| `FileType` | `string` | Required, `MaxLength(255)` | MIME type; 255 chars to accommodate long Office Open XML content-type strings (constitution/stakeholder constraint). |
| `FileSizeBytes` | `long` | Required, range 1–26,214,400 (25 MB) | Enforced at upload validation (FR-003). |
| `UploadedByUserId` | `int` | Required, FK → `User.UserId` | Captured automatically, not user-editable (FR-008). |
| `ProjectId` | `int?` | Optional, FK → `Project.ProjectId` | Null = personal document. Set automatically when uploaded via a task (FR-026). |
| `TaskId` | `int?` | Optional, FK → `TaskItem.TaskId` | Set when uploaded/attached from a task detail page (FR-025/026). |
| `UploadDate` | `DateTime` | Required, default `DateTime.UtcNow` | FR-008. |
| `UpdatedDate` | `DateTime` | Required, default `DateTime.UtcNow` | Bumped on metadata edit or file replace (FR-018/019). |
| `IsDeleted` | — | *(not present)* | Deletion is permanent per FR-020 and spec.md Out of Scope ("no soft delete") — rows are hard-deleted, not flagged. |

**Navigation properties**: `Uploader` (`User`, via `UploadedByUserId`), `Project` (`Project?`, via `ProjectId`), `Task` (`TaskItem?`, via `TaskId`), `Shares` (`ICollection<DocumentShare>`), `ActivityLogEntries` (`ICollection<DocumentActivityLog>`).

**Validation rules** (enforced in `DocumentService`, not just data annotations):
- Extension (derived from `FileName`) MUST be in the whitelist {`.pdf`, `.doc`, `.docx`, `.xls`, `.xlsx`, `.ppt`, `.pptx`, `.txt`, `.jpg`, `.jpeg`, `.png`} — FR-002.
- `FileSizeBytes` MUST NOT exceed 25 MB — FR-003.
- `Category` MUST be one of the six fixed values — FR-007.
- If `ProjectId` is set, the uploading user MUST be a member of that project (or its Project Manager) — IDOR prevention (constitution Principle II).
- If `TaskId` is set, `ProjectId` MUST equal the task's `ProjectId` — FR-026 ("automatically associated with the task's project").

**Indexes** (`OnModelCreating`, supporting FR-011/012/015 sort/filter/search at 500-document scale — constitution Principle IV):
- `HasIndex(d => d.UploadedByUserId)`
- `HasIndex(d => d.ProjectId)`
- `HasIndex(d => d.Category)`
- `HasIndex(d => d.UploadDate)`

## DocumentShare

Represents a grant of access to a document for a specific recipient (spec.md Key Entities → Document Share; FR-022/023).

| Field | Type | Constraints | Notes |
|---|---|---|---|
| `DocumentShareId` | `int` | PK, identity | |
| `DocumentId` | `int` | Required, FK → `Document.DocumentId` | |
| `SharedByUserId` | `int` | Required, FK → `User.UserId` | Must be the document owner (enforced in service layer). |
| `SharedWithUserId` | `int?` | Optional, FK → `User.UserId` | Set for an individual-user share. Exactly one of `SharedWithUserId`/`SharedWithDepartment` is set. |
| `SharedWithDepartment` | `string?` | Optional, `MaxLength(100)` | Set for a department/"team" share (research.md §8); matches `User.Department`'s existing length. |
| `SharedDate` | `DateTime` | Required, default `DateTime.UtcNow` | |
| `NotificationSent` | `bool` | Required, default `false` | Set `true` once the in-app notification has been created (FR-023). |

**Navigation properties**: `Document` (`Document`), `SharedByUser` (`User`), `SharedWithUser` (`User?`).

**Validation rules**:
- Exactly one of `SharedWithUserId` / `SharedWithDepartment` must be non-null (a share is either to a person or to a department, never both/neither).
- Department-share visibility is evaluated dynamically against current `User.Department` values at read time (research.md §8) — no per-user rows are materialized for a department share.

**Indexes**:
- `HasIndex(s => s.DocumentId)`
- `HasIndex(s => s.SharedWithUserId)`

## DocumentActivityLog

Represents one recorded action against a document, for audit reporting (spec.md Key Entities → Document Activity Log Entry; FR-030/031).

| Field | Type | Constraints | Notes |
|---|---|---|---|
| `DocumentActivityLogId` | `int` | PK, identity | |
| `DocumentId` | `int` | Required, FK → `Document.DocumentId` | Nullable-on-delete not needed — see cascade note below. |
| `UserId` | `int` | Required, FK → `User.UserId` | The acting user. |
| `Action` | `DocumentActivityType` (enum) | Required | `Upload`, `Download`, `Delete`, `Share`, `MetadataEdit`, `FileReplace`. (An enum is appropriate here — unlike `Category`, this field has no "store as text" constraint in the spec/constitution.) |
| `Timestamp` | `DateTime` | Required, default `DateTime.UtcNow` | FR-030. |

**Cascade note**: Because deletion is permanent (FR-020) but activity logs must remain available for audit/reporting (FR-031) even after the underlying document is gone, `DocumentId` on `DocumentActivityLog` uses `OnDelete(DeleteBehavior.SetNull)` semantics conceptually — implemented by making the FK column nullable at the database level (`int?`) while keeping it required at creation time, so a deleted document's history entries survive with `DocumentId = null` plus a denormalized `DocumentTitle` snapshot captured at log-write time (added field: `DocumentTitleSnapshot string`, `MaxLength(255)`).

**Indexes**:
- `HasIndex(l => l.DocumentId)`
- `HasIndex(l => l.UserId)`
- `HasIndex(l => l.Timestamp)`

## Modifications to existing entities

- **`Notification.NotificationType`** (`Models/Notification.cs`): extend the enum with `DocumentShared` and `DocumentAddedToProject` values, used by `NotificationService` for FR-023/FR-029. Existing values are untouched (additive change only).
- **`User`**: no schema change. `Department` (existing nullable `string`) is read, not written, by document sharing logic.
- **`Project` / `TaskItem`**: no schema change. Each gains an implicit inverse collection `ICollection<Document> Documents` for `.Include()`-based eager loading from project/task detail views (constitution Principle IV).

## Relationships summary

```
User (1) ──uploads──> (*) Document
User (1) ──shares──> (*) DocumentShare ──targets──> (0..1) User
Project (1) ──has──> (*) Document
TaskItem (1) ──has──> (*) Document
Document (1) ──has──> (*) DocumentShare
Document (1) ──has──> (*) DocumentActivityLog
```

## Storage-layer counterpart (not a DB entity)

`IFileStorageService` (in `Services/`) is the abstraction for the actual file bytes referenced by `Document.FilePath`:

```csharp
public interface IFileStorageService
{
    Task<string> UploadAsync(Stream fileStream, string fileName, string contentType, int userId, int? projectId);
    Task DeleteAsync(string filePath);
    Task<Stream> DownloadAsync(string filePath);
    Task<string> GetUrlAsync(string filePath, TimeSpan expiration);
}
```

`LocalFileStorageService` is the only implementation registered today (`AppData/uploads/...`, `System.IO`); `GetUrlAsync` returns an authenticated `DocumentsController` route rather than a signed URL, since local files have no public URL — the signature is kept identical to what a future `AzureBlobStorageService` (returning a SAS URL) would need, per the constitution's migration-path requirement.
