using System;

namespace WebDocumentManagement_FileSharing.Models
{
    public class ShareLink
    {
        public int Id { get; set; }
        public string Token { get; set; } = string.Empty; // short unique token
        public string TargetType { get; set; } = "document"; // "document" or "folder"
        public int TargetId { get; set; }
        public AccessLevel AccessType { get; set; } = AccessLevel.Read;
        public bool IsPublic { get; set; } = true; // true = anyone with link, false = restricted
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
