using System;

namespace WebDocumentManagement_FileSharing.Models
{
    public class GroupShare
    {
        public int Id { get; set; }
        public int GroupId { get; set; }
        public int? DocumentId { get; set; }
        public int? FolderId { get; set; }
        public AccessLevel AccessType { get; set; }
        public DateTime SharedDate { get; set; } = DateTime.UtcNow;
    }
}
