using System;

namespace WebDocumentManagement_FileSharing.Models
{
    public class GroupMember
    {
        public int Id { get; set; }
        public int GroupId { get; set; }
        public string UserId { get; set; } = string.Empty;
        public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

        public virtual Group? Group { get; set; }
    }
}
