using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Http;
using WebDocumentManagement_FileSharing.Helpers; // Đảm bảo đã có namespace này
using WebDocumentManagement_FileSharing.Data;
using Microsoft.EntityFrameworkCore;

namespace WebDocumentManagement_FileSharing.Areas.Identity.Pages.Account.Manage
{
    public class IndexModel : PageModel
    {
        private readonly UserManager<IdentityUser> _userManager;
        private readonly SignInManager<IdentityUser> _signInManager;
        private readonly ApplicationDbContext _db;

        public IndexModel(
            UserManager<IdentityUser> userManager,
            SignInManager<IdentityUser> signInManager,
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

        // bind file upload
        [BindProperty]
        public IFormFile AvatarUpload { get; set; }

        // avatar url to display in view
        public string AvatarUrl { get; set; }

        // --- CÁC THUỘC TÍNH MỚI CHO QUOTA ---
        public long UsedStorage { get; set; }
        public long TotalStorage { get; set; }
        public double StoragePercentage { get; set; }
        public string FormattedUsed { get; set; }
        public string FormattedTotal { get; set; }
        // ------------------------------------

        public class InputModel
        {
            [Phone]
            [Display(Name = "Số điện thoại")]
            public string PhoneNumber { get; set; }
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

        private async Task LoadAsync(IdentityUser user)
        {
            var userName = await _userManager.GetUserNameAsync(user);
            var phoneNumber = await _userManager.GetPhoneNumberAsync(user);

            Username = userName;

            Input = new InputModel
            {
                PhoneNumber = phoneNumber
            };

            // --- LOGIC TÍNH TOÁN DUNG LƯỢNG ---
            var email = await _userManager.GetEmailAsync(user);
            // Logic chuẩn hóa tên thư mục (phải khớp với logic trong Controllers)
            var folderName = "unknown";
            if (!string.IsNullOrEmpty(email))
            {
                var invalid = Path.GetInvalidFileNameChars();
                var cleaned = new string(email.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
                folderName = cleaned.Replace('@', '_').Replace(' ', '_').Trim();
            }
            else
            {
                var userId = await _userManager.GetUserIdAsync(user);
                var invalid = Path.GetInvalidFileNameChars();
                var cleaned = new string(userId.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
                folderName = cleaned.Replace('@', '_').Replace(' ', '_').Trim();
            }

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

            // Determine total storage from system settings. Default fallback: Standard 15GB
            var standardFallback = 15L * 1024 * 1024 * 1024; // 15 GB
            TotalStorage = await GetSystemQuotaBytesAsync("StandardQuota", standardFallback);

            // Tính phần trăm, tối đa là 100%
            StoragePercentage = Math.Min(100, (double)UsedStorage / TotalStorage * 100);

            // Sử dụng Helper để format số liệu cho đẹp (ví dụ: 120 MB)
            FormattedUsed = StorageHelper.FormatSize(UsedStorage);
            FormattedTotal = StorageHelper.FormatSize(TotalStorage);
            // ------------------------------------

            // --- Avatar URL: nếu có file avatar trong uploads/{user}/avatar.* thì dùng, không thì dùng ui-avatars ---
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
                AvatarUrl = avatarRelative;
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
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
            }

            // Handle avatar upload first if present
            if (AvatarUpload != null && AvatarUpload.Length > 0)
            {
                var email = await _userManager.GetEmailAsync(user);
                var folderName = "unknown";
                if (!string.IsNullOrEmpty(email))
                {
                    var invalid = Path.GetInvalidFileNameChars();
                    var cleaned = new string(email.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
                    folderName = cleaned.Replace('@', '_').Replace(' ', '_').Trim();
                }
                else
                {
                    var userId = await _userManager.GetUserIdAsync(user);
                    var invalid = Path.GetInvalidFileNameChars();
                    var cleaned = new string(userId.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
                    folderName = cleaned.Replace('@', '_').Replace(' ', '_').Trim();
                }

                var uploadsRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
                var userFolder = Path.Combine(uploadsRoot, folderName);
                if (!Directory.Exists(userFolder)) Directory.CreateDirectory(userFolder);

                var ext = Path.GetExtension(AvatarUpload.FileName).ToLowerInvariant();
                var allowed = new[] { ".png", ".jpg", ".jpeg", ".gif" };
                if (!allowed.Contains(ext))
                {
                    StatusMessage = "Định dạng ảnh không được hỗ trợ. Vui lòng chọn PNG/JPG/GIF.";
                    await LoadAsync(user);
                    return Page();
                }

                // remove existing avatar files with other extensions
                foreach (var e in allowed)
                {
                    var exist = Path.Combine(userFolder, "avatar" + e);
                    try
                    {
                        if (System.IO.File.Exists(exist)) System.IO.File.Delete(exist);
                    }
                    catch { }
                }

                var dest = Path.Combine(userFolder, "avatar" + ext);
                try
                {
                    using (var fs = new FileStream(dest, FileMode.Create))
                    {
                        await AvatarUpload.CopyToAsync(fs);
                    }

                    StatusMessage = "Ảnh đại diện đã được cập nhật.";
                }
                catch
                {
                    StatusMessage = "Không thể lưu ảnh đại diện. Vui lòng thử lại.";
                    await LoadAsync(user);
                    return Page();
                }
            }

            if (!ModelState.IsValid)
            {
                await LoadAsync(user);
                return Page();
            }

            var phoneNumber = await _userManager.GetPhoneNumberAsync(user);
            if (Input.PhoneNumber != phoneNumber)
            {
                var setPhoneResult = await _userManager.SetPhoneNumberAsync(user, Input.PhoneNumber);
                if (!setPhoneResult.Succeeded)
                {
                    StatusMessage = "Unexpected error when trying to set phone number.";
                    return RedirectToPage();
                }
            }

            await _signInManager.RefreshSignInAsync(user);
            if (string.IsNullOrEmpty(StatusMessage)) StatusMessage = "Hồ sơ của bạn đã được cập nhật";
            return RedirectToPage();
        }
    }
}