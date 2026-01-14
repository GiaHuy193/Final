using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebDocumentManagement_FileSharing.Data;
using WebDocumentManagement_FileSharing.Helpers;
using WebDocumentManagement_FileSharing.Models;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

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
            ViewBag.AdminCount = admins?.Count ?? 0;

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

            // Add file type distribution and top downloads
            try
            {
                if (_context.Documents != null && await _context.Documents.AnyAsync())
                {
                    var types = await _context.Documents
                        .GroupBy(d => (string.IsNullOrEmpty(d.Extension) ? "(no ext)" : d.Extension.ToLower()))
                        .Select(g => new { Ext = g.Key, Count = g.Count() })
                        .OrderByDescending(x => x.Count)
                        .ToListAsync();

                    var totalDocs = types.Sum(x => x.Count);
                    ViewBag.FileTypeLabels = types.Select(x => x.Ext).ToList();
                    ViewBag.FileTypeValues = types.Select(x => x.Count).ToList();
                    ViewBag.FileTypeTotal = totalDocs;
                }
                else
                {
                    ViewBag.FileTypeLabels = new List<string>();
                    ViewBag.FileTypeValues = new List<int>();
                    ViewBag.FileTypeTotal = 0;
                }

                // role based counts for package distribution
                var premium = await _userManager.GetUsersInRoleAsync("Premium");
                var pro = await _userManager.GetUsersInRoleAsync("Pro");
                var adminRoleUsers = await _userManager.GetUsersInRoleAsync("Admin");
                var totalUsers = await _userManager.Users.CountAsync();
                int premiumCount = premium.Count;
                int proCount = pro.Count;
                int adminCount = adminRoleUsers.Count;
                int standardCount = Math.Max(0, (int)totalUsers - premiumCount - proCount - adminCount);
                ViewBag.PremiumCount = premiumCount;
                ViewBag.ProCount = proCount;
                ViewBag.StandardCount = standardCount;

                // top downloaded files from AuditLogs (Action contains DOWNLOAD)
                var topDownloads = new List<KeyValuePair<string, int>>();
                if (_context.AuditLogs != null && await _context.AuditLogs.AnyAsync(a => a.Action != null && a.Action.ToUpper().Contains("DOWNLOAD") && a.TargetId != null))
                {
                    var grouped = await _context.AuditLogs
                        .Where(a => a.Action != null && a.Action.ToUpper().Contains("DOWNLOAD") && a.TargetId != null)
                        .GroupBy(a => a.TargetId)
                        .Select(g => new { TargetId = g.Key, Count = g.Count() })
                        .OrderByDescending(x => x.Count)
                        .Take(5)
                        .ToListAsync();

                    foreach (var g in grouped)
                    {
                        string name = null;
                        if (g.TargetId.HasValue)
                        {
                            var doc = await _context.Documents.FindAsync(g.TargetId.Value);
                            if (doc != null) name = doc.FileName;
                        }
                        if (string.IsNullOrEmpty(name))
                        {
                            name = await _context.AuditLogs.Where(a => a.TargetId == g.TargetId).Select(a => a.TargetName).FirstOrDefaultAsync() ?? (g.TargetId.HasValue ? "Id:" + g.TargetId.Value.ToString() : "Unknown");
                        }
                        topDownloads.Add(new KeyValuePair<string, int>(name, g.Count));
                    }
                }
                ViewBag.TopFiles = topDownloads;

                // top accessed files (views) from AuditLogs (Action contains VIEW or OPEN)
                var topAccessed = new List<KeyValuePair<string, int>>();
                if (_context.AuditLogs != null && await _context.AuditLogs.AnyAsync(a => a.Action != null && (a.Action.ToUpper().Contains("VIEW") || a.Action.ToUpper().Contains("OPEN")) && a.TargetId != null))
                {
                    var groupedViews = await _context.AuditLogs
                        .Where(a => a.Action != null && (a.Action.ToUpper().Contains("VIEW") || a.Action.ToUpper().Contains("OPEN")) && a.TargetId != null)
                        .GroupBy(a => a.TargetId)
                        .Select(g => new { TargetId = g.Key, Count = g.Count() })
                        .OrderByDescending(x => x.Count)
                        .Take(5)
                        .ToListAsync();

                    foreach (var g in groupedViews)
                    {
                        string name = null;
                        if (g.TargetId.HasValue)
                        {
                            var doc = await _context.Documents.FindAsync(g.TargetId.Value);
                            if (doc != null) name = doc.FileName;
                        }
                        if (string.IsNullOrEmpty(name))
                        {
                            name = await _context.AuditLogs.Where(a => a.TargetId == g.TargetId).Select(a => a.TargetName).FirstOrDefaultAsync() ?? (g.TargetId.HasValue ? "Id:" + g.TargetId.Value.ToString() : "Unknown");
                        }
                        topAccessed.Add(new KeyValuePair<string, int>(name, g.Count));
                    }
                }
                ViewBag.TopAccessedFiles = topAccessed;

                // top active users from AuditLogs: count actions that indicate activity (create/update/delete/group/share/upload/lock/unlock)
                var activeKeywords = new[] { "CREATE", "DELETE", "UPDATE", "ADD", "REMOVE", "SHARE", "UPLOAD", "LOCK", "UNLOCK", "RENAME", "MOVE", "GROUP" };
                var topActive = new List<KeyValuePair<string, int>>();

                if (_context.AuditLogs != null && await _context.AuditLogs.AnyAsync(a => a.Action != null && activeKeywords.Any(k => a.Action.ToUpper().Contains(k))))
                {
                    var groupedActive = await _context.AuditLogs
                        .Where(a => a.Action != null && activeKeywords.Any(k => a.Action.ToUpper().Contains(k)))
                        .GroupBy(a => string.IsNullOrEmpty(a.Actor) ? a.ActorId : a.Actor)
                        .Select(g => new { Actor = g.Key, Count = g.Count() })
                        .OrderByDescending(x => x.Count)
                        .Take(10)
                        .ToListAsync();

                    // map of actorKey -> display name
                    var nameMap = new Dictionary<string, string>();

                    foreach (var g in groupedActive)
                    {
                        var actorKey = g.Actor; // could be Actor (name/email) or ActorId
                        string displayName = actorKey ?? "Unknown";
                        bool isAdminActor = false;

                        if (!string.IsNullOrEmpty(actorKey))
                        {
                            // try resolve to ApplicationUser by id or email
                            ApplicationUser actorUser = null;
                            try
                            {
                                actorUser = await _userManager.FindByIdAsync(actorKey);
                                if (actorUser == null)
                                {
                                    actorUser = await _userManager.FindByEmailAsync(actorKey);
                                }
                            }
                            catch { actorUser = null; }

                            if (actorUser != null)
                            {
                                isAdminActor = await _userManager.IsInRoleAsync(actorUser, "Admin");
                                displayName = string.IsNullOrEmpty(actorUser.UserName) ? (actorUser.Email ?? actorKey) : actorUser.UserName;
                            }
                        }

                        if (!isAdminActor)
                        {
                            topActive.Add(new KeyValuePair<string, int>(actorKey ?? "unknown", g.Count));
                            nameMap[actorKey ?? "unknown"] = displayName;
                        }
                    }

                    // store display name map for view
                    ViewBag.TopActiveUserNames = nameMap;
                }

                ViewBag.TopActiveUsers = topActive;

                // For each top active user, collect recent activity lines (latest 3)
                var topActiveActivities = new Dictionary<string, List<string>>();
                if (topActive.Any())
                {
                    foreach (var t in topActive)
                    {
                        var actorKey = t.Key;
                        var recentLogs = await _context.AuditLogs
                            .Where(a => (a.Actor == actorKey || a.ActorId == actorKey) && a.Action != null && activeKeywords.Any(k => a.Action.ToUpper().Contains(k)))
                            .OrderByDescending(a => a.Timestamp)
                            .Take(3)
                            .ToListAsync();

                        var entries = recentLogs.Select(a =>
                            $"{a.Timestamp:yyyy-MM-dd HH:mm} — {a.Action}{(string.IsNullOrEmpty(a.TargetName) ? string.Empty : " '" + a.TargetName + "'")}").ToList();

                        topActiveActivities[actorKey] = entries;
                    }
                }

                ViewBag.TopActiveUserActivities = topActiveActivities;
            }
            catch
            {
                ViewBag.FileTypeLabels = new List<string>();
                ViewBag.FileTypeValues = new List<int>();
                ViewBag.FileTypeTotal = 0;
                ViewBag.PremiumCount = 0;
                ViewBag.ProCount = 0;
                ViewBag.StandardCount = 0;
                ViewBag.TopFiles = new List<KeyValuePair<string, int>>();
                ViewBag.TopAccessedFiles = new List<KeyValuePair<string, int>>();
                ViewBag.TopActiveUsers = new List<KeyValuePair<string, int>>();
                ViewBag.TopActiveUserActivities = new Dictionary<string, List<string>>();
            }

            // return the latestUsers prepared earlier
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
                // clear application premium flags
                try
                {
                    user.IsPremium = false;
                    user.PremiumUntil = null;
                    _context.Users.Update(user);
                    await _context.SaveChangesAsync();
                }
                catch { }

                TempData["Success"] = $"Đã đặt tài khoản {user.Email} về Standard.";
            }
            else if (tier.Equals("Pro", System.StringComparison.OrdinalIgnoreCase))
            {
                if (await _userManager.IsInRoleAsync(user, "Premium")) await _userManager.RemoveFromRoleAsync(user, "Premium");
                if (!await _userManager.IsInRoleAsync(user, "Pro")) await _userManager.AddToRoleAsync(user, "Pro");
                // Pro is not premium
                try
                {
                    user.IsPremium = false;
                    user.PremiumUntil = null;
                    _context.Users.Update(user);
                    await _context.SaveChangesAsync();
                }
                catch { }

                TempData["Success"] = $"Đã nâng cấp tài khoản {user.Email} lên Pro.";
            }
            else if (tier.Equals("Premium", System.StringComparison.OrdinalIgnoreCase))
            {
                if (await _userManager.IsInRoleAsync(user, "Pro")) await _userManager.RemoveFromRoleAsync(user, "Pro");
                if (!await _userManager.IsInRoleAsync(user, "Premium")) await _userManager.AddToRoleAsync(user, "Premium");
                // mark application premium flag (do not set PremiumUntil here)
                try
                {
                    user.IsPremium = true;
                    _context.Users.Update(user);
                    await _context.SaveChangesAsync();
                }
                catch { }

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

        // Action xóa tài khoản người dùng (Admin only)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteUser(string userId)
        {
            var currentAdminId = _userManager.GetUserId(User);

            if (string.IsNullOrEmpty(userId)) return RedirectToAction("ManageUsers");

            // Prevent self-delete
            if (userId == currentAdminId)
            {
                TempData["Error"] = "Bạn không thể xóa chính mình.";
                return RedirectToAction("ManageUsers");
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null) return RedirectToAction("ManageUsers");

            // Protect Admin accounts
            if (await _userManager.IsInRoleAsync(user, "Admin"))
            {
                TempData["Error"] = "Không thể xóa tài khoản có quyền Quản trị viên.";
                return RedirectToAction("ManageUsers");
            }

            // Require transfer of any owned groups first
            var ownsGroup = await _context.Groups.AnyAsync(g => g.OwnerId == userId);
            if (ownsGroup)
            {
                TempData["Error"] = "Người dùng đang là chủ nhóm. Vui lòng chuyển quyền trước khi xóa.";
                return RedirectToAction("ManageUsers");
            }

            using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                // Remove group memberships
                var gm = await _context.GroupMembers.Where(m => m.UserId == userId).ToListAsync();
                if (gm.Any()) _context.GroupMembers.RemoveRange(gm);

                // Remove permissions granted to this user
                var perms = await _context.Permissions.Where(p => p.UserId == userId).ToListAsync();
                if (perms.Any()) _context.Permissions.RemoveRange(perms);

                // Optionally remove payment transactions
                var pays = await _context.PaymentTransactions.Where(p => p.UserId == userId).ToListAsync();
                if (pays.Any()) _context.PaymentTransactions.RemoveRange(pays);

                await _context.SaveChangesAsync();

                // Delete the Identity user
                var result = await _userManager.DeleteAsync(user);
                if (!result.Succeeded)
                {
                    var errors = string.Join("; ", result.Errors.Select(e => e.Description));
                    TempData["Error"] = "Xóa tài khoản thất bại: " + errors;
                    await tx.RollbackAsync();
                    return RedirectToAction("ManageUsers");
                }

                await AuditHelper.LogAsync(HttpContext, "DELETE_USER", "User", null, user.Email, $"Admin deleted user {user.Email}");
                await tx.CommitAsync();

                TempData["Success"] = $"Đã xóa tài khoản {user.Email}";
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                TempData["Error"] = "Lỗi khi xóa tài khoản: " + ex.Message;
            }

            return RedirectToAction("ManageUsers");
        }

        // Admin: create new user (no email confirmation required)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateUser(string email, string password, string userName, string role = "Standard")
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                TempData["Error"] = "Email và mật khẩu là bắt buộc.";
                return RedirectToAction("ManageUsers");
            }

            var existing = await _userManager.FindByEmailAsync(email);
            if (existing != null)
            {
                TempData["Error"] = "Đã tồn tại tài khoản với email này.";
                return RedirectToAction("ManageUsers");
            }

            var newUser = new ApplicationUser
            {
                UserName = string.IsNullOrWhiteSpace(userName) ? email : userName,
                Email = email,
                EmailConfirmed = true, // admin-created users are marked confirmed
                IsPremium = false
            };

            var createResult = await _userManager.CreateAsync(newUser, password);
            if (!createResult.Succeeded)
            {
                var errors = string.Join("; ", createResult.Errors.Select(e => e.Description));
                TempData["Error"] = "Tạo tài khoản thất bại: " + errors;
                return RedirectToAction("ManageUsers");
            }

            // Ensure common roles exist
            if (!await _roleManager.RoleExistsAsync("User")) await _roleManager.CreateAsync(new IdentityRole("User"));
            if (!await _roleManager.RoleExistsAsync("Premium")) await _roleManager.CreateAsync(new IdentityRole("Premium"));
            if (!await _roleManager.RoleExistsAsync("Pro")) await _roleManager.CreateAsync(new IdentityRole("Pro"));

            if (role.Equals("Premium", System.StringComparison.OrdinalIgnoreCase))
            {
                await _userManager.AddToRoleAsync(newUser, "Premium");
                // mark as premium in application
                try { newUser.IsPremium = true; _context.Users.Update(newUser); await _context.SaveChangesAsync(); } catch { }
            }
            else
            {
                await _userManager.AddToRoleAsync(newUser, "User");
            }

            TempData["Success"] = $"Đã tạo tài khoản {email} và đánh dấu là đã xác thực.";
            await AuditHelper.LogAsync(HttpContext, "CREATE_USER", "User", null, email, $"Admin created user {email} (confirmed)");

            return RedirectToAction("ManageUsers");
        }
    }
}