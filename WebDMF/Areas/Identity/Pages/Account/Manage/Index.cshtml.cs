using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Http;
using WebDocumentManagement_FileSharing.Helpers;
using WebDocumentManagement_FileSharing.Data;
using Microsoft.EntityFrameworkCore;
using WebDocumentManagement_FileSharing.Models;

namespace WebDocumentManagement_FileSharing.Areas.Identity.Pages.Account.Manage
{
    public class IndexModel : PageModel
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly ApplicationDbContext _db;

        public IndexModel(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ApplicationDbContext db)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _db = db;
        }

        public string Username { get; set; }

        [TempData]
        public string StatusMessage { get; set; }

        [BindProperty]
        public InputModel Input { get; set; }

        [BindProperty]
        public IFormFile AvatarUpload { get; set; }

        public string AvatarUrl { get; set; }

        // Các thuộc tính hiển thị Quota
        public long UsedStorage { get; set; }
        public long TotalStorage { get; set; }
        public double StoragePercentage { get; set; }
        public string FormattedUsed { get; set; }
        public string FormattedTotal { get; set; }
        public bool IsPremium { get; set; }

        public class InputModel
        {
            [Phone]
            [Display(Name = "Số điện thoại")]
            public string PhoneNumber { get; set; }

            [Display(Name = "Giới tính")]
            public string Gender { get; set; }

            [DataType(DataType.Date)]
            [Display(Name = "Ngày sinh")]
            public DateTime? DateOfBirth { get; set; }
        }

        private async Task<long> GetSystemQuotaBytesAsync(string key, long fallback)
        {
            try
            {
                var s = await _db.SystemSettings.FirstOrDefaultAsync(x => x.SettingKey == key);
                if (s != null && long.TryParse(s.SettingValue, out var v)) return v;
            }
            catch { }
            return fallback;
        }

        // --- HÀM MỚI: TẠO TÊN THƯ MỤC CHUẨN (Dùng chung để tránh lệch) ---
        private async Task<string> GetUserFolderNameAsync(ApplicationUser user)
        {
            var email = await _userManager.GetEmailAsync(user);
            string rawName;

            if (!string.IsNullOrEmpty(email))
            {
                rawName = email;
            }
            else
            {
                rawName = await _userManager.GetUserIdAsync(user);
            }

            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(rawName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            // Thêm ToLowerInvariant() để đảm bảo tên thư mục luôn là chữ thường
            return cleaned.Replace('@', '_').Replace(' ', '_').Trim().ToLowerInvariant();
        }
        // ----------------------------------------------------------------

        private async Task LoadAsync(ApplicationUser user)
        {
            var userName = await _userManager.GetUserNameAsync(user);
            var phoneNumber = await _userManager.GetPhoneNumberAsync(user);

            Username = userName;
            IsPremium = user.IsPremium;

            Input = new InputModel
            {
                PhoneNumber = phoneNumber,
                Gender = user.Gender,
                DateOfBirth = user.DateOfBirth
            };

            // 1. Lấy tên thư mục chuẩn
            var folderName = await GetUserFolderNameAsync(user);

            var userPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", folderName);
            long usedOnDisk = 0;
            if (Directory.Exists(userPath))
            {
                try
                {
                    usedOnDisk = new DirectoryInfo(userPath).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                }
                catch { usedOnDisk = 0; }
            }
            UsedStorage = usedOnDisk;

            long standardBytes = 16106127360;
            long premiumBytes = 107374182400;

            standardBytes = await GetSystemQuotaBytesAsync("StandardQuota", standardBytes);
            premiumBytes = await GetSystemQuotaBytesAsync("PremiumQuota", premiumBytes);

            TotalStorage = user.IsPremium ? premiumBytes : standardBytes;
            StoragePercentage = TotalStorage > 0 ? Math.Min(100, (double)UsedStorage / TotalStorage * 100) : 0;

            FormattedUsed = StorageHelper.FormatSize(UsedStorage);
            FormattedTotal = StorageHelper.FormatSize(TotalStorage);

            string avatarRelative = null;
            var allowedExts = new[] { ".png", ".jpg", ".jpeg", ".gif" };
            if (Directory.Exists(userPath))
            {
                foreach (var ext in allowedExts)
                {
                    var p = Path.Combine(userPath, "avatar" + ext);
                    if (System.IO.File.Exists(p))
                    {
                        avatarRelative = "/uploads/" + folderName + "/avatar" + ext;
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(avatarRelative))
            {
                // Thêm ticks để chống cache
                AvatarUrl = avatarRelative + "?v=" + DateTime.Now.Ticks;
            }
            else
            {
                AvatarUrl = $"https://ui-avatars.com/api/?name={Uri.EscapeDataString(userName)}&background=0D6EFD&color=fff&size=128&bold=true";
            }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
            }

            await LoadAsync(user);
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var userId = _userManager.GetUserId(User);
            if (userId == null) return NotFound("Không tìm thấy User ID.");

            // Lấy User trực tiếp từ DB để tracking
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return NotFound("User không tồn tại trong DB.");

            // --- XỬ LÝ UPLOAD AVATAR ---
            if (AvatarUpload != null && AvatarUpload.Length > 0)
            {
                // 2. Dùng hàm chung để lấy tên thư mục chuẩn
                var folderName = await GetUserFolderNameAsync(user);

                var uploadsRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
                var userFolder = Path.Combine(uploadsRoot, folderName);
                if (!Directory.Exists(userFolder)) Directory.CreateDirectory(userFolder);

                var ext = Path.GetExtension(AvatarUpload.FileName).ToLowerInvariant();
                var allowed = new[] { ".png", ".jpg", ".jpeg", ".gif" };
                if (!allowed.Contains(ext))
                {
                    StatusMessage = "Định dạng ảnh không hỗ trợ. Chỉ chấp nhận PNG/JPG/GIF.";
                    await LoadAsync(user);
                    return Page();
                }

                // Xóa ảnh cũ
                foreach (var e in allowed)
                {
                    var exist = Path.Combine(userFolder, "avatar" + e);
                    try { if (System.IO.File.Exists(exist)) System.IO.File.Delete(exist); } catch { }
                }

                var dest = Path.Combine(userFolder, "avatar" + ext);
                try
                {
                    using (var fs = new FileStream(dest, FileMode.Create))
                    {
                        await AvatarUpload.CopyToAsync(fs);
                    }
                    // Không gán StatusMessage vội để ưu tiên thông báo lưu thành công ở dưới
                }
                catch
                {
                    StatusMessage = "Lỗi: Không thể lưu tệp ảnh lên server.";
                    await LoadAsync(user);
                    return Page();
                }
            }

            if (!ModelState.IsValid)
            {
                await LoadAsync(user);
                return Page();
            }

            try
            {
                // --- CẬP NHẬT DỮ LIỆU ---
                user.PhoneNumber = Input.PhoneNumber;
                user.Gender = Input.Gender;
                user.DateOfBirth = Input.DateOfBirth;

                // Đánh dấu là đã sửa và lưu
                _db.Update(user);
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = "Lỗi Database: " + ex.Message;
                await LoadAsync(user);
                return Page();
            }

            // Refresh Cookie
            await _signInManager.RefreshSignInAsync(user);

            StatusMessage = "Hồ sơ của bạn đã được cập nhật thành công.";
            // Redirect để load lại trang sạch sẽ và hiển thị ảnh mới
            return RedirectToPage();
        }
    }
}