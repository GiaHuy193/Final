namespace WebDocumentManagement_FileSharing.Models;
using System.Collections.Generic;

public class Document
{
     public int Id { get; set; }
     public string Title { get; set; } = string.Empty;
     public string FileName { get; set; } = string.Empty; // Tên file thực tế trên ổ cứng
     public string Extension { get; set; } = string.Empty; // .pdf, .docx...
     public long FileSize { get; set; }
     public string ContentType { get; set; } = string.Empty;
     public string FilePath { get; set; } = string.Empty; // Ví dụ: /uploads/2023/file-guid.pdf
     public DateTime UploadedDate { get; set; } = DateTime.Now;


     // Khóa ngoại tới Folder
    public int? FolderId { get; set; }
     public virtual Folder? Folder { get; set; }

     // Versions navigation for DocumentVersion
     public virtual ICollection<DocumentVersion> Versions { get; set; } = new List<DocumentVersion>();


     public string OwnerId { get; set; } = string.Empty;
     public bool IsShared { get; set; }
     public bool IsStarred { get; set; }
     public bool IsDeleted { get; set; } // Dùng cho chức năng Soft Delete (Thùng rác)
}
