namespace ContosoDashboard.Shared;

public record UploadedDocumentInfo(int DocumentId, string Title, string Category, string FileName, long FileSizeBytes, DateTime UploadDate, int? ProjectId);
