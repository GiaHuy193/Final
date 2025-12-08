namespace WebDocumentManagement_FileSharing.Models
{
    public class Document
    {
        public int Id { get; set; }
        public string Name { get; set; } // Tên tệp
        public string FilePath { get; set; } // Đường dẫn lưu trên server
        public long Size { get; set; } // Dung lượng
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public string Category { get; set; } 
    }
}
