using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ContosoDashboard.Models;

public class DocumentActivityLog
{
    [Key]
    public int DocumentActivityLogId { get; set; }

    public int? DocumentId { get; set; }

    [MaxLength(255)]
    public string? DocumentTitleSnapshot { get; set; }

    [Required]
    public int UserId { get; set; }

    [Required]
    public DocumentActivityType Action { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [ForeignKey("DocumentId")]
    public virtual Document? Document { get; set; }

    [ForeignKey("UserId")]
    public virtual User User { get; set; } = null!;
}

public enum DocumentActivityType
{
    Upload,
    Download,
    Delete,
    Share,
    MetadataEdit,
    FileReplace
}
