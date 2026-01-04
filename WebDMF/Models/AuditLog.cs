using System;

namespace WebDocumentManagement_FileSharing.Models
{
    public class AuditLog
    {
        public int Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string ActorId { get; set; }
        public string Actor { get; set; }
        public string Action { get; set; } // e.g., DeleteFolder, DeleteDocument, Share, Star, CONFIG_UPDATE
        public string TargetType { get; set; } // Folder, Document, SystemSetting, Permission
        public int? TargetId { get; set; }
        public string TargetName { get; set; }
        public string Details { get; set; }
    }
}
