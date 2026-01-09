using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using WebDocumentManagement_FileSharing.Data;
using WebDocumentManagement_FileSharing.Models;
using WebDocumentManagement_FileSharing.Models.ViewModel;
using WebDocumentManagement_FileSharing.Helpers;
using System.Security.Cryptography;

namespace WebDocumentManagement_FileSharing.Controllers
{
    [Authorize]
    public class ShareController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        public ShareController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        // ======================================================
        // 1️⃣ SHARE FILE (GET)
        // ======================================================
        public async Task<IActionResult> Create(int documentId)
        {
            var document = await _context.Documents.FindAsync(documentId);
            if (document == null)
                return NotFound();

            return View(new ShareViewModel
            {
                DocumentId = documentId,
                DocumentTitle = document.Title ?? document.FileName,
                AccessType = AccessLevel.Read
            });
        }

        // ======================================================
        // 2️⃣ SHARE FILE (POST)
        // ======================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcessShareGeneric(string targetType, int targetId, string receiverEmail, string accessType)
        {
            if (string.IsNullOrEmpty(receiverEmail))
            {
                TempData["Error"] = "Email người nhận chưa được cung cấp.";
                return RedirectToAction("Index", "Documents");
            }

            var receiver = await _userManager.FindByEmailAsync(receiverEmail);
            if (receiver == null)
            {
                TempData["Error"] = "Không tìm thấy người dùng với email này.";
                return RedirectToAction("Index", "Documents");
            }

            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (receiver.Id == currentUserId)
            {
                TempData["Error"] = "Bạn không thể chia sẻ cho chính mình.";
                return RedirectToAction("Index", "Documents");
            }

            // parse access type
            if (!Enum.TryParse(accessType, out AccessLevel level)) level = AccessLevel.Read;

            if (string.Equals(targetType, "folder", StringComparison.OrdinalIgnoreCase))
            {
                // ensure folder exists and current user has rights (owner)
                var folder = await _context.Folders.FirstOrDefaultAsync(f => f.Id == targetId && f.OwnerId == currentUserId && !f.IsDeleted);
                if (folder == null)
                {
                    TempData["Error"] = "Thư mục không tồn tại hoặc bạn không có quyền.";
                    return RedirectToAction("Index", "Home");
                }

                // check existing permission
                bool existsPerm = await _context.Permissions.AnyAsync(p => p.FolderId == targetId && p.UserId == receiver.Id);
                if (existsPerm)
                {
                    TempData["Error"] = "Thư mục đã được chia sẻ cho người này.";
                    return RedirectToAction("Index", "Home");
                }

                var folderPerm = new Permission
                {
                    FolderId = targetId,
                    UserId = receiver.Id,
                    AccessType = level,
                    SharedDate = DateTime.Now
                };

                _context.Permissions.Add(folderPerm);
                await _context.SaveChangesAsync();

                // Audit: folder shared
                await AuditHelper.LogAsync(HttpContext, "SHARE_FOLDER", "Folder", folder.Id, folder.Name, $"Shared folder '{folder.Name}' with {receiverEmail} as {level}");

                TempData["Message"] = "Đã chia sẻ thư mục thành công.";
                return RedirectToAction("Index", "Home");
            }

            // default: document
            var document = await _context.Documents.FindAsync(targetId);
            if (document == null)
            {
                TempData["Error"] = "Tài liệu không tồn tại.";
                return RedirectToAction("Index", "Documents");
            }

            bool exists = await _context.Permissions.AnyAsync(p => p.DocumentId == targetId && p.UserId == receiver.Id);
            if (exists)
            {
                TempData["Error"] = "Tài liệu đã được chia sẻ cho người này.";
                return RedirectToAction("Index", "Documents");
            }

            var docPerm = new Permission
            {
                DocumentId = targetId,
                UserId = receiver.Id,
                AccessType = level,
                SharedDate = DateTime.Now
            };

            _context.Permissions.Add(docPerm);
            await _context.SaveChangesAsync();

            // Audit: document shared
            await AuditHelper.LogAsync(HttpContext, "SHARE_DOCUMENT", "Document", document.Id, document.FileName, $"Shared document '{document.FileName}' with {receiverEmail} as {level}");

            TempData["Message"] = "Chia sẻ tài liệu thành công.";
            return RedirectToAction("Index", "Documents");
        }

        // ======================================================
        // 3️⃣ FILE ĐƯỢC SHARE VỚI TÔI + TÔI ĐÃ CHIA SẺ
        // ======================================================
        public async Task<IActionResult> SharedWithMe()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // Include both Document and Folder navigation so view can render either
            var sharedWithMe = await _context.Permissions
                .Where(p => p.UserId == userId)
                .Include(p => p.Document)
                .Include(p => p.Folder)
                .OrderByDescending(p => p.SharedDate)
                .ToListAsync();

            // Permissions where current user is the owner of the document or folder
            var sharedByMe = await _context.Permissions
                .Include(p => p.Document)
                .Include(p => p.Folder)
                .Where(p => (p.Document != null && p.Document.OwnerId == userId) || (p.Folder != null && p.Folder.OwnerId == userId))
                .OrderByDescending(p => p.SharedDate)
                .ToListAsync();

            var vm = new SharedListingsViewModel
            {
                SharedWithMe = sharedWithMe,
                SharedByMe = sharedByMe
            };

            // return the shared view that lives under Views/Documents/Shared.cshtml
            return View("~/Views/Documents/Shared.cshtml", vm);
        }

        // ======================================================
        // 4️⃣ HỦY CHIA SẺ THEO ID PERMISSION (CHO CHỦ SỞ HỮU)
        // ======================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RevokePermission(int id)
        {
            var permission = await _context.Permissions
                .Include(p => p.Document)
                .Include(p => p.Folder)
                .FirstOrDefaultAsync(p => p.Id == id);

            if (permission == null)
                return NotFound();

            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var ownerId = permission.Document?.OwnerId ?? permission.Folder?.OwnerId;

            if (ownerId != currentUserId)
                return Forbid();

            _context.Permissions.Remove(permission);
            await _context.SaveChangesAsync();

            await AuditHelper.LogAsync(HttpContext, "UNSHARE", "Permission", permission.Id, permission.DocumentId?.ToString() ?? permission.FolderId?.ToString() ?? "", $"Removed permission id={permission.Id} by owner {currentUserId}");

            TempData["Message"] = "Đã thu hồi quyền chia sẻ.";
            // Redirect to Documents/Shared which displays both "shared with me" and "shared by me"
            return RedirectToAction("Shared", "Documents");
        }

        // ======================================================
        // 5️⃣ CẬP NHẬT QUYỀN TRUY CẬP (CHO CHỦ SỞ HỮU)
        // ======================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdatePermission(int id, string accessType)
        {
            var permission = await _context.Permissions
                .Include(p => p.Document)
                .Include(p => p.Folder)
                .FirstOrDefaultAsync(p => p.Id == id);

            if (permission == null)
                return NotFound();

            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var ownerId = permission.Document?.OwnerId ?? permission.Folder?.OwnerId;

            if (ownerId != currentUserId)
                return Forbid();

            if (!Enum.TryParse(accessType, out AccessLevel level)) level = AccessLevel.Read;

            permission.AccessType = level;
            _context.Permissions.Update(permission);
            await _context.SaveChangesAsync();

            await AuditHelper.LogAsync(HttpContext, "UPDATE_PERMISSION", "Permission", permission.Id, permission.DocumentId?.ToString() ?? permission.FolderId?.ToString() ?? "", $"Updated permission id={permission.Id} to {level} by owner {currentUserId}");

            TempData["Message"] = "Đã cập nhật quyền truy cập.";
            // Redirect to Documents/Shared which displays both "shared with me" and "shared by me"
            return RedirectToAction("Shared", "Documents");
        }

        // ======================================================
        // 6️⃣ TẠO LINK CHIA SẺ
        // ======================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateShareLink(string targetType, int targetId, string accessType, string scope)
        {
            // scope: "public" or "restricted"
            if (string.IsNullOrEmpty(targetType)) return BadRequest();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            // validate target exists and ownership if folder
            if (targetType == "folder")
            {
                var folder = await _context.Folders.FindAsync(targetId);
                if (folder == null) return NotFound();
                if (folder.OwnerId != userId) return Forbid();
            }
            else
            {
                var doc = await _context.Documents.FindAsync(targetId);
                if (doc == null) return NotFound();
                if (doc.OwnerId != userId) return Forbid();
            }

            if (!Enum.TryParse(accessType, out AccessLevel level)) level = AccessLevel.Read;
            var isPublic = string.Equals(scope, "public", StringComparison.OrdinalIgnoreCase);

            // generate short token
            var token = GenerateToken();

            var link = new ShareLink
            {
                Token = token,
                TargetType = targetType,
                TargetId = targetId,
                AccessType = level,
                IsPublic = isPublic,
                CreatedBy = userId,
                CreatedAt = DateTime.UtcNow
            };

            _context.Add(link);
            await _context.SaveChangesAsync();

            var url = Url.Action("OpenShared", "Share", new { token = token }, Request.Scheme);
            // If called via AJAX (from modal), return JSON with url
            if (Request.Headers != null && string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
            {
                return Json(new { url = url });
            }

            TempData["ShareLink"] = url;
            return RedirectToAction("Index", targetType == "folder" ? "Home" : "Documents");
        }

        // Resolve a share link token and allow access or present restricted flow
        [AllowAnonymous]
        public async Task<IActionResult> OpenShared(string token)
        {
            if (string.IsNullOrEmpty(token)) return NotFound();
            var link = await _context.Set<ShareLink>().FirstOrDefaultAsync(s => s.Token == token);
            if (link == null) return NotFound();

            // if public, show resource according to type and access
            if (link.IsPublic)
            {
                if (link.TargetType == "folder")
                {
                    return RedirectToAction("Index", "Folders", new { folderId = link.TargetId });
                }
                else
                {
                    // Redirect to Documents/Preview and include token so DocumentsController can validate
                    return RedirectToAction("Preview", "Documents", new { id = link.TargetId, token = token });
                }
            }

            // restricted: require login -- redirect to login with returnUrl to this token route
            var returnUrl = Url.Action("OpenShared", "Share", new { token = token });
            return RedirectToPage("/Account/Login", new { area = "Identity", ReturnUrl = returnUrl });
        }

        private string GenerateToken()
        {
            // generate a URL-safe token of ~16 chars
            var bytes = new byte[12];
            RandomNumberGenerator.Fill(bytes);
            return WebEncoders.Base64UrlEncode(bytes);
        }
    }
}

