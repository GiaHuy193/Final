using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace WebDocumentManagement_FileSharing.Models
{
    public class PaymentTransaction
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string OrderId { get; set; } = string.Empty; // Mã đơn hàng từ PayPal (VD: 5KW...)

        public string TransactionId { get; set; } = string.Empty; // Mã giao dịch (Capture ID)

        [Required]
        public string UserId { get; set; } = string.Empty; // Người thanh toán

        [ForeignKey("UserId")]
        public virtual ApplicationUser? User { get; set; } // Liên kết với bảng User (IdentityUser used in this project)

        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; } // Số tiền

        public string Currency { get; set; } = "USD"; // Tiền tệ (PayPal Sandbox dùng USD)

        public string PaymentMethod { get; set; } = "PayPal"; // Cổng thanh toán

        public string Status { get; set; } = string.Empty; // Trạng thái: Success, Failed, Pending

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    }
}