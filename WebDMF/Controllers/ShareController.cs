using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using WebDocumentManagement_FileSharing.Data;
using WebDocumentManagement_FileSharing.Models;
using WebDocumentManagement_FileSharing.Models.ViewModel;


namespace WebDocumentManagement_FileSharing.Controllers
{
    [Authorize]
    public class ShareController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<IdentityUser> _userManager;

        public ShareController(
            ApplicationDbContext context,
            UserManager<IdentityUser> userManager)
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

            TempData["Message"] = "Chia sẻ tài liệu thành công.";
            return RedirectToAction("Index", "Documents");
        }

        // ======================================================
        // 3️⃣ FILE ĐƯỢC SHARE VỚI TÔI
        // ======================================================
        public async Task<IActionResult> SharedWithMe()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // Include both Document and Folder navigation so view can render either
            var sharedPermissions = await _context.Permissions
                .Where(p => p.UserId == userId)
                .Include(p => p.Document)
                .Include(p => p.Folder)
                .OrderByDescending(p => p.SharedDate)
                .ToListAsync();

            return View(sharedPermissions);
        }

        // ======================================================
        // 4️⃣ HỦY CHIA SẺ
        // ======================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Remove(int documentId, string userId)
        {
            var permission = await _context.Permissions
                .FirstOrDefaultAsync(p =>
                    p.DocumentId == documentId &&
                    p.UserId == userId);

            if (permission == null)
                return NotFound();

            _context.Permissions.Remove(permission);
            await _context.SaveChangesAsync();

            TempData["Message"] = "Đã hủy chia sẻ.";
            return RedirectToAction("Index", "Documents");
        }

        // (No ownership transfer helper required; folder sharing uses Permission records.)
    }
}

