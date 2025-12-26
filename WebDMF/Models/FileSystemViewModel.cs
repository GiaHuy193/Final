namespace WebDocumentManagement_FileSharing.Models
{
    public class FileSystemViewModel
    {
        public int? CurrentFolderId { get; set; }
        public ICollection<Folder> Folders { get; set; } = new List<Folder>();
        public ICollection<Document> Documents { get; set; } = new List<Document>();
    }
}
