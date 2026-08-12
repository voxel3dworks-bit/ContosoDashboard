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
}
