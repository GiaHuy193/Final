using System;
using System.Collections.Generic;
using System.IO;
using WebDocumentManagement_FileSharing.Models;

namespace WebDocumentManagement_FileSharing.Helpers
{
    public static class FilePreviewHelper
    {
        private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif" };
        private static readonly HashSet<string> PdfExt = new(StringComparer.OrdinalIgnoreCase) { ".pdf" };
        private static readonly HashSet<string> OfficeExt = new(StringComparer.OrdinalIgnoreCase) { ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx" };

        public static FilePreviewType GetPreviewType(string fileName, string? contentType = null)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return FilePreviewType.NotSupported;
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ImageExt.Contains(ext)) return FilePreviewType.Image;
            if (PdfExt.Contains(ext)) return FilePreviewType.Pdf;
            if (OfficeExt.Contains(ext)) return FilePreviewType.Office;

            if (!string.IsNullOrWhiteSpace(contentType))
            {
                var ct = contentType.ToLowerInvariant();
                if (ct.StartsWith("image/")) return FilePreviewType.Image;
                if (ct == "application/pdf") return FilePreviewType.Pdf;
                if (ct.Contains("word") || ct.Contains("officedocument") || ct.Contains("excel") || ct.Contains("presentation")) return FilePreviewType.Office;
            }

            return FilePreviewType.NotSupported;
        }
    }
}
