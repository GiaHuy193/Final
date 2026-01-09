using System.Collections.Generic;
using WebDocumentManagement_FileSharing.Models;

namespace WebDocumentManagement_FileSharing.Models.ViewModel
{
    public class SharedListingsViewModel
    {
        public List<Permission> SharedWithMe { get; set; } = new List<Permission>();
        public List<Permission> SharedByMe { get; set; } = new List<Permission>();
    }
}
