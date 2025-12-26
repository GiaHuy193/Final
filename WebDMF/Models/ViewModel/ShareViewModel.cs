using System.ComponentModel.DataAnnotations;

namespace WebDocumentManagement_FileSharing.Models.ViewModel
{
    public class ShareViewModel
    {
        public int DocumentId { get; set; }

        public string DocumentTitle { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        public AccessLevel AccessType { get; set; } = AccessLevel.Read;
    }
}
