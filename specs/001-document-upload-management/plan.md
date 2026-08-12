# Implementation Plan: Document Upload and Management

**Branch**: `001-document-upload-management` | **Date**: 2026-08-11 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/001-document-upload-management/spec.md`

## Summary

Add centralized document upload, organization, search, retrieval, editing, deletion, sharing, and task/dashboard integration to ContosoDashboard, so employees stop scattering work files across local drives, email, and shared drives. Technical approach: a new `Document`/`DocumentShare` data model (integer keys, text category, 255-char MIME field) persisted via the existing `ApplicationDbContext`; an `IFileStorageService` abstraction with a `LocalFileStorageService` implementation that writes files to `AppData/uploads/{userId}/{projectId|"personal"}/{guid}.{ext}` outside `wwwroot`; a `DocumentService` that enforces the upload sequence (generate path → save file → save DB row) and all authorization/IDOR checks; a new authenticated `DocumentsController` endpoint to stream downloads/previews with per-request authorization (files outside `wwwroot` cannot be served by static file middleware); and new/extended Blazor Server pages (`Documents.razor`, project/task integration, dashboard widget) following the app's existing flat-`Pages/`, card-based UI conventions.

## Technical Context

**Language/Version**: C# 12 / .NET 8.0 (ASP.NET Core 8.0, Blazor Server)
**Primary Dependencies**: EF Core 8 (`Microsoft.EntityFrameworkCore.SqlServer`, `.Tools`) for data access; ASP.NET Core Cookie Authentication (existing mock login) for identity; built-in `System.IO` for local file operations (no new file-storage NuGet package needed). No Azure SDK packages are introduced (constitution: offline-first). `Microsoft.Identity.Web`/`.UI` packages already referenced in the `.csproj` are unused leftovers from OIDC scaffolding — not used by this feature or by the app's actual cookie-based auth.
**Storage**: SQL Server LocalDB via `ApplicationDbContext` (existing) for metadata; local filesystem under `AppData/uploads/` (new, outside `wwwroot`) for file bytes.
**Testing**: No test project exists yet in the repo. Constitution Principle III (SDD, non-negotiable) requires automated tests to accompany new features — this plan introduces a new `ContosoDashboard.Tests` xUnit project (industry-standard choice for ASP.NET Core/EF Core, integrates with EF Core's in-memory/SQLite providers for service-layer tests). See `research.md` for the decision record.
**Target Platform**: Self-hosted ASP.NET Core 8 (Kestrel) on the training machine (Windows), SQL Server LocalDB, browser-based Blazor Server client. Fully offline-capable — no external network calls.
**Project Type**: Single project (existing `ContosoDashboard` Blazor Server monolith; no separate frontend/backend split). This feature adds one new test project (`ContosoDashboard.Tests`) alongside it.
**Performance Goals**: Uploads of files up to 25 MB complete within 30 seconds (SC-002); document list/search pages load within 2 seconds for up to 500 documents (SC-003); previews begin rendering within 3 seconds (SC-004); search results returned within 2 seconds (FR-015).
**Constraints**: Fully offline (no cloud dependency); files stored outside `wwwroot`; GUID-based filenames only (no user-supplied names in paths); 25 MB max file size; supported types PDF/Word/Excel/PowerPoint/text/JPEG/PNG only, enforced via extension whitelist; `FileType` column must accommodate 255 characters; `DocumentId`/all keys are `int`; `Category` stored as free text, not an enum; no version history retained on file replace; no soft-delete/trash.
**Scale/Scope**: Single-organization internal tool; document lists must stay performant to 500 documents per view (SC-003); existing small/medium user base (matches current `User`/`Project`/`TaskItem` seed scale).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Requirement | How this feature satisfies it |
|---|---|---|
| I. Offline-First with Cloud Migration Path | No cloud dependency; infra behind interfaces | `IFileStorageService` interface (`UploadAsync`/`DeleteAsync`/`DownloadAsync`/`GetUrlAsync`) with `LocalFileStorageService` as the only implementation; registered via DI so a future `AzureBlobStorageService` is a config/DI swap with zero business-logic changes. No Azure SDK referenced. |
| II. Service-Level Security & IDOR Prevention | All access goes through authorization checks tied to the requesting user | `DocumentService` methods all take the requesting user's identity and validate ownership/project-membership/role before returning or mutating data, matching the existing `TaskService`/`ProjectService` pattern. Downloads/previews go through an authenticated `DocumentsController` action (not static files), which re-validates access on every request before streaming bytes. |
| III. Spec-Driven Development (NON-NEGOTIABLE) | Spec+plan approved before code; automated tests accompany all new features | This plan follows spec.md → plan.md → tasks.md. A new `ContosoDashboard.Tests` xUnit project is introduced specifically so `DocumentService` authorization/validation logic and upload-sequence behavior are covered by automated tests (see research.md). |
| IV. Async & Performance-Focused Data Access | `async/await` throughout; eager loading; indexes on filtered fields | All `DocumentService`/`IFileStorageService` methods are `async`. EF queries for document lists use `.Include()` for `Uploader`/`Project`. New indexes on `Document.UploadedByUserId`, `Document.ProjectId`, `Document.Category`, `Document.UploadDate` support the sort/filter/search requirements (FR-011/012/015) without full scans. |
| V. Safe File Storage & Upload Sequence | Files outside `wwwroot`; GUID filenames; generate-path → save-file → save-DB-row order | `LocalFileStorageService` writes under `AppData/uploads/{userId}/{projectId|"personal"}/{guid}.{ext}`, never under `wwwroot`. `DocumentService.UploadAsync` follows the mandated 3-step sequence exactly, so a failed disk write never produces an orphaned DB row and a failed DB write triggers cleanup of the just-written file. |

**Result**: PASS. No principle requires an exception; no entries needed in Complexity Tracking.

## Project Structure

### Documentation (this feature)

```text
specs/001-document-upload-management/
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output (/speckit.plan command)
├── data-model.md        # Phase 1 output (/speckit.plan command)
├── quickstart.md        # Phase 1 output (/speckit.plan command)
├── contracts/
│   └── document-service-contract.md   # Phase 1 output (/speckit.plan command)
├── checklists/
│   └── requirements.md
└── tasks.md              # Phase 2 output (/speckit.tasks command - NOT created by /speckit.plan)
```

### Source Code (repository root)

This is a **single-project** Blazor Server application (Option 1). There is no `frontend`/`backend` split and no `.sln` — everything lives under `ContosoDashboard/`. The tree below shows real, existing paths plus every new/modified file this feature introduces.

```text
ContosoDashboard/                         # existing single project (Microsoft.NET.Sdk.Web, net8.0)
├── Models/
│   ├── Document.cs                       # NEW — Document entity (int key, text Category, 255-char FileType)
│   ├── DocumentShare.cs                  # NEW — share grants (user or department recipient)
│   ├── DocumentActivityLog.cs            # NEW — audit log entries (FR-030)
│   ├── Notification.cs                   # MODIFIED — extend NotificationType with Document* values
│   ├── Project.cs, TaskItem.cs, User.cs  # existing — Document adds optional FKs to Project/TaskItem/User
│   └── ...
├── Data/
│   └── ApplicationDbContext.cs           # MODIFIED — add DbSets, relationships, indexes, seed data
├── Services/
│   ├── IFileStorageService.cs            # NEW — storage abstraction (interface + LocalFileStorageService)
│   ├── LocalFileStorageService.cs        # NEW — local filesystem implementation
│   ├── IDocumentService.cs               # NEW — interface + DocumentService implementation (same-file pattern)
│   ├── DocumentService.cs                # NEW — upload sequence, authorization, CRUD, search, share, audit log
│   ├── NotificationService.cs            # MODIFIED — emit notifications for share + new project document events
│   └── IUserService.cs, ITaskService.cs, IProjectService.cs, IDashboardService.cs   # existing, unchanged
├── Controllers/                          # NEW folder — first Controller in the repo
│   └── DocumentsController.cs            # NEW — authenticated download/preview streaming endpoint
├── Pages/
│   ├── Documents.razor                   # NEW — "My Documents" list/sort/filter/search + upload entry point
│   ├── SharedWithMe.razor                # NEW — documents shared with the current user
│   ├── ProjectDetails.razor              # MODIFIED — add project documents section + upload
│   ├── Tasks.razor                       # MODIFIED — task detail view/modal gains document attach/upload
│   ├── Index.razor                       # MODIFIED — "Recent Documents" widget + document count summary card
│   └── Notifications.razor               # existing, unchanged (renders new Document* notification types)
├── Shared/
│   └── DocumentUploadModal.razor         # NEW — reusable upload component (title/category/tags/file input)
├── Program.cs                            # MODIFIED — register IFileStorageService/IDocumentService, MapControllers(), DocumentStorage config binding
├── appsettings.json                      # MODIFIED — new `DocumentStorage` section (BasePath, MaxFileSizeBytes, AllowedExtensions)
└── wwwroot/                              # unchanged — uploaded files are NEVER placed here

AppData/uploads/{userId}/{projectId|"personal"}/{guid}.{ext}   # NEW — runtime file storage root, outside wwwroot, git-ignored

ContosoDashboard.Tests/                   # NEW test project (xUnit)
├── ContosoDashboard.Tests.csproj
├── Services/
│   ├── DocumentServiceTests.cs           # upload sequence, authorization/IDOR, validation, sharing
│   └── LocalFileStorageServiceTests.cs   # save/download/delete against a temp directory
└── ...
```

**Structure Decision**: Single-project structure (Option 1), matching the existing app exactly — new code is added as additional files inside the existing `Models/`, `Data/`, `Services/`, `Pages/`, `Shared/` folders, plus one new `Controllers/` folder (required because authorized file streaming cannot be done through `UseStaticFiles()`) and one new `ContosoDashboard.Tests` project (required to satisfy the constitution's non-negotiable testing principle, since no test project currently exists).

## Complexity Tracking

*No entries — Constitution Check passed with no violations requiring justification.*
