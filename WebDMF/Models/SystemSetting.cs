using System.ComponentModel.DataAnnotations;
namespace WebDocumentManagement_FileSharing.Models
{
    public class SystemSetting
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string SettingKey { get; set; } // Ví dụ: "AllowedExtensions"

        [Required]
        public string SettingValue { get; set; } // Ví dụ: ".pdf,.docx"

        public string Description { get; set; } // Mô tả để Admin dễ hiểu
    }
}
