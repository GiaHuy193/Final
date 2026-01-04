namespace WebDocumentManagement_FileSharing.Helpers
{
    public static class StorageHelper
    {
        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb:0.##} KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:0.##} MB";
            double gb = mb / 1024.0;
            return $"{gb:0.##} GB";
        }
    }
}
