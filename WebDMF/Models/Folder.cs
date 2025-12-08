namespace WebDocumentManagement_FileSharing.Models
{
    public class Folder
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int? ParentFolderId { get; set; } // Để tạo thư mục lồng nhau
        public virtual Folder ParentFolder { get; set; }
    }
}
}
