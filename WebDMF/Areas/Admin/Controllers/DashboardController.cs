using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebDocumentManagement_FileSharing.Data;
using WebDocumentManagement_FileSharing.Helpers;
using WebDocumentManagement_FileSharing.Models;
using System.Collections.Generic;

namespace WebDocumentManagement_FileSharing.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class DashboardController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;

        public DashboardController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
        {
            _context = context;
            _userManager = userManager;
            _roleManager = roleManager;
        }

        public async Task<IActionResult> Index()
        {
            // 1. Thống kê tổng số người dùng (bao gồm cả Admin)
            ViewBag.TotalUsers = await _userManager.Users.CountAsync();

            // 2. Thống kê tệp tin và dung lượng tổng
            long totalBytes = 0;
            int totalFiles = 0;
            int totalFolders = 0;

            try
            {
                if (_context.Documents != null && await _context.Documents.AnyAsync())
                {
                    totalFiles = await _context.Documents.CountAsync();
                    totalBytes = await _context.Documents.SumAsync(f => f.FileSize);
                }

                if (_context.Folders != null && await _context.Folders.AnyAsync())
                {
                    totalFolders = await _context.Folders.CountAsync();
                }
            }
            catch (Exception)
            {
                totalFiles = 0;
                totalBytes = 0;
                totalFolders = 0;
            }

            ViewBag.TotalFiles = totalFiles;
            ViewBag.TotalFolders = totalFolders;

            // Expose total bytes and a human-readable formatted string
            ViewBag.TotalStorageBytes = totalBytes; // raw bytes
            ViewBag.TotalStorage = StorageHelper.FormatSize(totalBytes); // human-friendly (e.g., 1.2 GB)

            // 3. Xử lý danh sách người dùng mới (Ẩn Admin để tránh khóa nhầm)
            var admins = await _userManager.GetUsersInRoleAsync("Admin");
            var adminIds = admins.Select(a => a.Id).ToList();

            var latestUsers = await _userManager.Users
                .Where(u => !adminIds.Contains(u.Id))
                .OrderByDescending(u => u.Id)
                .Take(10)
                .ToListAsync();

            // 4. Tính dung lượng mỗi người dùng đã upload (group by OwnerId)
            var userIds = latestUsers.Select(u => u.Id).ToList();
            var userStorage = new Dictionary<string, string>();
            var userStorageBytes = new Dictionary<string, long>();

            if (userIds.Any() && _context.Documents != null)
            {
                var groups = await _context.Documents
                    .Where(d => userIds.Contains(d.OwnerId))
                    .GroupBy(d => d.OwnerId)
                    .Select(g => new { UserId = g.Key, TotalBytes = g.Sum(x => x.FileSize) })
                    .ToListAsync();

                foreach (var u in latestUsers)
                {
                    var g = groups.FirstOrDefault(x => x.UserId == u.Id);
                    var bytes = g?.TotalBytes ?? 0L;
                    userStorageBytes[u.Id] = bytes;
                    userStorage[u.Id] = StorageHelper.FormatSize(bytes);
                }
            }
            else
            {
                // initialize zero values
                foreach (var u in latestUsers)
                {
                    userStorageBytes[u.Id] = 0L;
                    userStorage[u.Id] = StorageHelper.FormatSize(0);
                }
            }

            ViewBag.UserStorage = userStorage; // map userId -> formatted size
            ViewBag.UserStorageBytes = userStorageBytes; // map userId -> bytes

            return View(latestUsers);
        }

        // New: User management listing (all users)
        public async Task<IActionResult> ManageUsers()
        {
            // Exclude users who are in the Admin role from the management list
            var admins = await _userManager.GetUsersInRoleAsync("Admin");
            var adminIds = admins.Select(a => a.Id).ToList();

            var users = await _userManager.Users
                .Where(u => !adminIds.Contains(u.Id))
                .OrderBy(u => u.UserName)
                .ToListAsync();

             // compute storage per user
             var userIds = users.Select(u => u.Id).ToList();
             var storageMap = new Dictionary<string, long>();
             if (userIds.Any() && _context.Documents != null)
             {
                 var groups = await _context.Documents
                     .Where(d => userIds.Contains(d.OwnerId))
                     .GroupBy(d => d.OwnerId)
                     .Select(g => new { UserId = g.Key, TotalBytes = g.Sum(x => x.FileSize) })
                     .ToListAsync();

                 foreach (var u in users)
                 {
                     var g = groups.FirstOrDefault(x => x.UserId == u.Id);
                     storageMap[u.Id] = g?.TotalBytes ?? 0L;
                 }
             }
             else
             {
                 foreach (var u in users) storageMap[u.Id] = 0L;
             }

             // prepare viewmodel via ViewBag
             ViewBag.UserStorageBytes = storageMap;

             // get role membership for 'Pro' and 'Premium' and derive tier
             var tierMap = new Dictionary<string, string>();
             foreach (var u in users)
             {
                 var isPro = await _userManager.IsInRoleAsync(u, "Pro");
                 var isPremium = await _userManager.IsInRoleAsync(u, "Premium");
                 if (isPremium) tierMap[u.Id] = "Premium";
                 else if (isPro) tierMap[u.Id] = "Pro";
                 else tierMap[u.Id] = "Standard";
             }
             ViewBag.UserTier = tierMap;

             // get role membership for 'Pro'
             var proMap = new Dictionary<string, bool>();
             foreach (var u in users)
             {
                 proMap[u.Id] = await _userManager.IsInRoleAsync(u, "Pro");
             }
             ViewBag.IsPro = proMap;

             // The ManageUsers view was moved to Areas/Admin/Views/Users/ManageUsers.cshtml
             // Return by explicit view path so MVC can locate it.
             return View("~/Areas/Admin/Views/Users/ManageUsers.cshtml", users);
         }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetUserTier(string userId, string tier)
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(tier)) return RedirectToAction("ManageUsers");
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null) return RedirectToAction("ManageUsers");

            // Ensure roles exist
            if (!await _roleManager.RoleExistsAsync("Pro")) await _roleManager.CreateAsync(new IdentityRole("Pro"));
            if (!await _roleManager.RoleExistsAsync("Premium")) await _roleManager.CreateAsync(new IdentityRole("Premium"));

            tier = tier.Trim();
            var previousRoles = new List<string>();
            if (await _userManager.IsInRoleAsync(user, "Pro")) previousRoles.Add("Pro");
            if (await _userManager.IsInRoleAsync(user, "Premium")) previousRoles.Add("Premium");

            if (tier.Equals("Standard", System.StringComparison.OrdinalIgnoreCase))
            {
                if (await _userManager.IsInRoleAsync(user, "Pro")) await _userManager.RemoveFromRoleAsync(user, "Pro");
                if (await _userManager.IsInRoleAsync(user, "Premium")) await _userManager.RemoveFromRoleAsync(user, "Premium");
                TempData["Success"] = $"Đã đặt tài khoản {user.Email} về Standard.";
            }
            else if (tier.Equals("Pro", System.StringComparison.OrdinalIgnoreCase))
            {
                if (await _userManager.IsInRoleAsync(user, "Premium")) await _userManager.RemoveFromRoleAsync(user, "Premium");
                if (!await _userManager.IsInRoleAsync(user, "Pro")) await _userManager.AddToRoleAsync(user, "Pro");
                TempData["Success"] = $"Đã nâng cấp tài khoản {user.Email} lên Pro.";
            }
            else if (tier.Equals("Premium", System.StringComparison.OrdinalIgnoreCase))
            {
                if (await _userManager.IsInRoleAsync(user, "Pro")) await _userManager.RemoveFromRoleAsync(user, "Pro");
                if (!await _userManager.IsInRoleAsync(user, "Premium")) await _userManager.AddToRoleAsync(user, "Premium");
                TempData["Success"] = $"Đã nâng cấp tài khoản {user.Email} lên Premium.";
            }

            // audit: record tier change
            await AuditHelper.LogAsync(HttpContext, "UPDATE_TIER", "User", null, user.Email, $"Changed roles from [{string.Join(',', previousRoles)}] to '{tier}'");

            return RedirectToAction("ManageUsers");
        }

        // Action xử lý khóa tài khoản người dùng
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LockUser(string userId)
        {
            // Lấy ID của Admin hiện tại đang thực hiện thao tác
            var currentAdminId = _userManager.GetUserId(User);

            // Chống "tự hủy": Không cho phép tự khóa chính mình
            if (userId == currentAdminId)
            {
                TempData["Error"] = "Hệ thống đã ngăn chặn hành vi tự khóa tài khoản Admin!";
                return RedirectToAction(nameof(Index));
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user != null)
            {
                // Kiểm tra thêm lần nữa xem User định khóa có phải Admin khác không (Bảo vệ đồng nghiệp)
                if (await _userManager.IsInRoleAsync(user, "Admin"))
                {
                    TempData["Error"] = "Không thể khóa tài khoản có quyền Quản trị viên!";
                    return RedirectToAction(nameof(Index));
                }

                // Thực hiện khóa tài khoản (ở đây ví dụ khóa 100 năm)
                await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddYears(100));
                TempData["Success"] = $"Đã khóa tài khoản {user.Email} thành công.";

                await AuditHelper.LogAsync(HttpContext, "LOCK_USER", "User", null, user.Email, $"Locked user account {user.Email}");
            }

            return RedirectToAction(nameof(Index));
        }

        // Action mở khóa tài khoản
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UnlockUser(string userId)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user != null)
            {
                await _userManager.SetLockoutEndDateAsync(user, null);
                TempData["Success"] = $"Đã mở khóa tài khoản {user.Email}.";
                await AuditHelper.LogAsync(HttpContext, "UNLOCK_USER", "User", null, user.Email, $"Unlocked user account {user.Email}");
            }
            return RedirectToAction(nameof(Index));
        }
    }
}