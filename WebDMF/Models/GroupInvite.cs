using System;

namespace WebDocumentManagement_FileSharing.Models
{
    public enum InviteStatus { Pending = 0, Accepted = 1, Declined = 2 }

    public class GroupInvite
    {
        public int Id { get; set; }
        public int GroupId { get; set; }
        public string InviterId { get; set; } = string.Empty;
        public string? InviteeUserId { get; set; }
        public string InviteeEmail { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public InviteStatus Status { get; set; } = InviteStatus.Pending;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public virtual Group? Group { get; set; }
    }
}