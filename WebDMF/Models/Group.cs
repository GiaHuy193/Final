using System;
using System.Collections.Generic;

namespace WebDocumentManagement_FileSharing.Models
{
    public class Group
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

        public virtual ICollection<GroupMember> Members { get; set; } = new List<GroupMember>();
    }
}
