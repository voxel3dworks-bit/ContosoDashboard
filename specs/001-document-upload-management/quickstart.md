# Quickstart: Validating Document Upload and Management

Manual/automated validation guide for this feature once implemented. Entity/field details are in [data-model.md](./data-model.md); service and HTTP contracts are in [contracts/](./contracts/).

## Prerequisites

- .NET 8 SDK installed.
- SQL Server LocalDB available (existing project requirement).
- Clean database state before first-time testing of upload (constitution Development Workflow gate):
  ```powershell
  sqllocaldb stop mssqllocaldb
  sqllocaldb delete mssqllocaldb
  # or, if EF tools/migrations are in use:
  dotnet ef database drop --force
  ```
- Confirm `AppData/uploads/` does not already contain stale files from a previous failed run; delete it if so (it will be recreated automatically).

## Setup

```powershell
cd C:\entrenamiento\ContosoDashboard
dotnet build
dotnet run --project ContosoDashboard
```

Log in via `/login` using one of the seeded users (dropdown, no password — existing mock auth). Use at least two different users across the scenarios below: one seeded as a **Project Manager** on a project, one as a plain **Employee** on the same project, and (if available) a third user in a **different department** for the sharing/negative-access checks.

## Validation scenarios

Each scenario maps to a User Story in [spec.md](./spec.md) and should be run against the running app.

### 1. Upload a document (User Story 1 / FR-001–FR-009)

1. Navigate to `/documents`, click upload, select a PDF under 25 MB, enter a title, choose a category, submit.
2. **Expected**: progress indicator shown, success message on completion, document appears in "My Documents" with correct uploader, upload date, size, and MIME type auto-filled.
3. Repeat with a file > 25 MB → **expected**: rejected with a size-limit error, no document created.
4. Repeat with an unsupported extension (e.g., `.exe` renamed to `.pdf`, or a genuinely unsupported type) → **expected**: rejected, no document created, no orphaned file under `AppData/uploads/`.

### 2. Browse and search (User Story 2 / FR-010–FR-015)

1. As the uploader, sort/filter "My Documents" by title, date, category, project, date range.
2. **Expected**: correct ordering/filtering; page loads well under 2 seconds.
3. As a project member, open the project's documents view.
4. **Expected**: sees only documents associated with that project.
5. Search by a keyword present only in another user's document title, description, tag, or their name.
6. **Expected**: results returned within 2 seconds; documents the searching user has no access to never appear, even if the keyword matches.

### 3. Download and preview (User Story 3 / FR-016–FR-017)

1. As a user with access, download a document → file downloads intact.
2. As the same user, preview a PDF and a PNG/JPEG → renders inline within 3 seconds (see [contracts/documents-controller-contract.md](./contracts/documents-controller-contract.md)).
3. As a user **without** access (different department, not a project member, not a share recipient), request `/documents/{id}/download` directly by URL.
4. **Expected**: `404`, not the file — confirms the IDOR check in `DocumentsController` (see contract).

### 4. Edit and delete (User Story 4 / FR-018–FR-021, FR-024)

1. As the owner, edit title/description/category/tags → changes persist and reflect immediately in list views.
2. As the owner, replace the file with a new one → new content downloads afterward; old content is gone (no version history).
3. As the owner, delete the document with confirmation → disappears from all views; verify (via DB or admin report) that a `DocumentActivityLog` entry survives with the title snapshot.
4. As a Team Lead (not the owner, not a PM on the project), attempt to delete a team member's document → **expected**: denied. Attempt to edit its metadata → **expected**: allowed (FR-024).
5. As the Project Manager of the document's project, delete a document uploaded by someone else on that project → **expected**: succeeds, logged with the PM as the acting user.

### 5. Share documents (User Story 5 / FR-022–FR-023)

1. As the owner, share a document with a specific individual user.
2. **Expected**: recipient gets an in-app notification and sees the document under "Shared with Me."
3. Owner deletes the shared document.
4. **Expected**: it disappears from the recipient's "Shared with Me" view.
5. Share a document with your own department (team share) and confirm every current member of that department can see it under "Shared with Me," and a user in a *different* department cannot.

### 6. Task and dashboard integration (User Story 6 / FR-025–FR-029)

1. Open a task's detail view → see related documents; attach an existing document to the task.
2. Upload a new document directly from the task detail view → **expected**: it's automatically associated with the task's project (verify via the project's documents view).
3. Go to the dashboard home page → **expected**: "Recent Documents" widget shows the signed-in user's 5 most recent uploads; summary card shows an accurate document count.
4. As a project member (not the uploader), have another user add a document to the shared project.
5. **Expected**: the project member receives a notification about the new project document.

## Automated coverage

`ContosoDashboard.Tests` (see [research.md](./research.md) §1) should cover at minimum, per [contracts/document-service-contract.md](./contracts/document-service-contract.md):
- `DocumentService.UploadAsync`: whitelist rejection, size rejection, correct generate→save-file→save-row ordering (including that a simulated disk-write failure leaves no DB row).
- Authorization boundaries for `GetProjectDocumentsAsync`, `AuthorizeAccessAsync`, `DeleteAsync`, `ShareAsync` (owner vs. non-owner vs. PM vs. Team Lead vs. Administrator vs. unrelated user).
- `LocalFileStorageService`: round-trip upload/download/delete against a temporary directory, and path generation never incorporates user-supplied filenames.

## Definition of done for this quickstart

All six scenarios above pass manually against a locally running instance, and the automated tests listed run green via `dotnet test`.
