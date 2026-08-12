using ContosoDashboard.Models;
using ContosoDashboard.Services;
using ContosoDashboard.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ContosoDashboard.Tests.Services;

public class DocumentServiceTests : IDisposable
{
    private readonly DocumentTestFixture _fixture;
    private readonly FakeFileStorageService _fileStorage;
    private readonly FakeMalwareScanner _malwareScanner;
    private readonly DocumentService _sut;

    public DocumentServiceTests()
    {
        _fixture = new DocumentTestFixture();
        _fileStorage = new FakeFileStorageService();
        _malwareScanner = new FakeMalwareScanner();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DocumentStorage:MaxFileSizeBytes"] = "26214400",
                ["DocumentStorage:AllowedExtensions:0"] = ".pdf",
                ["DocumentStorage:AllowedExtensions:1"] = ".doc",
                ["DocumentStorage:AllowedExtensions:2"] = ".docx",
                ["DocumentStorage:AllowedExtensions:3"] = ".xls",
                ["DocumentStorage:AllowedExtensions:4"] = ".xlsx",
                ["DocumentStorage:AllowedExtensions:5"] = ".ppt",
                ["DocumentStorage:AllowedExtensions:6"] = ".pptx",
                ["DocumentStorage:AllowedExtensions:7"] = ".txt",
                ["DocumentStorage:AllowedExtensions:8"] = ".jpg",
                ["DocumentStorage:AllowedExtensions:9"] = ".jpeg",
                ["DocumentStorage:AllowedExtensions:10"] = ".png"
            })
            .Build();

        _sut = new DocumentService(_fixture.Context, _fileStorage, _malwareScanner, new NotificationService(_fixture.Context), configuration);
    }

    public void Dispose() => _fixture.Dispose();

    private static DocumentUploadRequest ValidRequest(string fileName = "report.pdf", long sizeBytes = 1024, int? projectId = null) => new()
    {
        FileStream = new MemoryStream(new byte[] { 1, 2, 3, 4 }),
        FileName = fileName,
        ContentType = "application/pdf",
        FileSizeBytes = sizeBytes,
        Title = "Quarterly Report",
        Category = DocumentCategories.Reports,
        ProjectId = projectId
    };

    [Fact]
    public async Task UploadAsync_RejectsUnsupportedFileExtension()
    {
        var request = ValidRequest(fileName: "malicious.exe");

        var result = await _sut.UploadAsync(DocumentTestFixture.EmployeeUserId, request);

        Assert.False(result.Success);
        Assert.Empty(_fixture.Context.Documents);
        Assert.Empty(_fileStorage.UploadedPaths);
    }

    [Fact]
    public async Task UploadAsync_RejectsFileOverSizeLimit()
    {
        var request = ValidRequest(sizeBytes: 26_214_401); // 25 MB + 1 byte

        var result = await _sut.UploadAsync(DocumentTestFixture.EmployeeUserId, request);

        Assert.False(result.Success);
        Assert.Empty(_fixture.Context.Documents);
    }

    [Fact]
    public async Task UploadAsync_RejectsMissingTitle()
    {
        var request = ValidRequest();
        request.Title = "";

        var result = await _sut.UploadAsync(DocumentTestFixture.EmployeeUserId, request);

        Assert.False(result.Success);
        Assert.Empty(_fixture.Context.Documents);
    }

    [Fact]
    public async Task UploadAsync_RejectsInvalidCategory()
    {
        var request = ValidRequest();
        request.Category = "Not A Real Category";

        var result = await _sut.UploadAsync(DocumentTestFixture.EmployeeUserId, request);

        Assert.False(result.Success);
        Assert.Empty(_fixture.Context.Documents);
    }

    [Fact]
    public async Task UploadAsync_RejectsWhenMalwareScanFails()
    {
        _malwareScanner.ShouldFlagAsInfected = true;
        var request = ValidRequest();

        var result = await _sut.UploadAsync(DocumentTestFixture.EmployeeUserId, request);

        Assert.False(result.Success);
        Assert.Empty(_fixture.Context.Documents);
        Assert.Empty(_fileStorage.UploadedPaths);
    }

    [Fact]
    public async Task UploadAsync_CreatesDocumentRow_WhenRequestIsValid()
    {
        var request = ValidRequest();

        var result = await _sut.UploadAsync(DocumentTestFixture.EmployeeUserId, request);

        Assert.True(result.Success);
        Assert.NotNull(result.DocumentId);
        Assert.Single(_fileStorage.UploadedPaths);

        var saved = await _fixture.Context.Documents.FindAsync(result.DocumentId);
        Assert.NotNull(saved);
        Assert.Equal("Quarterly Report", saved!.Title);
        Assert.Equal(DocumentTestFixture.EmployeeUserId, saved.UploadedByUserId);
        Assert.Equal(_fileStorage.UploadedPaths[0], saved.FilePath);
    }

    [Fact]
    public async Task UploadAsync_WritesUploadActivityLogEntry_WhenRequestIsValid()
    {
        var request = ValidRequest();

        var result = await _sut.UploadAsync(DocumentTestFixture.EmployeeUserId, request);

        var logEntry = await _fixture.Context.DocumentActivityLogs
            .FirstOrDefaultAsync(l => l.DocumentId == result.DocumentId);

        Assert.NotNull(logEntry);
        Assert.Equal(DocumentActivityType.Upload, logEntry!.Action);
        Assert.Equal(DocumentTestFixture.EmployeeUserId, logEntry.UserId);
    }

    [Fact]
    public async Task UploadAsync_LeavesNoDocumentRow_WhenDiskWriteFails()
    {
        _fileStorage.ThrowOnUpload = true;
        var request = ValidRequest();

        var result = await _sut.UploadAsync(DocumentTestFixture.EmployeeUserId, request);

        Assert.False(result.Success);
        Assert.Empty(_fixture.Context.Documents);
    }

    [Fact]
    public async Task UploadAsync_RejectsUpload_WhenCallerIsNotProjectMember()
    {
        var request = ValidRequest(projectId: DocumentTestFixture.ProjectId);

        var result = await _sut.UploadAsync(DocumentTestFixture.OutsiderUserId, request);

        Assert.False(result.Success);
        Assert.Empty(_fixture.Context.Documents);
    }

    [Fact]
    public async Task UploadAsync_AllowsUpload_WhenCallerIsProjectMember()
    {
        var request = ValidRequest(projectId: DocumentTestFixture.ProjectId);

        var result = await _sut.UploadAsync(DocumentTestFixture.EmployeeUserId, request);

        Assert.True(result.Success);
        var saved = await _fixture.Context.Documents.FindAsync(result.DocumentId);
        Assert.Equal(DocumentTestFixture.ProjectId, saved!.ProjectId);
    }

    [Fact]
    public async Task UploadAsync_AllowsUpload_WhenCallerIsProjectManager()
    {
        var request = ValidRequest(projectId: DocumentTestFixture.ProjectId);

        var result = await _sut.UploadAsync(DocumentTestFixture.ProjectManagerUserId, request);

        Assert.True(result.Success);
    }

    private Document SeedDocument(
        string title,
        string category,
        int uploaderId,
        int? projectId = null,
        long fileSizeBytes = 1000,
        DateTime? uploadDate = null,
        string? description = null,
        string? tags = null)
    {
        var document = new Document
        {
            Title = title,
            Category = category,
            UploadedByUserId = uploaderId,
            ProjectId = projectId,
            FileName = "file.pdf",
            FilePath = $"{uploaderId}/{(projectId?.ToString() ?? "personal")}/{Guid.NewGuid()}.pdf",
            FileType = "application/pdf",
            FileSizeBytes = fileSizeBytes,
            UploadDate = uploadDate ?? DateTime.UtcNow,
            UpdatedDate = DateTime.UtcNow,
            Description = description,
            Tags = tags
        };

        _fixture.Context.Documents.Add(document);
        _fixture.Context.SaveChanges();
        return document;
    }

    [Fact]
    public async Task GetMyDocumentsAsync_ReturnsOnlyDocumentsUploadedByCaller()
    {
        SeedDocument("Mine", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);
        SeedDocument("Not Mine", DocumentCategories.PersonalFiles, DocumentTestFixture.ProjectManagerUserId);

        var result = await _sut.GetMyDocumentsAsync(DocumentTestFixture.EmployeeUserId, new DocumentQuery());

        Assert.Single(result.Items);
        Assert.Equal("Mine", result.Items[0].Title);
    }

    [Fact]
    public async Task GetMyDocumentsAsync_SortsByTitleAscending()
    {
        SeedDocument("Zebra Report", DocumentCategories.Reports, DocumentTestFixture.EmployeeUserId);
        SeedDocument("Alpha Report", DocumentCategories.Reports, DocumentTestFixture.EmployeeUserId);

        var result = await _sut.GetMyDocumentsAsync(DocumentTestFixture.EmployeeUserId,
            new DocumentQuery { SortBy = DocumentSortField.Title, SortDescending = false });

        Assert.Equal(new[] { "Alpha Report", "Zebra Report" }, result.Items.Select(d => d.Title));
    }

    [Fact]
    public async Task GetMyDocumentsAsync_SortsByFileSizeDescending()
    {
        SeedDocument("Small", DocumentCategories.Reports, DocumentTestFixture.EmployeeUserId, fileSizeBytes: 100);
        SeedDocument("Large", DocumentCategories.Reports, DocumentTestFixture.EmployeeUserId, fileSizeBytes: 9000);

        var result = await _sut.GetMyDocumentsAsync(DocumentTestFixture.EmployeeUserId,
            new DocumentQuery { SortBy = DocumentSortField.FileSizeBytes, SortDescending = true });

        Assert.Equal(new[] { "Large", "Small" }, result.Items.Select(d => d.Title));
    }

    [Fact]
    public async Task GetMyDocumentsAsync_FiltersByCategory()
    {
        SeedDocument("A Report", DocumentCategories.Reports, DocumentTestFixture.EmployeeUserId);
        SeedDocument("A Presentation", DocumentCategories.Presentations, DocumentTestFixture.EmployeeUserId);

        var result = await _sut.GetMyDocumentsAsync(DocumentTestFixture.EmployeeUserId,
            new DocumentQuery { CategoryFilter = DocumentCategories.Reports });

        Assert.Single(result.Items);
        Assert.Equal("A Report", result.Items[0].Title);
    }

    [Fact]
    public async Task GetMyDocumentsAsync_FiltersByProject()
    {
        SeedDocument("Project Doc", DocumentCategories.ProjectDocuments, DocumentTestFixture.EmployeeUserId, projectId: DocumentTestFixture.ProjectId);
        SeedDocument("Personal Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var result = await _sut.GetMyDocumentsAsync(DocumentTestFixture.EmployeeUserId,
            new DocumentQuery { ProjectIdFilter = DocumentTestFixture.ProjectId });

        Assert.Single(result.Items);
        Assert.Equal("Project Doc", result.Items[0].Title);
    }

    [Fact]
    public async Task GetMyDocumentsAsync_FiltersByDateRange()
    {
        SeedDocument("Old Doc", DocumentCategories.Reports, DocumentTestFixture.EmployeeUserId, uploadDate: DateTime.UtcNow.AddDays(-30));
        SeedDocument("Recent Doc", DocumentCategories.Reports, DocumentTestFixture.EmployeeUserId, uploadDate: DateTime.UtcNow.AddDays(-1));

        var result = await _sut.GetMyDocumentsAsync(DocumentTestFixture.EmployeeUserId,
            new DocumentQuery { UploadedFrom = DateTime.UtcNow.AddDays(-5), UploadedTo = DateTime.UtcNow });

        Assert.Single(result.Items);
        Assert.Equal("Recent Doc", result.Items[0].Title);
    }

    [Fact]
    public async Task GetProjectDocumentsAsync_ReturnsDocuments_WhenCallerIsProjectMember()
    {
        SeedDocument("Project Doc", DocumentCategories.ProjectDocuments, DocumentTestFixture.ProjectManagerUserId, projectId: DocumentTestFixture.ProjectId);

        var result = await _sut.GetProjectDocumentsAsync(DocumentTestFixture.EmployeeUserId, DocumentTestFixture.ProjectId);

        Assert.Single(result);
    }

    [Fact]
    public async Task GetProjectDocumentsAsync_ReturnsEmpty_WhenCallerIsNotAMember()
    {
        SeedDocument("Project Doc", DocumentCategories.ProjectDocuments, DocumentTestFixture.ProjectManagerUserId, projectId: DocumentTestFixture.ProjectId);

        var result = await _sut.GetProjectDocumentsAsync(DocumentTestFixture.OutsiderUserId, DocumentTestFixture.ProjectId);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetProjectDocumentsAsync_ReturnsDocuments_WhenCallerIsAdministrator()
    {
        SeedDocument("Project Doc", DocumentCategories.ProjectDocuments, DocumentTestFixture.ProjectManagerUserId, projectId: DocumentTestFixture.ProjectId);

        var result = await _sut.GetProjectDocumentsAsync(DocumentTestFixture.AdminUserId, DocumentTestFixture.ProjectId);

        Assert.Single(result);
    }

    [Fact]
    public async Task SearchAsync_MatchesTitle()
    {
        SeedDocument("Unique Search Target Alpha", DocumentCategories.Reports, DocumentTestFixture.EmployeeUserId);

        var result = await _sut.SearchAsync(DocumentTestFixture.EmployeeUserId, "Search Target Alpha", new DocumentQuery());

        Assert.Single(result.Items);
    }

    [Fact]
    public async Task SearchAsync_MatchesDescriptionTagsAndUploaderName()
    {
        SeedDocument("Report One", DocumentCategories.Reports, DocumentTestFixture.EmployeeUserId, description: "Contains UniqueDescriptionMarker text");
        SeedDocument("Report Two", DocumentCategories.Reports, DocumentTestFixture.EmployeeUserId, tags: "UniqueTagMarker,other");

        var byDescription = await _sut.SearchAsync(DocumentTestFixture.EmployeeUserId, "UniqueDescriptionMarker", new DocumentQuery());
        var byTag = await _sut.SearchAsync(DocumentTestFixture.EmployeeUserId, "UniqueTagMarker", new DocumentQuery());
        var byUploaderName = await _sut.SearchAsync(DocumentTestFixture.EmployeeUserId, "Ni Kang", new DocumentQuery());

        Assert.Single(byDescription.Items);
        Assert.Single(byTag.Items);
        Assert.Equal(2, byUploaderName.Items.Count); // both documents uploaded by "Ni Kang" (EmployeeUserId)
    }

    [Fact]
    public async Task SearchAsync_ExcludesDocuments_CallerCannotAccess()
    {
        SeedDocument("Confidential Marker Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.ProjectManagerUserId);

        var result = await _sut.SearchAsync(DocumentTestFixture.OutsiderUserId, "Confidential Marker Doc", new DocumentQuery());

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task SearchAsync_IncludesProjectDocuments_ForProjectMembers()
    {
        SeedDocument("Shared Project Marker Doc", DocumentCategories.ProjectDocuments, DocumentTestFixture.ProjectManagerUserId, projectId: DocumentTestFixture.ProjectId);

        var result = await _sut.SearchAsync(DocumentTestFixture.EmployeeUserId, "Shared Project Marker Doc", new DocumentQuery());

        Assert.Single(result.Items);
    }

    [Fact]
    public async Task SearchAsync_Administrator_SeesAllDocuments()
    {
        SeedDocument("Admin Visible Marker Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var result = await _sut.SearchAsync(DocumentTestFixture.AdminUserId, "Admin Visible Marker Doc", new DocumentQuery());

        Assert.Single(result.Items);
    }

    [Fact]
    public async Task AuthorizeAccessAsync_GrantsAccess_ToOwner()
    {
        var document = SeedDocument("Owner's Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var access = await _sut.AuthorizeAccessAsync(DocumentTestFixture.EmployeeUserId, document.DocumentId);

        Assert.True(access.IsAuthorized);
        Assert.NotNull(access.Document);
        Assert.Equal("Owner's Doc", access.Document!.Title);
    }

    [Fact]
    public async Task AuthorizeAccessAsync_GrantsAccess_ToProjectMember()
    {
        var document = SeedDocument("Project Doc", DocumentCategories.ProjectDocuments, DocumentTestFixture.ProjectManagerUserId, projectId: DocumentTestFixture.ProjectId);

        var access = await _sut.AuthorizeAccessAsync(DocumentTestFixture.EmployeeUserId, document.DocumentId);

        Assert.True(access.IsAuthorized);
    }

    [Fact]
    public async Task AuthorizeAccessAsync_GrantsAccess_ToAdministrator()
    {
        var document = SeedDocument("Someone Else's Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var access = await _sut.AuthorizeAccessAsync(DocumentTestFixture.AdminUserId, document.DocumentId);

        Assert.True(access.IsAuthorized);
    }

    [Fact]
    public async Task AuthorizeAccessAsync_DeniesAccess_ToUnrelatedUser()
    {
        var document = SeedDocument("Private Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var access = await _sut.AuthorizeAccessAsync(DocumentTestFixture.OutsiderUserId, document.DocumentId);

        Assert.False(access.IsAuthorized);
        Assert.Null(access.Document);
    }

    [Fact]
    public async Task AuthorizeAccessAsync_DeniesAccess_ToNonexistentDocument()
    {
        var access = await _sut.AuthorizeAccessAsync(DocumentTestFixture.EmployeeUserId, documentId: 999999);

        Assert.False(access.IsAuthorized);
    }

    [Fact]
    public async Task AuthorizeAccessAsync_GrantsAccess_ToDirectShareRecipient()
    {
        var document = SeedDocument("Shared Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.ProjectManagerUserId);
        _fixture.Context.DocumentShares.Add(new DocumentShare
        {
            DocumentId = document.DocumentId,
            SharedByUserId = DocumentTestFixture.ProjectManagerUserId,
            SharedWithUserId = DocumentTestFixture.OutsiderUserId
        });
        _fixture.Context.SaveChanges();

        var access = await _sut.AuthorizeAccessAsync(DocumentTestFixture.OutsiderUserId, document.DocumentId);

        Assert.True(access.IsAuthorized);
    }

    [Fact]
    public async Task AuthorizeAccessAsync_GrantsAccess_ToDepartmentShareRecipient()
    {
        // Document owner (Engineering) shares with the "Sales" department, which OutsiderUserId belongs to.
        var document = SeedDocument("Department Shared Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.ProjectManagerUserId);
        _fixture.Context.DocumentShares.Add(new DocumentShare
        {
            DocumentId = document.DocumentId,
            SharedByUserId = DocumentTestFixture.ProjectManagerUserId,
            SharedWithDepartment = "Sales"
        });
        _fixture.Context.SaveChanges();

        var access = await _sut.AuthorizeAccessAsync(DocumentTestFixture.OutsiderUserId, document.DocumentId);

        Assert.True(access.IsAuthorized);
    }

    [Fact]
    public async Task AuthorizeAccessAsync_DeniesAccess_ToUserOutsideSharedDepartment()
    {
        // Shared with "Sales", but the requester (TeamLeadUserId) is in "Engineering" and not otherwise related.
        var document = SeedDocument("Department Shared Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.ProjectManagerUserId);
        _fixture.Context.DocumentShares.Add(new DocumentShare
        {
            DocumentId = document.DocumentId,
            SharedByUserId = DocumentTestFixture.ProjectManagerUserId,
            SharedWithDepartment = "Sales"
        });
        _fixture.Context.SaveChanges();

        var access = await _sut.AuthorizeAccessAsync(DocumentTestFixture.TeamLeadUserId, document.DocumentId);

        Assert.False(access.IsAuthorized);
    }

    [Fact]
    public async Task UpdateMetadataAsync_Allowed_ForOwner()
    {
        var document = SeedDocument("Original Title", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var success = await _sut.UpdateMetadataAsync(DocumentTestFixture.EmployeeUserId, document.DocumentId,
            new DocumentMetadataUpdate { Title = "Updated Title", Category = DocumentCategories.Reports });

        Assert.True(success);
        var updated = await _fixture.Context.Documents.FindAsync(document.DocumentId);
        Assert.Equal("Updated Title", updated!.Title);
        Assert.Equal(DocumentCategories.Reports, updated.Category);
    }

    [Fact]
    public async Task UpdateMetadataAsync_Allowed_ForTeamLeadOfUploader()
    {
        // TeamLeadUserId (3) is seeded as ProjectMember Role="TeamLead" on ProjectId 1;
        // EmployeeUserId (4) is seeded as a member of the same project — i.e., on the Team Lead's team.
        var document = SeedDocument("Employee's Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var success = await _sut.UpdateMetadataAsync(DocumentTestFixture.TeamLeadUserId, document.DocumentId,
            new DocumentMetadataUpdate { Title = "Edited by Team Lead", Category = DocumentCategories.Reports });

        Assert.True(success);
    }

    [Fact]
    public async Task UpdateMetadataAsync_Denied_ForUnrelatedUser()
    {
        var document = SeedDocument("Private Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var success = await _sut.UpdateMetadataAsync(DocumentTestFixture.OutsiderUserId, document.DocumentId,
            new DocumentMetadataUpdate { Title = "Hijacked Title", Category = DocumentCategories.Reports });

        Assert.False(success);
        var unchanged = await _fixture.Context.Documents.FindAsync(document.DocumentId);
        Assert.Equal("Private Doc", unchanged!.Title);
    }

    [Fact]
    public async Task UpdateMetadataAsync_WritesMetadataEditActivityLogEntry()
    {
        var document = SeedDocument("Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        await _sut.UpdateMetadataAsync(DocumentTestFixture.EmployeeUserId, document.DocumentId,
            new DocumentMetadataUpdate { Title = "Doc v2", Category = DocumentCategories.Reports });

        var logEntry = await _fixture.Context.DocumentActivityLogs
            .FirstOrDefaultAsync(l => l.DocumentId == document.DocumentId && l.Action == DocumentActivityType.MetadataEdit);
        Assert.NotNull(logEntry);
    }

    [Fact]
    public async Task GetByIdAsync_Allowed_ForTeamLeadOfUploader()
    {
        var document = SeedDocument("Employee's Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var detail = await _sut.GetByIdAsync(DocumentTestFixture.TeamLeadUserId, document.DocumentId);

        Assert.NotNull(detail);
    }

    [Fact]
    public async Task GetByIdAsync_Denied_ForUnrelatedUser()
    {
        var document = SeedDocument("Private Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var detail = await _sut.GetByIdAsync(DocumentTestFixture.OutsiderUserId, document.DocumentId);

        Assert.Null(detail);
    }

    [Fact]
    public async Task ReplaceFileAsync_Allowed_ForOwner()
    {
        var document = SeedDocument("Doc", DocumentCategories.Reports, DocumentTestFixture.EmployeeUserId);
        using var newContent = new MemoryStream(new byte[] { 1, 2, 3, 4 });

        var result = await _sut.ReplaceFileAsync(DocumentTestFixture.EmployeeUserId, document.DocumentId, newContent, "replacement.pdf", "application/pdf");

        Assert.True(result.Success);
        var updated = await _fixture.Context.Documents.FindAsync(document.DocumentId);
        Assert.Equal("replacement.pdf", updated!.FileName);
    }

    [Fact]
    public async Task ReplaceFileAsync_Denied_ForTeamLead()
    {
        // Team Leads get metadata view/edit rights (FR-024) but not file-replace rights.
        var document = SeedDocument("Employee's Doc", DocumentCategories.Reports, DocumentTestFixture.EmployeeUserId);
        using var newContent = new MemoryStream(new byte[] { 1, 2, 3, 4 });

        var result = await _sut.ReplaceFileAsync(DocumentTestFixture.TeamLeadUserId, document.DocumentId, newContent, "replacement.pdf", "application/pdf");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ReplaceFileAsync_DeletesOldFile_OnlyAfterNewFileAndDbRowSucceed()
    {
        var document = SeedDocument("Doc", DocumentCategories.Reports, DocumentTestFixture.EmployeeUserId);
        document.FilePath = "pre-existing/old-path.pdf";
        await _fixture.Context.SaveChangesAsync();
        using var newContent = new MemoryStream(new byte[] { 9, 9, 9 });

        await _sut.ReplaceFileAsync(DocumentTestFixture.EmployeeUserId, document.DocumentId, newContent, "new.pdf", "application/pdf");

        Assert.Contains("pre-existing/old-path.pdf", _fileStorage.DeletedPaths);
    }

    [Fact]
    public async Task DeleteAsync_Allowed_ForOwner()
    {
        var document = SeedDocument("Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var success = await _sut.DeleteAsync(DocumentTestFixture.EmployeeUserId, document.DocumentId);

        Assert.True(success);
        Assert.Null(await _fixture.Context.Documents.FindAsync(document.DocumentId));
    }

    [Fact]
    public async Task DeleteAsync_Denied_ForTeamLead()
    {
        // FR-024 explicitly excludes Team Leads from delete rights, even over their own team's documents.
        var document = SeedDocument("Employee's Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var success = await _sut.DeleteAsync(DocumentTestFixture.TeamLeadUserId, document.DocumentId);

        Assert.False(success);
        Assert.NotNull(await _fixture.Context.Documents.FindAsync(document.DocumentId));
    }

    [Fact]
    public async Task DeleteAsync_Allowed_ForProjectManagerOfDocumentsProject()
    {
        var document = SeedDocument("Project Doc", DocumentCategories.ProjectDocuments, DocumentTestFixture.EmployeeUserId, projectId: DocumentTestFixture.ProjectId);

        var success = await _sut.DeleteAsync(DocumentTestFixture.ProjectManagerUserId, document.DocumentId);

        Assert.True(success);
    }

    [Fact]
    public async Task DeleteAsync_Allowed_ForAdministrator()
    {
        var document = SeedDocument("Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var success = await _sut.DeleteAsync(DocumentTestFixture.AdminUserId, document.DocumentId);

        Assert.True(success);
    }

    [Fact]
    public async Task DeleteAsync_Denied_ForUnrelatedUser()
    {
        var document = SeedDocument("Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var success = await _sut.DeleteAsync(DocumentTestFixture.OutsiderUserId, document.DocumentId);

        Assert.False(success);
    }

    [Fact]
    public async Task DeleteAsync_PreservesActivityLogWithTitleSnapshot_AfterDocumentRemoved()
    {
        var document = SeedDocument("Doc To Be Deleted", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);
        var documentId = document.DocumentId;

        await _sut.DeleteAsync(DocumentTestFixture.EmployeeUserId, documentId);

        var deleteLog = await _fixture.Context.DocumentActivityLogs
            .FirstOrDefaultAsync(l => l.Action == DocumentActivityType.Delete && l.DocumentTitleSnapshot == "Doc To Be Deleted");
        Assert.NotNull(deleteLog);
        Assert.Null(deleteLog!.DocumentId);
    }

    [Fact]
    public async Task DeleteAsync_DeletesUnderlyingFile()
    {
        var request = ValidRequest();
        var uploadResult = await _sut.UploadAsync(DocumentTestFixture.EmployeeUserId, request);

        await _sut.DeleteAsync(DocumentTestFixture.EmployeeUserId, uploadResult.DocumentId!.Value);

        Assert.Contains(_fileStorage.UploadedPaths[0], _fileStorage.DeletedPaths);
    }

    [Fact]
    public async Task ShareAsync_CreatesShare_ForIndividualUserTarget()
    {
        var document = SeedDocument("Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var success = await _sut.ShareAsync(DocumentTestFixture.EmployeeUserId, document.DocumentId,
            new ShareTarget { UserId = DocumentTestFixture.OutsiderUserId });

        Assert.True(success);
        var share = await _fixture.Context.DocumentShares.SingleOrDefaultAsync(s => s.DocumentId == document.DocumentId);
        Assert.NotNull(share);
        Assert.Equal(DocumentTestFixture.OutsiderUserId, share!.SharedWithUserId);
        Assert.Null(share.SharedWithDepartment);
        Assert.True(share.NotificationSent);
    }

    [Fact]
    public async Task ShareAsync_CreatesShare_ForDepartmentTarget()
    {
        var document = SeedDocument("Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var success = await _sut.ShareAsync(DocumentTestFixture.EmployeeUserId, document.DocumentId,
            new ShareTarget { Department = "Sales" });

        Assert.True(success);
        var share = await _fixture.Context.DocumentShares.SingleOrDefaultAsync(s => s.DocumentId == document.DocumentId);
        Assert.NotNull(share);
        Assert.Equal("Sales", share!.SharedWithDepartment);
        Assert.Null(share.SharedWithUserId);
    }

    [Fact]
    public async Task ShareAsync_Denied_WhenCallerIsNotOwner()
    {
        var document = SeedDocument("Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var success = await _sut.ShareAsync(DocumentTestFixture.OutsiderUserId, document.DocumentId,
            new ShareTarget { UserId = DocumentTestFixture.ProjectManagerUserId });

        Assert.False(success);
        Assert.Empty(_fixture.Context.DocumentShares);
    }

    [Fact]
    public async Task ShareAsync_Denied_WhenBothUserAndDepartmentSet()
    {
        var document = SeedDocument("Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var success = await _sut.ShareAsync(DocumentTestFixture.EmployeeUserId, document.DocumentId,
            new ShareTarget { UserId = DocumentTestFixture.OutsiderUserId, Department = "Sales" });

        Assert.False(success);
    }

    [Fact]
    public async Task ShareAsync_Denied_WhenNeitherUserNorDepartmentSet()
    {
        var document = SeedDocument("Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var success = await _sut.ShareAsync(DocumentTestFixture.EmployeeUserId, document.DocumentId, new ShareTarget());

        Assert.False(success);
    }

    [Fact]
    public async Task ShareAsync_SendsNotification_ToDirectRecipient()
    {
        var document = SeedDocument("Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        await _sut.ShareAsync(DocumentTestFixture.EmployeeUserId, document.DocumentId,
            new ShareTarget { UserId = DocumentTestFixture.OutsiderUserId });

        var notifications = await _fixture.Context.Notifications
            .Where(n => n.UserId == DocumentTestFixture.OutsiderUserId && n.Type == NotificationType.DocumentShared)
            .ToListAsync();
        Assert.Single(notifications);
    }

    [Fact]
    public async Task ShareAsync_SendsNotifications_ToAllDepartmentMembers_ExceptSharer()
    {
        // EmployeeUserId (4) shares with their own department "Engineering", which also contains
        // ProjectManagerUserId (2) and TeamLeadUserId (3).
        var document = SeedDocument("Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        await _sut.ShareAsync(DocumentTestFixture.EmployeeUserId, document.DocumentId,
            new ShareTarget { Department = "Engineering" });

        var notifiedUserIds = await _fixture.Context.Notifications
            .Where(n => n.Type == NotificationType.DocumentShared)
            .Select(n => n.UserId)
            .ToListAsync();

        Assert.Contains(DocumentTestFixture.ProjectManagerUserId, notifiedUserIds);
        Assert.Contains(DocumentTestFixture.TeamLeadUserId, notifiedUserIds);
        Assert.DoesNotContain(DocumentTestFixture.EmployeeUserId, notifiedUserIds);
        Assert.DoesNotContain(DocumentTestFixture.OutsiderUserId, notifiedUserIds);
    }

    [Fact]
    public async Task GetSharedWithMeAsync_ReturnsDirectlySharedDocuments()
    {
        var document = SeedDocument("Shared Directly", DocumentCategories.PersonalFiles, DocumentTestFixture.ProjectManagerUserId);
        await _sut.ShareAsync(DocumentTestFixture.ProjectManagerUserId, document.DocumentId,
            new ShareTarget { UserId = DocumentTestFixture.OutsiderUserId });

        var result = await _sut.GetSharedWithMeAsync(DocumentTestFixture.OutsiderUserId);

        Assert.Single(result);
        Assert.Equal("Shared Directly", result[0].Title);
    }

    [Fact]
    public async Task GetSharedWithMeAsync_ReturnsDepartmentSharedDocuments()
    {
        var document = SeedDocument("Shared With Sales", DocumentCategories.PersonalFiles, DocumentTestFixture.ProjectManagerUserId);
        await _sut.ShareAsync(DocumentTestFixture.ProjectManagerUserId, document.DocumentId,
            new ShareTarget { Department = "Sales" });

        var result = await _sut.GetSharedWithMeAsync(DocumentTestFixture.OutsiderUserId);

        Assert.Single(result);
        Assert.Equal("Shared With Sales", result[0].Title);
    }

    [Fact]
    public async Task GetSharedWithMeAsync_DoesNotReturnUnsharedDocuments()
    {
        SeedDocument("Not Shared", DocumentCategories.PersonalFiles, DocumentTestFixture.ProjectManagerUserId);

        var result = await _sut.GetSharedWithMeAsync(DocumentTestFixture.OutsiderUserId);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTaskDocumentsAsync_ReturnsDocuments_ForProjectMember()
    {
        var document = SeedDocument("Task Doc", DocumentCategories.ProjectDocuments, DocumentTestFixture.ProjectManagerUserId, projectId: DocumentTestFixture.ProjectId);
        document.TaskId = DocumentTestFixture.TaskId;
        await _fixture.Context.SaveChangesAsync();

        var result = await _sut.GetTaskDocumentsAsync(DocumentTestFixture.EmployeeUserId, DocumentTestFixture.TaskId);

        Assert.Single(result);
    }

    [Fact]
    public async Task GetTaskDocumentsAsync_ReturnsEmpty_ForUnrelatedUser()
    {
        var document = SeedDocument("Task Doc", DocumentCategories.ProjectDocuments, DocumentTestFixture.ProjectManagerUserId, projectId: DocumentTestFixture.ProjectId);
        document.TaskId = DocumentTestFixture.TaskId;
        await _fixture.Context.SaveChangesAsync();

        var result = await _sut.GetTaskDocumentsAsync(DocumentTestFixture.OutsiderUserId, DocumentTestFixture.TaskId);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTaskDocumentsAsync_ReturnsDocuments_ForAssignee_WhenTaskHasNoProject()
    {
        const int projectLessTaskId = 9001;
        _fixture.Context.Tasks.Add(new TaskItem
        {
            TaskId = projectLessTaskId,
            Title = "Standalone Task",
            Priority = TaskPriority.Medium,
            Status = ContosoDashboard.Models.TaskStatus.NotStarted,
            AssignedUserId = DocumentTestFixture.OutsiderUserId,
            CreatedByUserId = DocumentTestFixture.OutsiderUserId,
            ProjectId = null
        });
        await _fixture.Context.SaveChangesAsync();

        var document = SeedDocument("Standalone Task Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.OutsiderUserId);
        document.TaskId = projectLessTaskId;
        await _fixture.Context.SaveChangesAsync();

        var assigneeResult = await _sut.GetTaskDocumentsAsync(DocumentTestFixture.OutsiderUserId, projectLessTaskId);
        var unrelatedResult = await _sut.GetTaskDocumentsAsync(DocumentTestFixture.EmployeeUserId, projectLessTaskId);

        Assert.Single(assigneeResult);
        Assert.Empty(unrelatedResult);
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsMostRecentDocumentsForCaller_UpToRequestedCount()
    {
        SeedDocument("Oldest", DocumentCategories.Reports, DocumentTestFixture.EmployeeUserId, uploadDate: DateTime.UtcNow.AddDays(-3));
        SeedDocument("Middle", DocumentCategories.Reports, DocumentTestFixture.EmployeeUserId, uploadDate: DateTime.UtcNow.AddDays(-2));
        SeedDocument("Newest", DocumentCategories.Reports, DocumentTestFixture.EmployeeUserId, uploadDate: DateTime.UtcNow.AddDays(-1));

        var result = await _sut.GetRecentAsync(DocumentTestFixture.EmployeeUserId, count: 2);

        Assert.Equal(new[] { "Newest", "Middle" }, result.Select(d => d.Title));
    }

    [Fact]
    public async Task GetAccessibleDocumentCountAsync_CountsOwnedProjectAndSharedDocuments()
    {
        SeedDocument("Owned", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);
        SeedDocument("Project Doc", DocumentCategories.ProjectDocuments, DocumentTestFixture.ProjectManagerUserId, projectId: DocumentTestFixture.ProjectId);
        var sharedDoc = SeedDocument("Shared Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.ProjectManagerUserId);
        _fixture.Context.DocumentShares.Add(new DocumentShare
        {
            DocumentId = sharedDoc.DocumentId,
            SharedByUserId = DocumentTestFixture.ProjectManagerUserId,
            SharedWithUserId = DocumentTestFixture.EmployeeUserId
        });
        SeedDocument("Inaccessible", DocumentCategories.PersonalFiles, DocumentTestFixture.ProjectManagerUserId);
        await _fixture.Context.SaveChangesAsync();

        var count = await _sut.GetAccessibleDocumentCountAsync(DocumentTestFixture.EmployeeUserId);

        Assert.Equal(3, count); // Owned + Project Doc (member) + Shared Doc; not "Inaccessible"
    }

    [Fact]
    public async Task UploadAsync_AutoSetsProjectId_WhenTaskIdProvidedWithoutExplicitProjectId()
    {
        var request = ValidRequest();
        request.TaskId = DocumentTestFixture.TaskId; // seeded task belongs to DocumentTestFixture.ProjectId

        var result = await _sut.UploadAsync(DocumentTestFixture.EmployeeUserId, request);

        Assert.True(result.Success);
        var saved = await _fixture.Context.Documents.FindAsync(result.DocumentId);
        Assert.Equal(DocumentTestFixture.ProjectId, saved!.ProjectId);
        Assert.Equal(DocumentTestFixture.TaskId, saved.TaskId);
    }

    [Fact]
    public async Task UploadAsync_SendsDocumentAddedToProjectNotification_ToOtherProjectMembers()
    {
        var request = ValidRequest(projectId: DocumentTestFixture.ProjectId);

        // ProjectManagerUserId manages the project; EmployeeUserId uploads, so the PM should be notified.
        await _sut.UploadAsync(DocumentTestFixture.EmployeeUserId, request);

        var notifications = await _fixture.Context.Notifications
            .Where(n => n.Type == NotificationType.DocumentAddedToProject && n.UserId == DocumentTestFixture.ProjectManagerUserId)
            .ToListAsync();
        Assert.Single(notifications);
    }

    [Fact]
    public async Task AttachToTaskAsync_LinksDocumentToTask_AndInheritsTaskProject()
    {
        var document = SeedDocument("Standalone Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.EmployeeUserId);

        var success = await _sut.AttachToTaskAsync(DocumentTestFixture.EmployeeUserId, document.DocumentId, DocumentTestFixture.TaskId);

        Assert.True(success);
        var updated = await _fixture.Context.Documents.FindAsync(document.DocumentId);
        Assert.Equal(DocumentTestFixture.TaskId, updated!.TaskId);
        Assert.Equal(DocumentTestFixture.ProjectId, updated.ProjectId);
    }

    [Fact]
    public async Task AttachToTaskAsync_Denied_ForNonOwner()
    {
        var document = SeedDocument("Someone Else's Doc", DocumentCategories.PersonalFiles, DocumentTestFixture.ProjectManagerUserId);

        var success = await _sut.AttachToTaskAsync(DocumentTestFixture.EmployeeUserId, document.DocumentId, DocumentTestFixture.TaskId);

        Assert.False(success);
    }

    [Fact]
    public async Task AttachToTaskAsync_Denied_WhenDocumentBelongsToADifferentProject()
    {
        const int otherProjectId = 9002;
        _fixture.Context.Projects.Add(new Project
        {
            ProjectId = otherProjectId,
            Name = "Other Project",
            ProjectManagerId = DocumentTestFixture.ProjectManagerUserId
        });
        await _fixture.Context.SaveChangesAsync();

        var document = SeedDocument("Other Project Doc", DocumentCategories.ProjectDocuments, DocumentTestFixture.EmployeeUserId, projectId: otherProjectId);

        var success = await _sut.AttachToTaskAsync(DocumentTestFixture.EmployeeUserId, document.DocumentId, DocumentTestFixture.TaskId);

        Assert.False(success);
    }
}
