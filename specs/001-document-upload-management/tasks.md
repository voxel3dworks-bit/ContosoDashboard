---

description: "Task list template for feature implementation"
---

# Tasks: Document Upload and Management

**Input**: Design documents from `/specs/001-document-upload-management/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: Included. Constitution Principle III ("Spec-Driven Development", NON-NEGOTIABLE) requires automated unit/integration tests to accompany every new feature, and no test project exists yet in the repo — this feature introduces `ContosoDashboard.Tests` (xUnit, see research.md §1) and includes unit test tasks per user story.

**Organization**: Tasks are grouped by user story (spec.md, priorities P1–P6) to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on an incomplete task)
- **[Story]**: Which user story this task belongs to (US1–US6)
- File paths are exact and relative to the repo root (`C:\entrenamiento\ContosoDashboard\`)

## Path Conventions

Single project (Option 1, per plan.md) — no `frontend`/`backend` split:
- App code: `ContosoDashboard/{Models,Data,Services,Controllers,Pages,Shared}/`
- Tests: `ContosoDashboard.Tests/` (new project this feature introduces)
- Runtime file storage: `AppData/uploads/` (repo root, outside `wwwroot`, git-ignored)

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project/tooling initialization needed before any model or story work begins.

- [X] T001 Create the `ContosoDashboard.Tests` xUnit project (`dotnet new xunit -o ContosoDashboard.Tests`), add a project reference to `ContosoDashboard/ContosoDashboard.csproj`, and add the `Microsoft.EntityFrameworkCore.InMemory` package (research.md §1) — files: `ContosoDashboard.Tests/ContosoDashboard.Tests.csproj`
- [X] T002 [P] Add a `DocumentStorage` configuration section (`BasePath`, `MaxFileSizeBytes`, `AllowedExtensions`) to `ContosoDashboard/appsettings.json`
- [X] T003 [P] Add `AppData/uploads/` to `.gitignore` at the repo root (`.gitignore`) so uploaded files are never committed

**Checkpoint**: Test project builds (`dotnet test` runs with zero tests), config section present.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Data model, storage abstraction, and service scaffolding that every user story depends on.

**⚠️ CRITICAL**: No user story task may start until this phase is complete.

- [X] T004 [P] Create the `Document` entity per data-model.md in `ContosoDashboard/Models/Document.cs` (int `DocumentId` PK, `Title`, `Description`, `Category` as `string`, `Tags`, `FileName`, `FilePath`, `FileType` `MaxLength(255)`, `FileSizeBytes`, `UploadedByUserId`, `ProjectId`, `TaskId`, `UploadDate`, `UpdatedDate`, navigation properties)
- [X] T005 [P] Create the `DocumentShare` entity per data-model.md in `ContosoDashboard/Models/DocumentShare.cs` (`DocumentShareId` PK, `DocumentId`, `SharedByUserId`, `SharedWithUserId`, `SharedWithDepartment`, `SharedDate`, `NotificationSent`)
- [X] T006 [P] Create the `DocumentActivityLog` entity and `DocumentActivityType` enum per data-model.md in `ContosoDashboard/Models/DocumentActivityLog.cs` (nullable `DocumentId`, `DocumentTitleSnapshot`, `UserId`, `Action`, `Timestamp`)
- [X] T007 [P] Extend `NotificationType` in `ContosoDashboard/Models/Notification.cs` with `DocumentShared` and `DocumentAddedToProject` values (additive only)
- [X] T008 Register `Document`, `DocumentShare`, `DocumentActivityLog` as `DbSet`s and configure relationships/indexes (`UploadedByUserId`, `ProjectId`, `Category`, `UploadDate` on `Document`; `DocumentId`, `SharedWithUserId` on `DocumentShare`; `DocumentId`, `UserId`, `Timestamp` on `DocumentActivityLog`) in `ContosoDashboard/Data/ApplicationDbContext.cs` (depends on T004, T005, T006, T007)
- [X] T009 [P] Create the `IFileStorageService` interface (`UploadAsync`, `DeleteAsync`, `DownloadAsync`, `GetUrlAsync`) per data-model.md in `ContosoDashboard/Services/IFileStorageService.cs`
- [X] T010 Implement `LocalFileStorageService : IFileStorageService` using `System.IO`, writing to `AppData/uploads/{userId}/{projectId or "personal"}/{guid}.{extension}`, never using caller-supplied filenames in paths (constitution Principle V) in `ContosoDashboard/Services/LocalFileStorageService.cs` (depends on T009)
- [X] T011 [P] Create the `IMalwareScanner` interface and a training-environment implementation performing extension/magic-byte validation (research.md §2) in `ContosoDashboard/Services/IMalwareScanner.cs`
- [X] T012 [P] Create the `IDocumentService` interface and its DTOs (`DocumentUploadRequest`, `DocumentSummary`, `DocumentDetail`, `DocumentQuery`, `PagedResult<T>`, `UploadResult`, `DocumentAccessCheck`, `DocumentMetadataUpdate`, `ShareTarget`, `DocumentActivitySummary`, `DocumentActivityReport`, `DateRange`) per contracts/document-service-contract.md in `ContosoDashboard/Services/IDocumentService.cs`
- [X] T013 Create the `DocumentService : IDocumentService` skeleton (constructor injecting `ApplicationDbContext`, `IFileStorageService`, `IMalwareScanner`, `INotificationService`; all interface methods stubbed with `NotImplementedException`) in `ContosoDashboard/Services/DocumentService.cs` (depends on T008, T009, T011, T012)
- [X] T014 Register `IFileStorageService`, `IMalwareScanner`, `IDocumentService` in DI, bind the `DocumentStorage` config section, and add `AddControllers()`/`MapControllers()` to the pipeline in `ContosoDashboard/Program.cs` (depends on T010, T011, T013, T002)
- [X] T015 [P] Create a shared xUnit test fixture providing an in-memory `ApplicationDbContext` and seeded `User`/`Project`/`TaskItem` rows for service tests in `ContosoDashboard.Tests/TestHelpers/DocumentTestFixture.cs` (depends on T001, T008)

**Checkpoint**: Solution builds; DI resolves `IDocumentService`/`IFileStorageService`; test fixture available. User story implementation can now begin.

---

## Phase 3: User Story 1 - Upload a Document (Priority: P1) 🎯 MVP

**Goal**: An employee can select a file, provide required metadata, and upload it, with progress feedback and a clear success/error result, ending up as a retrievable document with auto-captured system metadata.

**Independent Test**: Follow quickstart.md scenario 1 — upload a valid file (succeeds), a file over 25 MB (rejected with size error), and an unsupported file type (rejected with type error) — verify no orphaned files/DB rows on rejection.

### Tests for User Story 1

- [X] T016 [P] [US1] Unit tests for `DocumentService.UploadAsync`: extension whitelist rejection, size-limit rejection, required-field validation, malware-scan rejection, and correct generate-path → save-file → save-DB-row ordering (including that a simulated disk-write failure leaves no DB row) in `ContosoDashboard.Tests/Services/DocumentServiceTests.cs`
- [X] T017 [P] [US1] Unit tests for `LocalFileStorageService`: `UploadAsync` produces a GUID-based path matching the `{userId}/{projectId|"personal"}/{guid}.{ext}` pattern and never incorporates the caller-supplied filename, `DownloadAsync` round-trips written bytes, against a temp directory in `ContosoDashboard.Tests/Services/LocalFileStorageServiceTests.cs`

### Implementation for User Story 1

- [X] T018 [US1] Implement `DocumentService.UploadAsync` (validate extension/size/category, run `IMalwareScanner`, authorize project/task association, call `IFileStorageService.UploadAsync`, create the `Document` row, write a `DocumentActivityLog` "Upload" entry) in `ContosoDashboard/Services/DocumentService.cs` (depends on T016, T017)
- [X] T019 [US1] Add the project/task-membership authorization helper used by `UploadAsync` (caller must be a project member/PM to upload to a project; task's `ProjectId` must match the supplied `ProjectId`) in `ContosoDashboard/Services/DocumentService.cs`
- [X] T020 [US1] Create `DocumentUploadModal.razor` (title/description/category/project/tags form fields, `InputFile` with `@key`, extract name/size/contentType then copy to `MemoryStream` before clearing the `IBrowserFile` reference per constitution Development Workflow, upload progress indicator, success/error message display) in `ContosoDashboard/Shared/DocumentUploadModal.razor`
- [X] T021 [US1] Create `Documents.razor` with `@page "/documents"`, `[Authorize]`, and an upload entry point that opens `DocumentUploadModal`, showing the current user's uploaded documents in a minimal list (title, category, upload date) after a successful upload in `ContosoDashboard/Pages/Documents.razor`
- [X] T022 [US1] Wire `DocumentStorage:MaxFileSizeBytes` / `DocumentStorage:AllowedExtensions` configuration values into `DocumentService` validation instead of hardcoded constants in `ContosoDashboard/Services/DocumentService.cs`

**Checkpoint**: User Story 1 is fully functional and independently testable — a user can upload a document end-to-end and see it listed.

---

## Phase 4: User Story 2 - Browse and Find Documents (Priority: P2)

**Goal**: Users can view, sort, and filter their own documents, view a project's documents, and search across documents they have permission to see.

**Independent Test**: Follow quickstart.md scenario 2 — sort/filter "My Documents", view a project's documents as a member, and search by a keyword that only matches a document the searching user cannot access (must not appear).

### Tests for User Story 2

- [X] T023 [P] [US2] Unit tests for `DocumentService.GetMyDocumentsAsync` (sort by title/date/category/size, filter by category/project/date-range) and `SearchAsync` (matches title/description/tags/uploader/project, excludes documents the caller cannot access) in `ContosoDashboard.Tests/Services/DocumentServiceTests.cs`

### Implementation for User Story 2

- [X] T024 [US2] Implement `DocumentService.GetMyDocumentsAsync` with sorting, filtering, and pagination, eager-loading `Uploader`/`Project` via `.Include()` (constitution Principle IV) in `ContosoDashboard/Services/DocumentService.cs`
- [X] T025 [US2] Implement `DocumentService.GetProjectDocumentsAsync` with project-membership/PM/Administrator authorization in `ContosoDashboard/Services/DocumentService.cs`
- [X] T026 [US2] Implement `DocumentService.SearchAsync`, scoping results to documents visible via ownership, project membership, or share, before applying the search term (never filtering after the fact) in `ContosoDashboard/Services/DocumentService.cs`
- [X] T027 [US2] Build the "My Documents" list UI in `Documents.razor`: sortable columns (title, upload date, category, file size) and category/project/date-range filter controls in `ContosoDashboard/Pages/Documents.razor`
- [X] T028 [US2] Add a project documents section (list + upload button for PMs, per FR-014) to `ContosoDashboard/Pages/ProjectDetails.razor` (depends on T025)
- [X] T029 [US2] Add a search box and results view to `ContosoDashboard/Pages/Documents.razor` (depends on T026)

**Checkpoint**: User Stories 1 and 2 both work independently — documents can be found as well as uploaded.

---

## Phase 5: User Story 3 - Download and Preview Documents (Priority: P3)

**Goal**: Users can download any document they have access to, and preview PDFs/images inline in the browser.

**Independent Test**: Follow quickstart.md scenario 3 — download and preview a document as an authorized user; confirm a direct request to `/documents/{id}/download` by an unauthorized user returns `404`, not the file.

### Tests for User Story 3

- [X] T030 [P] [US3] Unit tests for `DocumentService.AuthorizeAccessAsync` covering owner, project member, department-share recipient, direct-user-share recipient, Administrator, and unauthorized-user cases in `ContosoDashboard.Tests/Services/DocumentServiceTests.cs`

### Implementation for User Story 3

- [X] T031 [US3] Implement `DocumentService.AuthorizeAccessAsync` per the authorization rule table in contracts/document-service-contract.md in `ContosoDashboard/Services/DocumentService.cs` (depends on T030)
- [X] T032 [US3] Create `DocumentsController` with `GET /documents/{id}/download` and `GET /documents/{id}/preview` actions per contracts/documents-controller-contract.md — call `AuthorizeAccessAsync` before touching the filesystem, return `404` for any unauthorized/missing case, `415` for unsupported preview types, stream via `IFileStorageService.DownloadAsync`, and log a "Download" `DocumentActivityLog` entry in `ContosoDashboard/Controllers/DocumentsController.cs` (depends on T031, T014)
- [X] T033 [US3] Add download links and inline preview rendering (`<img>` for JPEG/PNG, `<iframe>`/`<embed>` for PDF, pointed at the controller routes) to `ContosoDashboard/Pages/Documents.razor`

**Checkpoint**: User Stories 1–3 work independently — documents can be uploaded, found, downloaded, and previewed.

---

## Phase 6: User Story 4 - Edit and Delete Documents (Priority: P4)

**Goal**: Document owners can edit metadata and replace files; owners/Project Managers/Administrators can permanently delete documents, with Team Leads limited to metadata edits on their team's documents.

**Independent Test**: Follow quickstart.md scenario 4 — edit metadata, replace a file, delete with confirmation, and confirm the Team-Lead-cannot-delete / PM-can-delete-others'-documents authorization boundary.

### Tests for User Story 4

- [X] T034 [P] [US4] Unit tests for `UpdateMetadataAsync`, `ReplaceFileAsync`, and `DeleteAsync` authorization matrix (owner, Team Lead view/edit-only, Project Manager delete-in-their-project, Administrator, unrelated user denied) in `ContosoDashboard.Tests/Services/DocumentServiceTests.cs`

### Implementation for User Story 4

- [X] T035 [US4] Implement `DocumentService.GetByIdAsync` and `UpdateMetadataAsync` — callable by the document owner OR a Team Lead whose team includes the document's uploader (FR-024); `UpdateMetadataAsync` bumps `UpdatedDate` and writes a "MetadataEdit" activity log entry — in `ContosoDashboard/Services/DocumentService.cs` (depends on T034)
- [X] T036 [US4] Implement `DocumentService.ReplaceFileAsync` (validate new file same as upload, save new file, update the `Document` row, delete the old file only after the DB update succeeds, write a "FileReplace" activity log entry — no version history retained) in `ContosoDashboard/Services/DocumentService.cs`
- [X] T037 [US4] Implement `DocumentService.DeleteAsync` (owner, the project's Project Manager, or Administrator only — explicitly not Team Leads per FR-024; hard-delete the row and file; set surviving `DocumentActivityLog.DocumentId = null` with `DocumentTitleSnapshot` populated; write a "Delete" activity log entry) in `ContosoDashboard/Services/DocumentService.cs`
- [X] T038 [US4] Add an edit-metadata form and a delete-with-confirmation control to `ContosoDashboard/Pages/Documents.razor`
- [X] T039 [US4] Add a file-replace control to the edit flow in `ContosoDashboard/Shared/DocumentUploadModal.razor`

**Checkpoint**: User Stories 1–4 work independently — the full upload/find/retrieve/manage loop is complete.

---

## Phase 7: User Story 5 - Share Documents with Others (Priority: P5)

**Goal**: Document owners can share a document with a specific user or their department; recipients are notified and see shared documents in a dedicated view.

**Independent Test**: Follow quickstart.md scenario 5 — share with an individual and with a department, confirm notification + "Shared with Me" visibility, and confirm access disappears after the document is deleted.

### Tests for User Story 5

- [X] T040 [P] [US5] Unit tests for `DocumentService.ShareAsync` (individual-user target, department target, owner-only enforcement) and `GetSharedWithMeAsync` (direct shares plus dynamic department-membership matching) in `ContosoDashboard.Tests/Services/DocumentServiceTests.cs`

### Implementation for User Story 5

- [X] T041 [US5] Implement `DocumentService.ShareAsync` (create a `DocumentShare` row, call `INotificationService` to raise a `DocumentShared` notification, mark `NotificationSent`) in `ContosoDashboard/Services/DocumentService.cs` (depends on T040)
- [X] T042 [US5] Implement `DocumentService.GetSharedWithMeAsync`, matching direct `SharedWithUserId` rows and department shares against the caller's current `User.Department` in `ContosoDashboard/Services/DocumentService.cs`
- [X] T043 [US5] Add a share dialog (individual user picker or department entry) to `ContosoDashboard/Pages/Documents.razor`
- [X] T044 [US5] Create `SharedWithMe.razor` (`@page "/documents/shared-with-me"`, `[Authorize]`) listing documents shared with the current user in `ContosoDashboard/Pages/SharedWithMe.razor`
- [X] T045 [US5] Render the new `DocumentShared`/`DocumentAddedToProject` notification types (icon/label) in `ContosoDashboard/Pages/Notifications.razor`

**Checkpoint**: User Stories 1–5 work independently — sharing and cross-user visibility are complete.

---

## Phase 8: User Story 6 - Task and Dashboard Integration (Priority: P6)

**Goal**: Users can view/attach/upload documents from a task's detail view (auto-associated with the task's project), and see recent uploads and a document count on the dashboard home page.

**Independent Test**: Follow quickstart.md scenario 6 — attach and upload documents from a task detail view, confirm project auto-association, and confirm the dashboard widget/summary card and new-project-document notification.

### Tests for User Story 6

- [ ] T046 [P] [US6] Unit tests for `GetTaskDocumentsAsync`, `GetRecentAsync`, and `GetAccessibleDocumentCountAsync`, plus a test that uploading with a `TaskId` auto-sets `ProjectId` to the task's project in `ContosoDashboard.Tests/Services/DocumentServiceTests.cs`

### Implementation for User Story 6

- [ ] T047 [US6] Implement `DocumentService.GetTaskDocumentsAsync` and extend `UploadAsync` so a supplied `TaskId` auto-populates `ProjectId` from `TaskItem.ProjectId` (FR-026) in `ContosoDashboard/Services/DocumentService.cs` (depends on T046)
- [ ] T048 [US6] Implement `DocumentService.GetRecentAsync` (5 most recent uploads for the caller) and `GetAccessibleDocumentCountAsync` in `ContosoDashboard/Services/DocumentService.cs`
- [ ] T049 [US6] Implement the task detail view (replacing the `ViewTaskDetails` `// TODO` stub) with a document list, attach-existing-document control, and upload-from-task entry point in `ContosoDashboard/Pages/Tasks.razor`
- [ ] T050 [US6] Add a "Recent Documents" widget and a document-count summary card to `ContosoDashboard/Pages/Index.razor`
- [ ] T051 [US6] Trigger a `DocumentAddedToProject` notification to project members when a document is uploaded with a `ProjectId` set, in `ContosoDashboard/Services/DocumentService.cs`

**Checkpoint**: All six user stories are independently functional — the feature is feature-complete per spec.md.

---

## Phase 9: Polish & Cross-Cutting Concerns

**Purpose**: Audit/reporting requirements (FR-030/FR-031) and final hardening that span multiple stories.

- [ ] T052 [P] Implement `DocumentService.GetActivityLogAsync` and `GenerateActivityReportAsync` (Administrator-only; most-uploaded types, most active uploaders, access patterns) in `ContosoDashboard/Services/DocumentService.cs`
- [ ] T053 [P] Unit tests for `GenerateActivityReportAsync` authorization and aggregation correctness in `ContosoDashboard.Tests/Services/DocumentServiceTests.cs`
- [ ] T054 [P] Create an Administrator-only document activity/audit report page in `ContosoDashboard/Pages/DocumentReports.razor`
- [ ] T055 [P] Verify the `Content-Security-Policy` header in `ContosoDashboard/Program.cs` correctly permits the same-origin `<img>`/`<iframe>` preview sources introduced in T033 (no change expected since sources are same-origin, but confirm)
- [ ] T056 Run all `ContosoDashboard.Tests` (`dotnet test`) and fix any failures
- [ ] T057 Execute every scenario in quickstart.md manually against a locally running instance and confirm all expected outcomes

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — start immediately.
- **Foundational (Phase 2)**: Depends on Setup (T001, T002) — BLOCKS all user stories.
- **User Stories (Phase 3–8)**: All depend on Foundational (Phase 2) completion. Stories are listed in priority order (P1→P6) and are each independently testable, but within a story the Service layer must exist before the UI that calls it (e.g., T018 before T020/T021).
- **Polish (Phase 9)**: Depends on all desired user stories being complete.

### User Story Dependencies

- **US1 (P1)**: No dependency on other stories — the MVP.
- **US2 (P2)**: Independent of US1 at the data/service level; its UI builds on the `Documents.razor` page US1 creates (T021), so implement after US1 for a smooth increment, though the service methods themselves (T024–T026) have no US1 dependency.
- **US3 (P3)**: Independent service logic; its UI (T033) extends `Documents.razor`, so sequence after US1/US2 in practice.
- **US4 (P4)**: Independent service logic; UI extends `Documents.razor`/`DocumentUploadModal.razor`.
- **US5 (P5)**: Independent service logic; UI adds new pages/dialogs.
- **US6 (P6)**: Independent service logic; UI touches `Tasks.razor`/`Index.razor`, not `Documents.razor`, so it could be built in parallel with US2–US5 by a different developer once Foundational is done.

### Within Each User Story

- Tests written first, expected to fail against the `NotImplementedException` stubs from Phase 2.
- Service-layer implementation before UI.
- Story checkpoint reached only when both its tests pass and its UI is wired up.

### Parallel Opportunities

- Setup: T002, T003 in parallel (T001 is the prerequisite test project itself).
- Foundational: T004–T007 (separate model files) in parallel; then T009, T011, T012 in parallel; T015 in parallel with T009–T014.
- Once Foundational (Phase 2) completes: US1 and US6 can start in parallel (US6's UI doesn't touch `Documents.razor`); US2–US5 are best sequenced after US1 since they all extend `Documents.razor`, but their **service-layer** tasks (T024–T026, T031, T035–T037, T041–T042) have no file overlap with each other and can be parallelized across developers if `DocumentService.cs` edits are coordinated/merged carefully.
- All `[P]`-marked test tasks within a story can run in parallel with each other.

---

## Parallel Example: Foundational Phase

```bash
# Launch entity model creation together:
Task: "Create the Document entity in ContosoDashboard/Models/Document.cs"
Task: "Create the DocumentShare entity in ContosoDashboard/Models/DocumentShare.cs"
Task: "Create the DocumentActivityLog entity in ContosoDashboard/Models/DocumentActivityLog.cs"
Task: "Extend NotificationType in ContosoDashboard/Models/Notification.cs"

# Then, once entities exist, launch storage/service scaffolding together:
Task: "Create IFileStorageService interface in ContosoDashboard/Services/IFileStorageService.cs"
Task: "Create IMalwareScanner interface + impl in ContosoDashboard/Services/IMalwareScanner.cs"
Task: "Create IDocumentService interface + DTOs in ContosoDashboard/Services/IDocumentService.cs"
```

## Parallel Example: User Story 1

```bash
# Launch both test files together (both fail against the Phase 2 stub):
Task: "Unit tests for DocumentService.UploadAsync in ContosoDashboard.Tests/Services/DocumentServiceTests.cs"
Task: "Unit tests for LocalFileStorageService in ContosoDashboard.Tests/Services/LocalFileStorageServiceTests.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL — blocks all stories)
3. Complete Phase 3: User Story 1 (Upload a Document)
4. **STOP and VALIDATE**: run quickstart.md scenario 1 independently
5. Demo: employees can get documents off local drives/email into the dashboard — the core business need from spec.md is already addressed

### Incremental Delivery

1. Setup + Foundational → foundation ready
2. US1 (Upload) → validate → demo (MVP)
3. US2 (Browse/Find) → validate → demo
4. US3 (Download/Preview) → validate → demo
5. US4 (Edit/Delete) → validate → demo
6. US5 (Share) → validate → demo
7. US6 (Task/Dashboard integration) → validate → demo
8. Polish (audit reporting, quickstart sign-off)

### Parallel Team Strategy

With multiple developers, after Foundational (Phase 2) completes:
- Developer A: US1 → US2 → US3 (all extend `Documents.razor` sequentially)
- Developer B: US6 (touches `Tasks.razor`/`Index.razor` only — no file overlap with Developer A until both are done)
- Developer C: US4 → US5 once US1's `Documents.razor` skeleton (T021) exists, coordinating `DocumentService.cs` merges with Developer A

---

## Notes

- `[P]` tasks touch different files with no dependency on an incomplete task.
- Every `DocumentService.cs` task after T013 edits the same file — treat as sequential within a story even where not explicitly marked, and coordinate merges across parallel developers.
- Verify each story's tests fail before implementing, then pass after.
- Commit after each task or logical group.
- Stop at any checkpoint to validate a story independently before moving on.
- Team Leads are deliberately excluded from `DeleteAsync` authorization (FR-024) — do not "simplify" this away during implementation.
