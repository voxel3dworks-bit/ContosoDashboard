# Contract: DocumentsController (HTTP)

The only externally-reachable HTTP surface this feature adds. Required because files live outside `wwwroot` and therefore cannot be served by `UseStaticFiles()` — every request must pass through cookie authentication/authorization first (constitution Principle II & V, research.md §3). All routes require an authenticated session (existing cookie auth); unauthenticated requests are redirected to `/login` by the existing middleware pipeline, unchanged.

| Route | Method | Purpose | Success | Failure |
|---|---|---|---|---|
| `/documents/{id}/download` | GET | Stream the original file as an attachment (FR-016) | `200 OK`, `Content-Disposition: attachment; filename="{Document.FileName}"`, `Content-Type: {Document.FileType}`, body = file bytes | `404 Not Found` if the document doesn't exist or the caller is not authorized (identical response for both cases — no existence leakage, IDOR prevention) |
| `/documents/{id}/preview` | GET | Stream the file inline for browser rendering, PDF/JPEG/PNG only (FR-017) | `200 OK`, `Content-Disposition: inline`, `Content-Type: {Document.FileType}`, body = file bytes | `404 Not Found` (no access / doesn't exist, same as above); `415 Unsupported Media Type` if the document's type isn't PDF/JPEG/PNG |

## Behavior

1. Resolve `{id}` → call `IDocumentService.AuthorizeAccessAsync(currentUserId, id)`. If not authorized, return `404` immediately — do not touch the filesystem.
2. If authorized, call `IFileStorageService.DownloadAsync(document.FilePath)` and copy the stream to the HTTP response.
3. Both actions write a `DocumentActivityLog` entry (`Download` action) via `IDocumentService` before returning — satisfies FR-030's "log all... downloads."
4. No file bytes are ever cached in `wwwroot`, session state, or a Blazor Server component's in-memory state beyond the single stream copy needed to write the response body.

## Non-goals

- No public/anonymous access, no API-key auth, no separate versioned `/api/` prefix — this is a same-origin, cookie-authenticated endpoint for the app's own Blazor pages (`<a href>`/`<img src>`/`<iframe src>` pointed at these routes), not a general-purpose API for external systems (matches spec.md's Out of Scope: no external system integration).
