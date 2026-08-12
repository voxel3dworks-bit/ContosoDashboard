# Phase 0 Research: Document Upload and Management

All items below were either explicit stakeholder/constitution constraints (no open question, decision recorded for traceability) or genuine implementation unknowns resolved here so Phase 1 design has no outstanding `NEEDS CLARIFICATION` markers.

## 1. Automated test project

- **Decision**: Add a new `ContosoDashboard.Tests` xUnit project, referencing the main project and using EF Core's `Microsoft.EntityFrameworkCore.InMemory` (or SQLite in-memory) provider to exercise `DocumentService` against a real `DbContext` without LocalDB.
- **Rationale**: Constitution Principle III is explicit and non-negotiable: "Automated unit and integration tests MUST accompany all new features." No test project exists in the repo today, so this feature must introduce the first one. xUnit is the de facto standard for ASP.NET Core/EF Core projects and has first-class `WebApplicationFactory`/EF Core in-memory support if integration tests are added later.
- **Alternatives considered**: MSTest, NUnit — both viable but xUnit has the broadest ecosystem alignment with ASP.NET Core samples/tooling; no project convention exists yet to override this default. Skipping tests was rejected outright — it directly violates a non-negotiable constitution principle.

## 2. Malware/virus scanning (offline constraint)

- **Decision**: Introduce an `IMalwareScanner` abstraction (`ScanAsync(Stream) -> ScanResult`) with a training-environment implementation that performs deterministic file-signature/extension validation (magic-byte check against the declared content type) rather than a full antivirus engine, since the environment must run fully offline with no cloud services and no commercial AV product is assumed to be installed. Real AV engine integration (e.g., an on-box ClamAV or cloud AV API) is a documented future swap behind the same interface — mirroring the `IFileStorageService` pattern.
- **Rationale**: FR-006 requires a scan step before storage, but the constitution and stakeholder doc both mandate offline operation with no cloud services and no major new infrastructure. A pluggable interface satisfies FR-006 today (rejecting mismatched/malformed files) while leaving room for a real scanning engine later without touching `DocumentService` call sites.
- **Alternatives considered**: Skipping the scan step (rejected — violates FR-006 and the security/audit success criteria SC-008); requiring a third-party AV engine as a hard dependency (rejected — violates offline-first constraint and 8-10 week timeline).

## 3. Authenticated file download/preview delivery

- **Decision**: Add a minimal API/MVC `Controller` (`DocumentsController`, actions `GET /documents/{id}/download` and `GET /documents/{id}/preview`) that resolves the file through `IFileStorageService.DownloadAsync`, streams it with the correct `Content-Type`, and enforces the same authorization checks as `DocumentService` before returning any bytes.
- **Rationale**: Files are stored outside `wwwroot` specifically so `UseStaticFiles()` cannot serve them directly (constitution Principle V, "enables authorization checks"). Blazor Server components cannot stream arbitrary binary HTTP responses on their own render pipeline, so a thin Controller endpoint — reachable because the cookie auth middleware already runs ahead of routing — is the standard ASP.NET Core pattern for this. This is the first Controller in the repo; `Program.cs` needs `AddControllers()`/`MapControllers()` added alongside the existing Razor Pages/Blazor Hub registration.
- **Alternatives considered**: Serving files as static content from a second, separately-secured static file root (rejected — `UseStaticFiles()` has no per-request authorization hook); Base64-embedding file bytes into Blazor Server component state (rejected — breaks the 25 MB size target and Blazor Server's SignalR circuit has message-size practical limits).

## 4. In-browser preview for PDF/images

- **Decision**: For images (JPEG/PNG), render an `<img>` tag pointed at the authenticated preview endpoint. For PDFs, render an `<iframe>`/`<embed>` pointed at the same endpoint, relying on the browser's native PDF viewer (no third-party PDF.js/viewer library).
- **Rationale**: Meets FR-017 and SC-004 (preview within 3 seconds) with zero new dependencies, consistent with the offline/no-major-new-infrastructure constraint. All modern browsers render PDFs natively.
- **Alternatives considered**: A JS PDF-rendering library (rejected — unnecessary dependency for a solved problem, adds bundle weight and CSP surface); server-side thumbnail generation (rejected — out of scope, adds processing complexity not requested by the spec).

## 5. Blazor upload component state handling

- **Decision**: Follow the stakeholder-doc-mandated pattern exactly: extract `Name`/`Size`/`ContentType` into locals before opening the stream, copy `IBrowserFile.OpenReadStream()` into a `MemoryStream` immediately, set the `IBrowserFile` reference to `null` after copying, and use `@key` on `InputFile` to force re-render after a successful upload.
- **Rationale**: This is an explicit, non-negotiable implementation requirement from both the stakeholder document and the constitution's Development Workflow section, addressing known Blazor Server `IBrowserFile` disposal/reuse pitfalls.
- **Alternatives considered**: None — this is a fixed constraint, not an open design choice.

## 6. GUID-based storage path convention

- **Decision**: `AppData/uploads/{userId}/{projectId or "personal"}/{guid}.{extension}`, generated *before* the file is written to disk, which happens *before* the `Document` row is inserted (generate → save file → save DB row).
- **Rationale**: Directly specified by the stakeholder document and constitution Principle V; prevents path traversal (no user-supplied filename ever touches the filesystem path), prevents duplicate-key DB errors from empty/non-unique paths, and prevents orphaned DB rows if the disk write fails.
- **Alternatives considered**: Flat `uploads/{guid}.{ext}` with no per-user/project partitioning (rejected — makes future per-user storage auditing/cleanup harder and doesn't match the explicit stakeholder-specified pattern); database-generated identity used directly in the path (rejected — requires the DB row to exist before the file is saved, inverting the mandated safe sequence).

## 7. Category field representation

- **Decision**: `Document.Category` is a required `string` (`[MaxLength(50)]`), validated at the service layer against a fixed allowed list (`Project Documents`, `Team Resources`, `Personal Files`, `Reports`, `Presentations`, `Other`), not a C# `enum`.
- **Rationale**: Explicit constraint from both the stakeholder document and constitution: "Category must store text values (not integer enum) for simplicity."
- **Alternatives considered**: `enum Category` mapped via EF Core value converter (rejected — explicitly ruled out by the constraint, despite being the more "typical" EF Core pattern used elsewhere in this codebase for `ProjectStatus`/`TaskPriority`).

## 8. "Team" definition for sharing (FR-022)

- **Decision**: A "team" share targets all users sharing the same `User.Department` value as the document owner at the time of the share (evaluated dynamically — i.e., access is computed from current department membership, not a frozen snapshot list of user IDs).
- **Rationale**: `User.Department` already exists (nullable string, no dedicated `Team`/`Department` entity or FK). Reusing it avoids introducing a new organizational entity outside this feature's scope, and matches the Assumptions section of spec.md.
- **Alternatives considered**: Sharing scoped to `ProjectMember` rows on a specific project (rejected as the "team" definition — that's already covered by project-level document visibility per FR-013, so it would be redundant with "team" sharing specifically); introducing a new `Team` entity (rejected — no such entity exists today and spec.md's Out of Scope/Assumptions don't call for one).

## 9. Task detail page

- **Decision**: `Pages/Tasks.razor`'s existing `ViewTaskDetails(int taskId)` handler (currently a `// TODO` stub with no navigation target) will be implemented as part of this feature — a task detail view (modal or dedicated section) sufficient to host the document list/attach/upload UI required by FR-025/FR-026. No other task-management functionality is added.
- **Rationale**: FR-025/FR-026 require a "task detail page" that does not exist yet in any form; the existing stub is the natural integration point and avoids inventing an unrelated new navigation surface.
- **Alternatives considered**: Adding a documents section to the flat `Tasks.razor` list view without a detail view (rejected — spec.md's User Story 6 specifically describes viewing/attaching from *a task's* detail context, and doing it inline on the list would conflate every task's documents in one place).
