using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using WebDocumentManagement_FileSharing.Data;
using WebDocumentManagement_FileSharing.Models;

namespace WebDocumentManagement_FileSharing.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly ApplicationDbContext _context;

        public HomeController(ILogger<HomeController> logger, ApplicationDbContext context)
        {
            _logger = logger;
            _context = context;
        }

        // ======================================================
        // INDEX (Trang chủ sau khi đăng nhập)
        // ======================================================
        public async Task<IActionResult> Index()
        {
            // 1. Kiểm tra đăng nhập
            if (!(User?.Identity?.IsAuthenticated == true))
            {
                ViewData["ForceShowSidebar"] = false;
                return View("Landing");
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var vm = new FileSystemViewModel
            {
                CurrentFolderId = null
            };

            // 2. Lấy danh sách thư mục gốc (Root Folders) của chính User này
            // Bao gồm thư mục do user sở hữu hoặc đã được chia sẻ với user
            vm.Folders = await _context.Folders
                .Where(f => f.ParentId == null && !f.IsDeleted &&
                    (f.OwnerId == userId || _context.Permissions.Any(p => p.FolderId == f.Id && p.UserId == userId)))
                .OrderBy(f => f.Name)
                .ToListAsync();

            // 3. Lấy danh sách tệp tin gốc (Root Documents) cho user: sở hữu hoặc được chia sẻ
            vm.Documents = await _context.Documents
                .Where(d => (d.FolderId == null || d.FolderId == 0) && !d.IsDeleted &&
                    (d.OwnerId == userId || _context.Permissions.Any(p => p.DocumentId == d.Id && p.UserId == userId)))
                .Include(d => d.Folder)
                .OrderByDescending(d => d.UploadedDate)
                .ToListAsync();

            ViewData["ForceShowSidebar"] = true;
            return View(vm);
        }

        // ======================================================
        // CREATE (Tạo thư mục nhanh tại trang chủ)
        // ======================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Folder folder)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (!ModelState.IsValid || string.IsNullOrEmpty(userId))
            {
                return RedirectToAction(nameof(Index));
            }

            // Thiết lập mặc định cho thư mục tạo tại Home
            folder.ParentId = null;
            folder.CreatedDate = DateTime.UtcNow;
            folder.OwnerId = userId;
            folder.IsDeleted = false;

            // Kiểm tra trùng tên tại thư mục gốc
            bool exists = await _context.Folders.AnyAsync(f =>
                f.ParentId == null &&
                f.Name == folder.Name &&
                f.OwnerId == userId &&
                !f.IsDeleted);

            if (exists)
            {
                TempData["Error"] = "Thư mục có tên này đã tồn tại ở trang gốc.";
                return RedirectToAction(nameof(Index));
            }

            _context.Folders.Add(folder);
            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Index));
        }

        // ======================================================
        // TRASH (Tính năng đề xuất thêm: Xem thùng rác)
        // ======================================================
        public async Task<IActionResult> Trash()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            var vm = new FileSystemViewModel
            {
                Folders = await _context.Folders.Where(f => f.OwnerId == userId && f.IsDeleted).ToListAsync(),
                Documents = await _context.Documents.Where(d => d.OwnerId == userId && d.IsDeleted).ToListAsync()
            };

            ViewData["ForceShowSidebar"] = true;
            return View(vm);
        }
    }
}