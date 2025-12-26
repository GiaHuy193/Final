namespace WebDocumentManagement_FileSharing.Models
{
    public class Permission
    {
        public int Id { get; set; }
        public int? DocumentId { get; set; } // Quyền trên file
        public virtual Document? Document { get; set; }
        public int? FolderId { get; set; }   // Hoặc quyền trên cả thư mục
        public virtual Folder? Folder { get; set; }
        public string UserId { get; set; } = string.Empty;  // Cấp quyền cho ai

        public AccessLevel AccessType { get; set; } // "Read", "Write", "FullControl"
        public DateTime SharedDate { get; set; } = DateTime.Now;
    }
}
