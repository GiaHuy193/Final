using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using WebDocumentManagement_FileSharing.Data;
using WebDocumentManagement_FileSharing.Helpers;
using WebDocumentManagement_FileSharing.Models;

namespace WebDocumentManagement_FileSharing.Controllers
{
    [Authorize]
    public class DocumentsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<IdentityUser> _userManager;

        public DocumentsController(ApplicationDbContext context, UserManager<IdentityUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        // ==========================================
        // INDEX - Hiển thị danh sách file/folder
        // ==========================================
        public async Task<IActionResult> Index(int? folderId)
        {
            // Chuẩn hóa folderId: Nếu truyền vào 0 thì coi như null (thư mục gốc)
            int? actualFolderId = (folderId == 0) ? null : folderId;

            var viewModel = new FileSystemViewModel
            {
                CurrentFolderId = actualFolderId
            };

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // Determine access for the requested folder
            bool hasAccessToFolder = true;
            if (actualFolderId.HasValue)
            {
                hasAccessToFolder = await HasAccessToFolderAsync(actualFolderId.Value, userId);
            }

            if (!actualFolderId.HasValue)
            {
                // Root: show only folders/documents owned by the current user.
                // Do NOT include items shared by other users here — those appear in Documents/Shared only.
                viewModel.Folders = await _context.Folders
                    .Where(f => f.ParentId == null && !f.IsDeleted && f.OwnerId == userId)
                    .OrderBy(f => f.Name)
                    .ToListAsync();

                viewModel.Documents = await _context.Documents
                    .Where(d => (d.FolderId == null || d.FolderId == 0) && !d.IsDeleted && d.OwnerId == userId)
                    .OrderByDescending(d => d.UploadedDate)
                    .ToListAsync();
            }
            else
            {
                if (!hasAccessToFolder)
                {
                    return Forbid();
                }

                // Folder is accessible (owned by user or shared via ancestor); list all children regardless of owner
                viewModel.Folders = await _context.Folders
                    .Where(f => f.ParentId == actualFolderId && !f.IsDeleted)
                    .OrderBy(f => f.Name)
                    .ToListAsync();

                viewModel.Documents = await _context.Documents
                    .Where(d => d.FolderId == actualFolderId && !d.IsDeleted)
                    .OrderByDescending(d => d.UploadedDate)
                    .ToListAsync();
            }

            return View(viewModel);
        }

        // ==========================================
        // DETAILS - Xem chi tiết & Lịch sử phiên bản
        // ==========================================
        public async Task<IActionResult> Details(int id)
        {
            var document = await _context.Documents
                .Include(d => d.Folder)
                .Include(d => d.Versions) // Load lịch sử version
                .FirstOrDefaultAsync(d => d.Id == id);

            if (document == null || document.IsDeleted) return NotFound();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            // enforce access: owner or permission on document or permission on containing folder/ancestors
            if (!await HasAccessToDocumentAsync(id, userId)) return Forbid();

            // Lấy tên người dùng (Email/Username) từ OwnerId để hiển thị thay vì dãy số GUID
            var owner = await _userManager.FindByIdAsync(document.OwnerId);
            ViewBag.OwnerName = owner != null ? owner.UserName : "Không xác định";

            return View(document);
        }

        // ==========================================
        // CREATE (GET) - Hiển thị trang chọn tệp tải lên
        // ==========================================
        [HttpGet]
        public IActionResult Create(int? folderId, string? returnUrl = null)
        {
            // Chuẩn bị danh sách thư mục để người dùng chọn (nếu muốn đổi thư mục khi upload)
            ViewData["FolderId"] = new SelectList(_context.Folders.OrderBy(f => f.Name), "Id", "Name", folderId);

            ViewBag.ReturnUrl = returnUrl;

            // Trả về View cùng với một Model mới, gán sẵn FolderId nếu có
            return View(new Document { FolderId = folderId == 0 ? null : folderId });
        }

        // ==========================================
        // CREATE (POST) - Tải lên tệp lần đầu (v1)
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Document document, IFormFile fileUpload, string? returnUrl = null)
        {
            if (fileUpload == null || fileUpload.Length == 0)
            {
                TempData["Error"] = "Vui lòng chọn tệp hợp lệ.";
                return RedirectToAction(nameof(Index), new { folderId = document.FolderId });
            }

            // 1. Lưu file vật lý vào wwwroot/uploads
            var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
            if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

            var originalFileName = Path.GetFileName(fileUpload.FileName);
            var extension = Path.GetExtension(originalFileName);
            var storedFileName = $"{Guid.NewGuid()}{extension}";
            var physicalPath = Path.Combine(uploadsFolder, storedFileName);

            using (var stream = new FileStream(physicalPath, FileMode.Create))
            {
                await fileUpload.CopyToAsync(stream);
            }

            // 2. Lưu thông tin Document chính
            document.Title ??= Path.GetFileNameWithoutExtension(originalFileName);
            document.FileName = originalFileName;
            document.FilePath = "/uploads/" + storedFileName;
            document.Extension = extension.TrimStart('.');
            document.FileSize = fileUpload.Length;
            document.ContentType = fileUpload.ContentType;
            document.UploadedDate = DateTime.Now;
            document.OwnerId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            document.FolderId = (document.FolderId == 0) ? null : document.FolderId;
            document.IsDeleted = false;

            _context.Documents.Add(document);
            await _context.SaveChangesAsync();

            // 3. Tự động tạo Version 1
            var firstVersion = new DocumentVersion
            {
                DocumentId = document.Id,
                VersionNumber = 1,
                FileName = originalFileName,
                FilePath = document.FilePath,
                CreatedDate = DateTime.Now,
                ChangeNote = "Phiên bản gốc"
            };
            _context.DocumentVersions.Add(firstVersion);
            await _context.SaveChangesAsync();

            TempData["Message"] = "Tải lên thành công!";
            if (!string.IsNullOrEmpty(returnUrl)) return LocalRedirect(returnUrl);
            return RedirectToAction(nameof(Index), new { folderId = document.FolderId });
        }

        // ==========================================
        // UPLOAD NEW VERSION - Cập nhật v2, v3...
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadNewVersion(int documentId, IFormFile fileUpload, string changeNote)
        {
            var document = await _context.Documents
                .Include(d => d.Versions)
                .FirstOrDefaultAsync(d => d.Id == documentId);

            if (document == null || fileUpload == null) return NotFound();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!await HasAccessToDocumentAsync(documentId, userId)) return Forbid();

            var originalFileName = Path.GetFileName(fileUpload.FileName);
            var extension = Path.GetExtension(originalFileName);
            var storedFileName = $"{Guid.NewGuid()}{extension}";
            var physicalPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", storedFileName);

            using (var stream = new FileStream(physicalPath, FileMode.Create))
            {
                await fileUpload.CopyToAsync(stream);
            }

            // Cập nhật thông tin bản mới nhất vào Document chính
            document.FilePath = "/uploads/" + storedFileName;
            document.FileSize = fileUpload.Length;
            document.UploadedDate = DateTime.Now;

            // Tính số version tiếp theo
            int nextVersionNum = (document.Versions?.Max(v => v.VersionNumber) ?? 1) + 1;

            var newVersion = new DocumentVersion
            {
                DocumentId = document.Id,
                VersionNumber = nextVersionNum,
                FileName = originalFileName,
                FilePath = document.FilePath,
                CreatedDate = DateTime.Now,
                ChangeNote = changeNote ?? $"Cập nhật phiên bản {nextVersionNum}"
            };

            _context.DocumentVersions.Add(newVersion);
            await _context.SaveChangesAsync();

            TempData["Message"] = $"Đã cập nhật lên phiên bản v{nextVersionNum}";
            return RedirectToAction(nameof(Details), new { id = documentId });
        }

        // ==========================================
        // DELETE (SOFT DELETE) - Chuyển vào thùng rác
        // ==========================================
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmedPost(int id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var document = await _context.Documents.FindAsync(id);

            if (document == null || document.OwnerId != userId) return NotFound();

            document.IsDeleted = true;
            await _context.SaveChangesAsync();

            TempData["Message"] = "Đã chuyển tệp tin vào thùng rác.";
            return RedirectToAction(nameof(Index), new { folderId = document.FolderId });
        }

        // ==========================================
        // RENAME
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Rename(int id, string newName)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var document = await _context.Documents.FindAsync(id);

            if (document == null || document.OwnerId != userId) return NotFound();
            if (string.IsNullOrWhiteSpace(newName)) return RedirectToAction(nameof(Index), new { folderId = document.FolderId });

            document.Title = newName; // Cập nhật tiêu đề hiển thị
            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Index), new { folderId = document.FolderId });
        }

        // ==========================================
        // DOWNLOAD & OPEN
        // ==========================================
        public async Task<IActionResult> Download(int id)
        {
            var document = await _context.Documents.FindAsync(id);
            if (document == null || document.IsDeleted) return NotFound();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!await HasAccessToDocumentAsync(id, userId)) return Forbid();

            var physicalPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", document.FilePath.TrimStart('/'));
            if (!System.IO.File.Exists(physicalPath)) return NotFound();

            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(document.FileName, out var contentType))
                contentType = "application/octet-stream";

            return PhysicalFile(physicalPath, contentType, document.FileName);
        }

        [AllowAnonymous]
        public async Task<IActionResult> Open(int id)
        {
            var document = await _context.Documents.FindAsync(id);
            if (document == null || document.IsDeleted) return NotFound();

            var physicalPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", document.FilePath.TrimStart('/'));
            if (!System.IO.File.Exists(physicalPath)) return NotFound();

            // If document is public preview for anonymous allowed types, allow anonymous access; otherwise enforce access
            var previewType = FilePreviewHelper.GetPreviewType(document.FileName, document.ContentType);
            if (User?.Identity?.IsAuthenticated != true)
            {
                // allow anonymous only for images and pdfs
                if (previewType == FilePreviewType.Image) return PhysicalFile(physicalPath, document.ContentType ?? "image/*");
                if (previewType == FilePreviewType.Pdf) return PhysicalFile(physicalPath, "application/pdf");

                // otherwise require auth
                return Challenge();
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!await HasAccessToDocumentAsync(id, userId)) return Forbid();

            return previewType switch
            {
                FilePreviewType.Image => PhysicalFile(physicalPath, document.ContentType ?? "image/*"),
                FilePreviewType.Pdf => PhysicalFile(physicalPath, "application/pdf"),
                _ => RedirectToAction(nameof(Download), new { id })
            };
        }
        // ================================================
        // SHARE (GET) - Hiển thị trang nhập Email chia sẻ
        // ================================================
        public async Task<IActionResult> Shared()
        {
            // 1. Lấy ID của người dùng hiện tại
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // 2a. Folder permissions (where folder exists and not deleted)
            var folderPerms = await _context.Permissions
                .Where(p => p.UserId == userId && p.FolderId != null)
                .Include(p => p.Folder)
                .OrderByDescending(p => p.SharedDate)
                .ToListAsync();

            // 2b. Document permissions (where document exists and not deleted)
            var docPerms = await _context.Permissions
                .Where(p => p.UserId == userId && p.DocumentId != null)
                .Include(p => p.Document)
                .OrderByDescending(p => p.SharedDate)
                .ToListAsync();

            // 3. Filter out deleted targets and combine, keeping newest SharedDate ordering per group
            var visibleFolders = folderPerms.Where(p => p.Folder != null && !p.Folder.IsDeleted).ToList();
            var visibleDocs = docPerms.Where(p => p.Document != null && !p.Document.IsDeleted).ToList();

            var combined = visibleDocs.Concat(visibleFolders)
                .OrderByDescending(p => p.SharedDate)
                .ToList();

            return View(combined);
        }

        // ==================================================
        // PROCESS SHARE (POST) - Lưu quyền chia sẻ vào DB
        // ==================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcessShare(int documentId, string receiverEmail, string accessType)
        {
            // 1. Tìm người nhận dựa trên Email
            var receiver = await _userManager.FindByEmailAsync(receiverEmail);
            if (receiver == null)
            {
                TempData["Error"] = "Không tìm thấy người dùng với email này!";
                return RedirectToAction(nameof(Shared), new { id = documentId });
            }

            // 2. Ép kiểu từ String sang Enum AccessLevel (Sửa lỗi convert bạn vừa gặp)
            if (!Enum.TryParse(accessType, out AccessLevel level))
            {
                level = AccessLevel.Read;
            }

            // 3. Kiểm tra xem đã chia sẻ cho người này chưa
            var exists = await _context.Permissions
                .AnyAsync(p => p.DocumentId == documentId && p.UserId == receiver.Id);

            if (exists)
            {
                TempData["Error"] = "Tài liệu này đã được chia sẻ cho người này rồi.";
                return RedirectToAction(nameof(Index));
            }

            // 4. Tạo bản ghi Permission mới
            var permission = new Permission
            {
                DocumentId = documentId,
                UserId = receiver.Id,
                AccessType = level,
                SharedDate = DateTime.Now
            };

            _context.Permissions.Add(permission);
            await _context.SaveChangesAsync();

            TempData["Message"] = $"Đã chia sẻ thành công tài liệu cho {receiverEmail}";
            return RedirectToAction(nameof(Index));
        }

        // ==========================================
        // TRASH - Thùng rác
        // ==========================================
        [Authorize]
        public async Task<IActionResult> Trash()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            var vm = new FileSystemViewModel();

            vm.Documents = await _context.Documents
                .Where(d => d.IsDeleted && d.OwnerId == userId)
                .Include(d => d.Folder)
                .OrderByDescending(d => d.UploadedDate)
                .ToListAsync();

            vm.Folders = await _context.Folders
                .Where(f => f.IsDeleted && f.OwnerId == userId)
                .OrderByDescending(f => f.CreatedDate)
                .ToListAsync();

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize]
        public async Task<IActionResult> RestoreSelected(string documentIds, string folderIds)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            var docIds = ParseIds(documentIds);
            var folIds = ParseIds(folderIds);

            if (docIds.Length > 0)
            {
                var docs = await _context.Documents.Where(d => docIds.Contains(d.Id) && d.OwnerId == userId).ToListAsync();
                foreach (var d in docs) d.IsDeleted = false;
            }

            if (folIds.Length > 0)
            {
                var folders = await _context.Folders.Where(f => folIds.Contains(f.Id) && f.OwnerId == userId).ToListAsync();
                foreach (var f in folders) await RestoreFolderRecursive(f);
            }

            await _context.SaveChangesAsync();
            TempData["Message"] = "Đã phục hồi mục đã chọn.";
            return RedirectToAction(nameof(Trash));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize]
        public async Task<IActionResult> DeletePermanently(string documentIds, string folderIds)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                var docIds = ParseIds(documentIds);
                var folIds = ParseIds(folderIds);

                if (docIds.Length > 0)
                {
                    var docs = await _context.Documents.Where(d => docIds.Contains(d.Id) && d.OwnerId == userId).ToListAsync();
                    foreach (var d in docs)
                    {
                        if (!string.IsNullOrWhiteSpace(d.FilePath))
                        {
                            var physical = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", d.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                            if (System.IO.File.Exists(physical)) System.IO.File.Delete(physical);
                        }

                        var versions = await _context.DocumentVersions.Where(v => v.DocumentId == d.Id).ToListAsync();
                        foreach (var v in versions)
                        {
                            if (!string.IsNullOrEmpty(v.FilePath))
                            {
                                var p = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", v.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                                if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
                            }
                            _context.DocumentVersions.Remove(v);
                        }

                        _context.Documents.Remove(d);
                    }
                }

                if (folIds.Length > 0)
                {
                    foreach (var fid in folIds)
                    {
                        var folder = await _context.Folders.Include(f => f.SubFolders).Include(f => f.Documents).FirstOrDefaultAsync(f => f.Id == fid && f.OwnerId == userId);
                        if (folder != null)
                        {
                            await PermanentlyDeleteFolderRecursive(folder);
                        }
                    }
                }

                await _context.SaveChangesAsync();
                await tx.CommitAsync();

                TempData["Message"] = "Đã xóa vĩnh viễn mục đã chọn.";
                return RedirectToAction(nameof(Trash));
            }
            catch
            {
                await tx.RollbackAsync();
                TempData["Error"] = "Xóa vĩnh viễn thất bại.";
                return RedirectToAction(nameof(Trash));
            }
        }

        // helper to parse comma separated ids or multiple values joined by commas
        private int[] ParseIds(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<int>();
            // if csv contains commas or repeated keys, normalize
            try
            {
                var parts = csv.Split(new[] { ',', '&' }, StringSplitOptions.RemoveEmptyEntries);
                var list = parts.SelectMany(p => p.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)).Select(p => p.Trim()).Where(s => !string.IsNullOrEmpty(s)).Select(s => { int.TryParse(s, out var v); return v; }).Where(v => v > 0).Distinct().ToArray();
                return list;
            }
            catch
            {
                return Array.Empty<int>();
            }
        }

        private async Task RestoreFolderRecursive(Folder folder)
        {
            folder.IsDeleted = false;
            await _context.Entry(folder).Collection(f => f.Documents).LoadAsync();
            foreach (var d in folder.Documents) d.IsDeleted = false;

            await _context.Entry(folder).Collection(f => f.SubFolders).LoadAsync();
            foreach (var s in folder.SubFolders) await RestoreFolderRecursive(s);
        }

        private async Task PermanentlyDeleteFolderRecursive(Folder folder)
        {
            await _context.Entry(folder).Collection(f => f.Documents).LoadAsync();
            foreach (var d in folder.Documents.ToList())
            {
                if (!string.IsNullOrWhiteSpace(d.FilePath))
                {
                    var physical = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", d.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                    if (System.IO.File.Exists(physical)) System.IO.File.Delete(physical);
                }

                var versions = await _context.DocumentVersions.Where(v => v.DocumentId == d.Id).ToListAsync();
                foreach (var v in versions)
                {
                    if (!string.IsNullOrEmpty(v.FilePath))
                    {
                        var p = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", v.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                        if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
                    }
                    _context.DocumentVersions.Remove(v);
                }

                _context.Documents.Remove(d);
            }

            await _context.Entry(folder).Collection(f => f.SubFolders).LoadAsync();
            foreach (var s in folder.SubFolders.ToList())
            {
                await PermanentlyDeleteFolderRecursive(s);
            }

            _context.Folders.Remove(folder);
        }

        // =========================================================
        // GET: Documents/Starred - redirect to StarController Index
        // =========================================================
        [Authorize]
        public IActionResult Starred()
        {
            return RedirectToAction("Index", "Star");
        }

        // GET: Documents/Star (alias) - redirect to /Star
        [Authorize]
        public IActionResult Star()
        {
            return RedirectToAction("Index", "Star");
        }

        // Helper: checks whether the given user has access to the folder (owner or permission on this or any ancestor)
        private async Task<bool> HasAccessToFolderAsync(int folderId, string userId)
        {
            int? currentId = folderId;
            while (currentId.HasValue)
            {
                var folder = await _context.Folders.FindAsync(currentId.Value);
                if (folder == null) return false;
                if (folder.OwnerId == userId) return true;
                bool hasPerm = await _context.Permissions.AnyAsync(p => p.FolderId == folder.Id && p.UserId == userId);
                if (hasPerm) return true;
                currentId = folder.ParentId;
            }

            // check root-level permissions (folderId == null) - not applicable here
            return false;
        }

        // Helper: checks whether user has access to a document via ownership, direct permission, or folder permission on ancestor
        private async Task<bool> HasAccessToDocumentAsync(int documentId, string userId)
        {
            var doc = await _context.Documents.FindAsync(documentId);
            if (doc == null) return false;
            if (doc.OwnerId == userId) return true;

            // direct permission on document
            var hasDocPerm = await _context.Permissions.AnyAsync(p => p.DocumentId == documentId && p.UserId == userId);
            if (hasDocPerm) return true;

            // check folder/ancestor permissions
            if (doc.FolderId.HasValue)
            {
                return await HasAccessToFolderAsync(doc.FolderId.Value, userId);
            }

            return false;
        }
    }
}