using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using WebDocumentManagement_FileSharing.Data;
using WebDocumentManagement_FileSharing.Models;

namespace WebDocumentManagement_FileSharing.Controllers
{
    [Authorize]
    public class FoldersController : Controller
    {
        private readonly ApplicationDbContext _context;

        public FoldersController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ======================================================
        // CREATE
        // ======================================================
        public IActionResult Create(int? parentId)
        {
            return View(new Folder
            {
                ParentId = parentId
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Folder folder)
        {
            if (!ModelState.IsValid)
                return View(folder);

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

            folder.CreatedDate = DateTime.UtcNow;
            folder.OwnerId = userId;
            folder.IsDeleted = false;

            // Kiểm tra trùng tên trong cùng thư mục cha (và của cùng một người sở hữu)
            bool exists = await _context.Folders.AnyAsync(f =>
                f.ParentId == folder.ParentId &&
                f.Name == folder.Name &&
                f.OwnerId == userId &&
                !f.IsDeleted);

            if (exists)
            {
                ModelState.AddModelError("", "Tên thư mục đã tồn tại.");
                return View(folder);
            }

            _context.Folders.Add(folder);
            await _context.SaveChangesAsync();

            return RedirectAfterFolderAction(folder.ParentId);
        }

        // ======================================================
        // RENAME
        // ======================================================
        public async Task<IActionResult> Rename(int id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var folder = await _context.Folders.FirstOrDefaultAsync(f => f.Id == id && f.OwnerId == userId);

            if (folder == null)
                return NotFound();

            return View(folder);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Rename(int id, Folder input)
        {
            if (id != input.Id)
                return NotFound();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var folder = await _context.Folders.FirstOrDefaultAsync(f => f.Id == id && f.OwnerId == userId);

            if (folder == null)
                return NotFound();

            bool exists = await _context.Folders.AnyAsync(f =>
                f.ParentId == folder.ParentId &&
                f.Name == input.Name &&
                f.Id != id &&
                f.OwnerId == userId &&
                !f.IsDeleted);

            if (exists)
            {
                ModelState.AddModelError("", "Tên thư mục đã tồn tại.");
                return View(input);
            }

            folder.Name = input.Name;
            await _context.SaveChangesAsync();

            TempData["Message"] = "Đổi tên thư mục thành công.";
            return RedirectAfterFolderAction(folder.ParentId);
        }

        // ======================================================
        // DELETE (SOFT DELETE RECURSIVE)
        // ======================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // Tìm thư mục và kiểm tra quyền sở hữu
            var folder = await _context.Folders
                .Include(f => f.SubFolders)
                .Include(f => f.Documents)
                .FirstOrDefaultAsync(f => f.Id == id && f.OwnerId == userId);

            if (folder == null)
            {
                TempData["Error"] = "Thư mục không tồn tại hoặc bạn không có quyền xóa.";
                return RedirectToAction("Index", "Home");
            }

            // Thực hiện đánh dấu xóa mềm toàn bộ cây thư mục và tệp tin bên trong
            await SoftDeleteFolderRecursive(folder);
            await _context.SaveChangesAsync();

            TempData["Message"] = "Đã chuyển thư mục và nội dung bên trong vào thùng rác.";
            return RedirectAfterFolderAction(folder.ParentId);
        }

        /// <summary>
        /// Hàm hỗ trợ đánh dấu IsDeleted = true cho Folder, SubFolders và Documents bên trong
        /// </summary>
        private async Task SoftDeleteFolderRecursive(Folder folder)
        {
            folder.IsDeleted = true;

            // Load và đánh dấu xóa các Document trong thư mục này
            await _context.Entry(folder).Collection(f => f.Documents).LoadAsync();
            foreach (var doc in folder.Documents)
            {
                doc.IsDeleted = true;
            }

            // Load và đệ quy đánh dấu xóa các thư mục con
            await _context.Entry(folder).Collection(f => f.SubFolders).LoadAsync();
            foreach (var sub in folder.SubFolders)
            {
                await SoftDeleteFolderRecursive(sub);
            }
        }

        // ======================================================
        // UPLOAD FOLDER (POST) - Upload entire folder (webkitdirectory)
        // ======================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadFolder(int folderId)
        {
            var files = Request.Form.Files;
            if (files == null || files.Count == 0)
            {
                TempData["Error"] = "Không có tệp được chọn.";
                return RedirectAfterFolderAction(folderId == 0 ? (int?)null : folderId);
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            int? parentId = (folderId == 0) ? null : folderId;

            // Ensure uploads folder exists
            var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
            if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

            foreach (var file in files)
            {
                // webkitdirectory sends FileName as relative path e.g. "subfolder/file.txt"
                var relativePath = file.FileName.Replace('\\', '/').TrimStart('/');
                if (string.IsNullOrEmpty(relativePath)) continue;

                var parts = relativePath.Split('/');
                int? currentParent = parentId;

                // create/find folders for all but last segment (which is the file)
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    var partName = parts[i];
                    var existing = await _context.Folders.FirstOrDefaultAsync(f => f.ParentId == currentParent && f.Name == partName && f.OwnerId == userId && !f.IsDeleted);
                    if (existing == null)
                    {
                        var newFolder = new Folder
                        {
                            Name = partName,
                            ParentId = currentParent,
                            CreatedDate = DateTime.UtcNow,
                            OwnerId = userId,
                            IsDeleted = false
                        };
                        _context.Folders.Add(newFolder);
                        await _context.SaveChangesAsync();
                        currentParent = newFolder.Id;
                    }
                    else
                    {
                        currentParent = existing.Id;
                    }
                }

                // last part is the file name
                var fileName = parts.Last();
                var ext = Path.GetExtension(fileName);
                var storedFileName = Guid.NewGuid().ToString() + ext;
                var physicalPath = Path.Combine(uploadsFolder, storedFileName);

                using (var stream = new FileStream(physicalPath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                var document = new Document
                {
                    Title = Path.GetFileNameWithoutExtension(fileName),
                    FileName = fileName,
                    FilePath = "/uploads/" + storedFileName,
                    Extension = ext?.TrimStart('.') ?? string.Empty,
                    FileSize = file.Length,
                    ContentType = file.ContentType,
                    UploadedDate = DateTime.UtcNow,
                    OwnerId = userId,
                    FolderId = currentParent,
                    IsDeleted = false
                };

                _context.Documents.Add(document);
                await _context.SaveChangesAsync();

                // create initial version
                var version = new DocumentVersion
                {
                    DocumentId = document.Id,
                    VersionNumber = 1,
                    FileName = fileName,
                    FilePath = document.FilePath,
                    CreatedDate = DateTime.UtcNow,
                    ChangeNote = "Uploaded via folder upload"
                };
                _context.DocumentVersions.Add(version);
                await _context.SaveChangesAsync();
            }

            TempData["Message"] = "Tải thư mục thành công.";
            return RedirectAfterFolderAction(parentId);
        }

        // ======================================================
        // HELPER
        // ======================================================
        private IActionResult RedirectAfterFolderAction(int? parentId)
        {
            if (parentId.HasValue && parentId.Value != 0)
                return RedirectToAction("Index", "Documents", new { folderId = parentId });

            return RedirectToAction("Index", "Home");
        }
    }
}