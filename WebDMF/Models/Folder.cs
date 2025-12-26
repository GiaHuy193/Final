namespace WebDocumentManagement_FileSharing.Models;
using System.Collections.Generic;

    public class Folder
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTime CreatedDate { get; set; } = DateTime.Now;

        public int? ParentId { get; set; } // Để tạo thư mục lồng nhau
        public virtual Folder? ParentFolder { get; set; }

        public virtual ICollection<Folder> SubFolders { get; set; } = new List<Folder>();
        public virtual ICollection<Document> Documents { get; set; } = new List<Document>();

        // Standardize owner property name
        public string OwnerId { get; set; } = string.Empty; // ID của người sở hữu thư mục
        public bool IsDeleted { get; set; } // Dùng cho chức năng Soft Delete (Thùng rác)
        public bool IsStarred { get; set; }
    }


