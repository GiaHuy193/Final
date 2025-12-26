namespace WebDocumentManagement_FileSharing.Models
{
    public class DocumentVersion
    {
        public int Id { get; set; }
        public int DocumentId { get; set; }
        public virtual Document Document { get; set; } = null!;

        public int VersionNumber { get; set; } // 1, 2, 3...
        public string FileName { get; set; } = string.Empty; // Tên file phiên bản này
        public string FilePath { get; set; } = string.Empty; // Đường dẫn tới file phiên bản này
        public string? ChangeNote { get; set; } // Ghi chú thay đổi
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public bool IsDeleted { get; set; } // Thêm để quản lý đồng bộ
    }
}
