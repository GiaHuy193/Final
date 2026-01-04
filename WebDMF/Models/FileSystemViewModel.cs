namespace WebDocumentManagement_FileSharing.Models
{
    public class FileSystemViewModel
    {
        public int? CurrentFolderId { get; set; }
        public ICollection<Folder> Folders { get; set; } = new List<Folder>();
        public ICollection<Document> Documents { get; set; } = new List<Document>();

        // maps to effective access for the current user when viewing listed items
        public Dictionary<int, AccessLevel?> DocumentAccess { get; set; } = new Dictionary<int, AccessLevel?>();
        public Dictionary<int, AccessLevel?> FolderAccess { get; set; } = new Dictionary<int, AccessLevel?>();

        // effective access for the current folder being viewed
        public AccessLevel? CurrentFolderAccess { get; set; }
    }
}
