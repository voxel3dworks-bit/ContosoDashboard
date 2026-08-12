using System.Security.Claims;
using ContosoDashboard.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ContosoDashboard.Controllers;

/// <summary>
/// The only externally-reachable HTTP surface this feature adds. Files live outside wwwroot
/// (constitution Principle V) so UseStaticFiles() cannot serve them — every request must pass
/// through this authenticated, per-request-authorized endpoint instead (research.md #3,
/// contracts/documents-controller-contract.md).
/// </summary>
[Authorize]
[Route("documents")]
public class DocumentsController : Controller
{
    private static readonly HashSet<string> PreviewableContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/jpeg", "image/png"
    };

    private readonly IDocumentService _documentService;
    private readonly IFileStorageService _fileStorageService;

    public DocumentsController(IDocumentService documentService, IFileStorageService fileStorageService)
    {
        _documentService = documentService;
        _fileStorageService = fileStorageService;
    }

    [HttpGet("{id:int}/download")]
    public async Task<IActionResult> Download(int id)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return NotFound();
        }

        var access = await _documentService.AuthorizeAccessAsync(userId.Value, id);
        if (!access.IsAuthorized || access.Document == null)
        {
            // Identical response for "doesn't exist" and "not authorized" — no existence leakage (IDOR prevention).
            return NotFound();
        }

        var document = access.Document;
        var stream = await _fileStorageService.DownloadAsync(document.FilePath);
        await _documentService.RecordDownloadAsync(userId.Value, id);

        Response.Headers.ContentDisposition = $"attachment; filename=\"{document.FileName}\"";
        return File(stream, document.FileType);
    }

    [HttpGet("{id:int}/preview")]
    public async Task<IActionResult> Preview(int id)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return NotFound();
        }

        var access = await _documentService.AuthorizeAccessAsync(userId.Value, id);
        if (!access.IsAuthorized || access.Document == null)
        {
            return NotFound();
        }

        var document = access.Document;
        if (!PreviewableContentTypes.Contains(document.FileType))
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var stream = await _fileStorageService.DownloadAsync(document.FilePath);
        await _documentService.RecordDownloadAsync(userId.Value, id);

        Response.Headers.ContentDisposition = "inline";
        return File(stream, document.FileType);
    }

    private int? GetCurrentUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        return claim != null && int.TryParse(claim.Value, out var id) ? id : null;
    }
}
