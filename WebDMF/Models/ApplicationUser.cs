using Microsoft.AspNetCore.Identity;
using System;

namespace WebDocumentManagement_FileSharing.Models
{
    // Kế thừa IdentityUser để có sẵn các tính năng đăng nhập/mật khẩu cơ bản
    public class ApplicationUser : IdentityUser
    {
        // --- NHÓM 1: GÓI PREMIUM ---
        public bool IsPremium { get; set; } = false; // Mặc định là chưa mua
        public DateTime? PremiumUntil { get; set; }  // Ngày hết hạn VIP

        // --- NHÓM 2: THÔNG TIN CÁ NHÂN (PROFILE) ---
        public string? Gender { get; set; }         // Giới tính (Nam/Nữ)
        public DateTime? DateOfBirth { get; set; }  // Ngày sinh

        // *Lưu ý: Số điện thoại (PhoneNumber) đã có sẵn trong IdentityUser nên không cần viết lại.
    }
}