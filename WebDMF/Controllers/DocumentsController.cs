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
using WebDocumentManagement_FileSharing.Models.ViewModel;

namespace WebDocumentManagement_FileSharing.Controllers
{
    [Authorize]
    public class DocumentsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        public DocumentsController(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        // Sanitize a string to be safe as a folder name
        private string GetSafeFolderName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "unknown";
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(raw.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            cleaned = cleaned.Replace('@', '_').Replace(' ', '_');
            return cleaned.Trim();
        }

        // Resolve folder name for a user id: prefer email, otherwise use userId
        private async Task<string> GetFolderNameForUserIdAsync(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return "unknown";
            var user = await _userManager.FindByIdAsync(userId);
            if (user != null && !string.IsNullOrEmpty(user.Email))
            {
                return GetSafeFolderName(user.Email);
            }
            return GetSafeFolderName(userId);
        }

        private const long QuotaBytes = 500L * 1024 * 1024; // 500 MB per user

        // Sum sizes of all files in a folder (recursive). Returns 0 if folder missing or on error.
        private long GetUsedStorageForFolder(string folderPath)
        {
            try
            {
                if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return 0;
                var dir = new DirectoryInfo(folderPath);
                return dir.GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch
            {
                return 0;
            }
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
            AccessLevel? folderAccess = null;
            if (actualFolderId.HasValue)
            {
                folderAccess = await GetEffectiveAccessForUserOnFolderAsync(actualFolderId.Value, userId);
                hasAccessToFolder = (folderAccess != null);
                viewModel.CurrentFolderAccess = folderAccess;
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

                // compute effective access per child document/folder for the current user when viewing shared folder
                foreach (var d in viewModel.Documents)
                {
                    var a = await GetEffectiveAccessForUserOnDocumentAsync(d.Id, userId);
                    viewModel.DocumentAccess[d.Id] = a;
                }
                foreach (var f in viewModel.Folders)
                {
                    var a = await GetEffectiveAccessForUserOnFolderAsync(f.Id, userId);
                    viewModel.FolderAccess[f.Id] = a;
                }
            }

            return View(viewModel);
        }

        // ==========================================
        // SEARCH - Tìm kiếm theo tên thư mục, tên tệp hoặc email người tải lên
        // Trả về View Index với kết quả lọc
        // ==========================================
        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> Search(string q)
        {
            // Accept alternative query name used in some layouts
            if (string.IsNullOrWhiteSpace(q)) q = Request.Query["query"].ToString();
            if (string.IsNullOrWhiteSpace(q)) return RedirectToAction(nameof(Index));

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

            // find user IDs whose email matches query (for email-based search)
            var matchingUserIds = await _userManager.Users
                .Where(u => u.Email != null && EF.Functions.Like(u.Email, $"%{q}%"))
                .Select(u => u.Id)
                .ToListAsync();

            // Folders: match by name OR owner email
            var folders = await _context.Folders
                .Where(f => !f.IsDeleted && (
                    EF.Functions.Like(f.Name, $"%{q}%")
                    || matchingUserIds.Contains(f.OwnerId)
                ))
                .OrderBy(f => f.Name)
                .ToListAsync();

            // Documents: match by filename OR owner email
            var documents = await _context.Documents
                .Where(d => !d.IsDeleted && (
                    EF.Functions.Like(d.FileName, $"%{q}%")
                    || matchingUserIds.Contains(d.OwnerId)
                ))
                .OrderByDescending(d => d.UploadedDate)
                .ToListAsync();

            var vm = new FileSystemViewModel
            {
                CurrentFolderId = null,
                Folders = folders,
                Documents = documents
            };

            // Compute effective access for listed items for current user
            foreach (var d in vm.Documents)
            {
                vm.DocumentAccess[d.Id] = await GetEffectiveAccessForUserOnDocumentAsync(d.Id, userId);
            }
            foreach (var f in vm.Folders)
            {
                vm.FolderAccess[f.Id] = await GetEffectiveAccessForUserOnFolderAsync(f.Id, userId);
            }

            // If no exact matches, prepare fuzzy suggestions (closest names)
            if ((vm.Folders == null || !vm.Folders.Any()) && (vm.Documents == null || !vm.Documents.Any()))
            {
                var qLower = q.ToLowerInvariant();

                // candidate folders
                var folderCandidates = await _context.Folders
                    .Where(f => !f.IsDeleted)
                    .Select(f => new { f.Id, f.Name })
                    .ToListAsync();

                var folderSuggest = folderCandidates
                    .Select(c => new { c.Id, c.Name, Dist = Levenshtein(c.Name?.ToLowerInvariant() ?? string.Empty, qLower) })
                    .OrderBy(x => x.Dist).ThenBy(x => x.Name)
                    .Take(5)
                    .ToList();

                ViewBag.SuggestFolders = folderSuggest;

                // candidate documents
                var docCandidates = await _context.Documents
                    .Where(d => !d.IsDeleted)
                    .Select(d => new { d.Id, d.FileName })
                    .ToListAsync();

                var docSuggest = docCandidates
                    .Select(c => new { Id = c.Id, Name = c.FileName, Dist = Levenshtein((c.FileName ?? string.Empty).ToLowerInvariant(), qLower) })
                    .OrderBy(x => x.Dist).ThenBy(x => x.Name)
                    .Take(8)
                    .ToList();

                ViewBag.SuggestDocs = docSuggest;
            }

            ViewBag.SearchQuery = q;
            return View("Index", vm);
        }

        // Simple Levenshtein distance for fuzzy matching suggestions
        private static int Levenshtein(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;

            var la = a.Length;
            var lb = b.Length;
            var d = new int[la + 1, lb + 1];

            for (int i = 0; i <= la; i++) d[i, 0] = i;
            for (int j = 0; j <= lb; j++) d[0, j] = j;

            for (int i = 1; i <= la; i++)
            {
                for (int j = 1; j <= lb; j++)
                {
                    int cost = (b[j - 1] == a[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }

            return d[la, lb];
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

            // Set IsPremium flag for the current viewer (if authenticated)
            ViewBag.IsPremium = false;
            if (!string.IsNullOrEmpty(userId))
            {
                var currentUser = await _userManager.FindByIdAsync(userId);
                if (currentUser != null) ViewBag.IsPremium = currentUser.IsPremium;
            }

            return View(document);
        }

        // ==========================================
        // UPLOAD (GET) - Hiển thị trang chọn tệp tải lên
        // ==========================================
        [HttpGet]
        public IActionResult Upload(int? folderId, string? returnUrl = null)
        {
            // Chuẩn bị danh sách thư mục để người dùng chọn (nếu muốn đổi thư mục khi upload)
            ViewData["FolderId"] = new SelectList(_context.Folders.OrderBy(f => f.Name), "Id", "Name", folderId);

            ViewBag.ReturnUrl = returnUrl;

            // Trả về View cùng với một Model mới, gán sẵn FolderId nếu có
            return View(new Document { FolderId = folderId == 0 ? null : folderId });
        }

        // ==========================================
        // UPLOAD (POST) - Tải lên tệp lần đầu (v1)
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Upload(Document document, IFormFile fileUpload, string? returnUrl = null)
        {
            if (fileUpload == null || fileUpload.Length == 0)
            {
                TempData["Error"] = "Vui lòng chọn tệp hợp lệ.";
                return RedirectToAction(nameof(Index), new { folderId = document.FolderId });
            }

            // --- [BẮT ĐẦU ĐOẠN KIỂM TRA MỚI] ---
            var userFileExt = Path.GetExtension(fileUpload.FileName).ToLower();
            var allowedExtensions = await GetAllowedExtensions();

            if (!allowedExtensions.Contains(userFileExt))
            {
                TempData["Error"] = $"Định dạng tệp '{userFileExt}' bị chặn. Admin chỉ cho phép: {string.Join(", ", allowedExtensions)}";
                return RedirectToAction(nameof(Index), new { folderId = document.FolderId });
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

            // normalize FolderId: treat 0 as null (root). Do this before permission checks.
            if (document.FolderId.HasValue && document.FolderId.Value == 0)
            {
                document.FolderId = null;
            }
            
            // If uploading into an existing folder, enforce permission: owner or Edit required
            if (document.FolderId.HasValue)
            {
                // Explicit check: allow owner always; if not owner require an explicit Permission on that folder/ancestor and it must be Edit
                var folder = await _context.Folders.FindAsync(document.FolderId.Value);
                if (folder == null)
                {
                    TempData["Error"] = "Thư mục không tồn tại.";
                    return RedirectToAction(nameof(Index), new { folderId = document.FolderId });
                }

                if (folder.OwnerId != userId)
                {
                    // check for explicit permission (document-level perms not relevant here) on this folder or nearest ancestor
                    var perm = await _context.Permissions.FirstOrDefaultAsync(p => p.UserId == userId && p.FolderId == folder.Id);
                    if (perm == null)
                    {
                        // look up ancestors for a permission
                        int? current = folder.ParentId;
                        while (current.HasValue)
                        {
                            var aPerm = await _context.Permissions.FirstOrDefaultAsync(p => p.UserId == userId && p.FolderId == current.Value);
                            if (aPerm != null) { perm = aPerm; break; }
                            var parent = await _context.Folders.FindAsync(current.Value);
                            if (parent == null) break;
                            current = parent.ParentId;
                        }
                    }

                    if (perm == null || perm.AccessType != AccessLevel.Edit)
                    {
                        TempData["Error"] = "Bạn cần quyền 'Edit' để tải lên vào thư mục này.";
                        return RedirectToAction(nameof(Index), new { folderId = document.FolderId });
                    }
                }
            }

            // 1. Save file into per-user folder (use sanitized email as folder name)
            var folderName = await GetFolderNameForUserIdAsync(userId);
            var uploadsRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
            var userUploadsFolder = Path.Combine(uploadsRoot, folderName);
            // Check quota before saving: compute current used bytes on disk for this user's folder
            var usedOnDisk = GetUsedStorageForFolder(userUploadsFolder);
            if (usedOnDisk + fileUpload.Length > QuotaBytes)
            {
                TempData["Error"] = "Bộ nhớ đã vượt quá giới hạn 500 MB. Vui lòng xóa bớt hoặc liên hệ quản trị viên.";
                return RedirectToAction(nameof(Index), new { folderId = document.FolderId });
            }
            if (!Directory.Exists(userUploadsFolder)) Directory.CreateDirectory(userUploadsFolder);

            var originalFileName = Path.GetFileName(fileUpload.FileName);
            var extension = Path.GetExtension(originalFileName);
            var storedFileName = $"{Guid.NewGuid()}{extension}";
            var physicalPath = Path.Combine(userUploadsFolder, storedFileName);

            using (var stream = new FileStream(physicalPath, FileMode.Create))
            {
                await fileUpload.CopyToAsync(stream);
            }

            // 2. Lưu thông tin Document chính
            document.Title ??= Path.GetFileNameWithoutExtension(originalFileName);
            document.FileName = originalFileName;
            document.FilePath = $"/uploads/{folderName}/{storedFileName}";
            document.Extension = extension.TrimStart('.');
            document.FileSize = fileUpload.Length;
            document.ContentType = fileUpload.ContentType;
            document.UploadedDate = DateTime.Now;
            document.OwnerId = userId ?? "";
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

            // Audit: document upload
            await AuditHelper.LogAsync(HttpContext, "UPLOAD_DOCUMENT", "Document", document.Id, document.FileName, $"Uploaded document '{document.FileName}' ({document.FileSize} bytes)");

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

            var userFileExt = Path.GetExtension(fileUpload.FileName).ToLower();
            var allowedExtensions = await GetAllowedExtensions();
            if (!allowedExtensions.Contains(userFileExt))
            {
                TempData["Error"] = $"Không thể cập nhật phiên bản mới. Định dạng '{userFileExt}' bị chặn bởi Admin.";
                return RedirectToAction(nameof(Details), new { id = documentId });
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var access = await GetEffectiveAccessForUserOnDocumentAsync(documentId, userId);

            // allow only owner or Edit permission to upload new versions
            if (document.OwnerId != userId && access != AccessLevel.Edit)
            {
                return Forbid();
            }

            var originalFileName = Path.GetFileName(fileUpload.FileName);
            var extension = Path.GetExtension(originalFileName);
            var storedFileName = $"{Guid.NewGuid()}{extension}";

            // determine owner's folder name and ensure directory exists
            var ownerId = !string.IsNullOrEmpty(document.OwnerId) ? document.OwnerId : userId;
            var ownerFolderName = await GetFolderNameForUserIdAsync(ownerId);
            var uploadsRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
            var ownerUploadsFolder = Path.Combine(uploadsRoot, ownerFolderName);
            if (!Directory.Exists(ownerUploadsFolder)) Directory.CreateDirectory(ownerUploadsFolder);

            var physicalPath = Path.Combine(ownerUploadsFolder, storedFileName);

            using (var stream = new FileStream(physicalPath, FileMode.Create))
            {
                await fileUpload.CopyToAsync(stream);
            }

            // Cập nhật thông tin bản mới nhất vào Document chính
            document.FilePath = $"/uploads/{ownerFolderName}/{storedFileName}";
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

            // Audit: new version uploaded
            await AuditHelper.LogAsync(HttpContext, "UPLOAD_VERSION", "Document", document.Id, document.FileName, $"Uploaded new version v{nextVersionNum} for '{document.FileName}'");

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

            if (document == null) return NotFound();

            // allow owner or users with Edit access to soft-delete
            if (document.OwnerId != userId)
            {
                var access = await GetEffectiveAccessForUserOnDocumentAsync(id, userId);
                if (access != AccessLevel.Edit)
                    return Forbid();
            }

            document.IsDeleted = true;
            await _context.SaveChangesAsync();

            // Audit: soft delete
            await AuditHelper.LogAsync(HttpContext, "DELETE_DOCUMENT_SOFT", "Document", document.Id, document.FileName, $"Soft-deleted document '{document.FileName}'");

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

            if (document == null) return NotFound();
            if (string.IsNullOrWhiteSpace(newName)) return RedirectToAction(nameof(Index), new { folderId = document?.FolderId });

            // allow owner or users with Edit access to rename
            if (document.OwnerId != userId)
            {
                var access = await GetEffectiveAccessForUserOnDocumentAsync(id, userId);
                if (access != AccessLevel.Edit)
                    return Forbid();
            }

            // preserve original extension
            var originalExt = Path.GetExtension(document.FileName) ?? string.Empty;
            var inputName = Path.GetFileName(newName);
            string newFileName;
            if (Path.HasExtension(inputName))
            {
                // if user provided an extension, use it only if it matches original; otherwise keep original
                var inputExt = Path.GetExtension(inputName);
                if (!string.Equals(inputExt, originalExt, StringComparison.OrdinalIgnoreCase))
                {
                    // ignore provided extension and keep original to avoid mismatches
                    newFileName = Path.GetFileNameWithoutExtension(inputName) + originalExt;
                }
                else
                {
                    newFileName = inputName;
                }
            }
            else
            {
                newFileName = inputName + originalExt;
            }

            // check duplicate filename in same folder for the owner
            var ownerId = document.OwnerId;
            bool exists = await _context.Documents.AnyAsync(d => d.FolderId == document.FolderId && d.FileName == newFileName && d.Id != id && d.OwnerId == ownerId && !d.IsDeleted);
            if (exists)
            {
                TempData["Error"] = "Đã tồn tại tệp có cùng tên trong thư mục này.";
                return RedirectToAction(nameof(Index), new { folderId = document.FolderId });
            }

            var oldName = document.FileName;
            document.FileName = newFileName;
            document.Title = Path.GetFileNameWithoutExtension(newFileName);
            await _context.SaveChangesAsync();

            // Audit: rename document (use file name change)
            await AuditHelper.LogAsync(HttpContext, "RENAME_DOCUMENT", "Document", document.Id, document.FileName, $"Renamed document from '{oldName}' to '{newFileName}'");

            TempData["Message"] = "Đổi tên tệp thành công.";
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
            var access = await GetEffectiveAccessForUserOnDocumentAsync(id, userId);
            if (document.OwnerId != userId)
            {
                // require Download or Edit
                if (access == null || (access != AccessLevel.Download && access != AccessLevel.Edit)) return Forbid();
            }

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
            var access = await GetEffectiveAccessForUserOnDocumentAsync(id, userId);
            if (document.OwnerId != userId && access == null)
            {
                return Forbid();
            }

            // If user has only Read permission, allow inline preview for images, pdfs, videos and office preview (redirect to Preview for office)
            if (access != null && access == AccessLevel.Read)
            {
                if (previewType == FilePreviewType.Image) return PhysicalFile(physicalPath, document.ContentType ?? "image/*");
                if (previewType == FilePreviewType.Pdf) return PhysicalFile(physicalPath, "application/pdf");
                if (previewType == FilePreviewType.Video) return PhysicalFile(physicalPath, document.ContentType ?? "video/mp4");
                if (previewType == FilePreviewType.Office) return RedirectToAction(nameof(Preview), new { id });
                // for other types deny full download/preview
                return Forbid();
            }

            // For images and pdfs return inline; for other previewable files redirect to Preview action
            return previewType switch
            {
                FilePreviewType.Image => PhysicalFile(physicalPath, document.ContentType ?? "image/*"),
                FilePreviewType.Pdf => PhysicalFile(physicalPath, "application/pdf"),
                _ => RedirectToAction(nameof(Preview), new { id })
            };
        }

        // New: Stream endpoint for preview JS to fetch file blob without forcing download
        [AllowAnonymous]
        public async Task<IActionResult> Stream(int id, string? token = null)
        {
            var document = await _context.Documents.FindAsync(id);
            if (document == null || document.IsDeleted) return NotFound();

            var physicalPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", document.FilePath.TrimStart('/'));
            if (!System.IO.File.Exists(physicalPath)) return NotFound();

            // Determine preview type
            var previewType = FilePreviewHelper.GetPreviewType(document.FileName, document.ContentType);

            // Token-based share handling
            ShareLink? link = null;
            if (!string.IsNullOrEmpty(token))
            {
                link = await _context.ShareLinks.FirstOrDefaultAsync(s => s.Token == token);
                if (link == null)
                    return RedirectToPage("/Account/AccessDenied", new { area = "Identity" });

                // Verify target matches
                var targetMatches = false;
                if (string.Equals(link.TargetType, "document", StringComparison.OrdinalIgnoreCase) && link.TargetId == id)
                {
                    targetMatches = true;
                }
                else if (string.Equals(link.TargetType, "folder", StringComparison.OrdinalIgnoreCase))
                {
                    int? curFolderId = document.FolderId;
                    while (curFolderId.HasValue)
                    {
                        if (curFolderId.Value == link.TargetId) { targetMatches = true; break; }
                        var parent = await _context.Folders.FindAsync(curFolderId.Value);
                        if (parent == null) break;
                        curFolderId = parent.ParentId;
                    }
                }

                if (!targetMatches)
                    return RedirectToPage("/Account/AccessDenied", new { area = "Identity" });

                // If link requires login
                if (!link.IsPublic)
                {
                    if (!(User?.Identity?.IsAuthenticated == true))
                    {
                        return Challenge();
                    }
                }

                // If link grants only Read, restrict to previewable types
                if (link.AccessType == AccessLevel.Read)
                {
                    if (!(previewType == FilePreviewType.Image || previewType == FilePreviewType.Pdf || previewType == FilePreviewType.Video || previewType == FilePreviewType.Office))
                        return Forbid();
                }
                // else Download/Edit allow full stream
            }
            else
            {
                // Non-token: require authentication and permissions
                if (!(User?.Identity?.IsAuthenticated == true)) return Challenge();

                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (document.OwnerId != userId)
                {
                    var access = await GetEffectiveAccessForUserOnDocumentAsync(id, userId);
                    if (access == null) return Forbid();
                    if (access == AccessLevel.Read)
                    {
                        if (!(previewType == FilePreviewType.Image || previewType == FilePreviewType.Pdf || previewType == FilePreviewType.Video || previewType == FilePreviewType.Office))
                            return Forbid();
                    }
                }
            }

            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(document.FileName, out var contentType))
                contentType = document.ContentType ?? "application/octet-stream";

            // Return inline stream (do not set download file name so browsers will attempt inline display)
            return PhysicalFile(physicalPath, contentType);
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

            // 4. Also compute items that *I* have shared (where current user is owner of document or folder)
            var sharedByMe = await _context.Permissions
                .Include(p => p.Document)
                .Include(p => p.Folder)
                .Where(p => (p.Document != null && p.Document.OwnerId == userId) || (p.Folder != null && p.Folder.OwnerId == userId))
                .OrderByDescending(p => p.SharedDate)
                .ToListAsync();

            var vm = new SharedListingsViewModel
            {
                SharedWithMe = combined,
                SharedByMe = sharedByMe
            };

            return View(vm);
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
                    // only permanently delete documents that are in trash
                    var docs = await _context.Documents.Where(d => docIds.Contains(d.Id) && d.OwnerId == userId && d.IsDeleted).ToListAsync();
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
                    // For each selected folder, only consider folders that are in trash (IsDeleted==true)
                    foreach (var fid in folIds)
                    {
                        var folder = await _context.Folders.FirstOrDefaultAsync(f => f.Id == fid && f.OwnerId == userId && f.IsDeleted);
                        if (folder == null) continue;

                        // collect subtree folder ids via BFS but only traverse deleted folders
                        var subtree = new List<int>();
                        var stack = new Stack<int>();
                        stack.Push(folder.Id);
                        while (stack.Count > 0)
                        {
                            var cur = stack.Pop();
                            subtree.Add(cur);
                            var children = await _context.Folders.Where(f => f.ParentId == cur && f.IsDeleted).Select(f => f.Id).ToListAsync();
                            foreach (var c in children) stack.Push(c);
                        }

                        // collect documents in subtree that are deleted
                        var docsInSub = await _context.Documents.Where(d => d.FolderId != null && subtree.Contains(d.FolderId.Value) && d.IsDeleted).Select(d => d.Id).ToListAsync();

                        // remove permissions pointing to these folders or documents
                        var permsToRemove = await _context.Permissions.Where(p => (p.FolderId != null && subtree.Contains(p.FolderId.Value)) || (p.DocumentId != null && docsInSub.Contains(p.DocumentId.Value))).ToListAsync();
                        if (permsToRemove.Any()) _context.Permissions.RemoveRange(permsToRemove);

                        // reload folder with children/docs for deletion
                        var folderForDelete = await _context.Folders.Include(f => f.SubFolders).Include(f => f.Documents).FirstOrDefaultAsync(f => f.Id == fid && f.OwnerId == userId && f.IsDeleted);
                        if (folderForDelete != null) await PermanentlyDeleteFolderRecursive(folderForDelete);
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

        // Get effective AccessLevel for a user on a folder (owner -> Edit, else permission on folder or any ancestor)
        private async Task<AccessLevel?> GetEffectiveAccessForUserOnFolderAsync(int folderId, string userId)
        {
            if (string.IsNullOrEmpty(userId)) return null;
            var folder = await _context.Folders.FindAsync(folderId);
            if (folder == null) return null;
            if (folder.OwnerId == userId) return AccessLevel.Edit;

            int? currentId = folderId;
            AccessLevel? best = null;
            while (currentId.HasValue)
            {
                var perm = await _context.Permissions.FirstOrDefaultAsync(p => p.FolderId == currentId.Value && p.UserId == userId);
                if (perm != null)
                {
                    best = perm.AccessType; // a direct permission on deepest ancestor applies
                    break; // stop at nearest ancestor that grants permission
                }
                var f = await _context.Folders.FindAsync(currentId.Value);
                if (f == null) break;
                currentId = f.ParentId;
            }

            return best;
        }

        // Get effective AccessLevel for a user on a document (owner -> Edit, else direct document perm, else folder perms)
        private async Task<AccessLevel?> GetEffectiveAccessForUserOnDocumentAsync(int documentId, string userId)
        {
            if (string.IsNullOrEmpty(userId)) return null;
            var doc = await _context.Documents.FindAsync(documentId);
            if (doc == null) return null;
            if (doc.OwnerId == userId) return AccessLevel.Edit;

            var docPerm = await _context.Permissions.FirstOrDefaultAsync(p => p.DocumentId == documentId && p.UserId == userId);
            if (docPerm != null) return docPerm.AccessType;

            if (doc.FolderId.HasValue)
            {
                return await GetEffectiveAccessForUserOnFolderAsync(doc.FolderId.Value, userId);
            }

            return null;
        }

        // ==========================================
        // DELETE (GET) - show confirmation when document is shared with others or perform immediate soft-delete when not shared
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Delete(int id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var document = await _context.Documents.FindAsync(id);

            if (document == null)
            {
                TempData["Error"] = "Tài liệu không tồn tại.";
                return RedirectToAction(nameof(Index));
            }

            // enforce access: owner or users with Edit can delete
            if (document.OwnerId != userId)
            {
                var access = await GetEffectiveAccessForUserOnDocumentAsync(id, userId);
                if (access != AccessLevel.Edit)
                {
                    TempData["Error"] = "Bạn không có quyền xóa tài liệu này.";
                    return RedirectToAction(nameof(Index), new { folderId = document.FolderId });
                }
            }

            // find permissions for this document that belong to other users
            var sharedPerms = await _context.Permissions.Where(p => p.DocumentId == id && p.UserId != document.OwnerId).ToListAsync();

            if (sharedPerms.Any())
            {
                ViewBag.SharedWith = await _context.Users.Where(u => sharedPerms.Select(p => p.UserId).Contains(u.Id)).Select(u => u.Email ?? u.UserName).ToListAsync();
                return View(document); // Views/Documents/Delete.cshtml
            }

            // not shared: perform soft-delete immediately and remove permissions
            document.IsDeleted = true;
            try
            {
                var perms = await _context.Permissions.Where(p => p.DocumentId == id).ToListAsync();
                if (perms.Any()) _context.Permissions.RemoveRange(perms);
            }
            catch { }

            await _context.SaveChangesAsync();
            TempData["Message"] = "Đã chuyển tệp tin vào thùng rác.";
            return RedirectToAction(nameof(Index), new { folderId = document.FolderId });
        }


            
        [AllowAnonymous]
        public async Task<IActionResult> Preview(int id, string? token = null)
        {
            var document = await _context.Documents.FindAsync(id);
            if (document == null || document.IsDeleted) return NotFound();

            // 1. Kiểm tra quyền truy cập qua Token (Share Link)
            if (!string.IsNullOrEmpty(token))
            {
                var link = await _context.ShareLinks.FirstOrDefaultAsync(s => s.Token == token);
                if (link == null)
                    return RedirectToPage("/Account/AccessDenied", new { area = "Identity" });

                // Kiểm tra xem link này có trỏ đúng vào document này hoặc thư mục cha của nó không
                var targetMatches = false;
                if (string.Equals(link.TargetType, "document", StringComparison.OrdinalIgnoreCase) && link.TargetId == id)
                {
                    targetMatches = true;
                }
                else if (string.Equals(link.TargetType, "folder", StringComparison.OrdinalIgnoreCase))
                {
                    int? curFolderId = document.FolderId;
                    while (curFolderId.HasValue)
                    {
                        if (curFolderId.Value == link.TargetId) { targetMatches = true; break; }
                        var parent = await _context.Folders.FindAsync(curFolderId.Value);
                        if (parent == null) break;
                        curFolderId = parent.ParentId;
                    }
                }

                if (!targetMatches)
                    return RedirectToPage("/Account/AccessDenied", new { area = "Identity" });

                // Nếu link yêu cầu đăng nhập (IsPublic = false)
                if (!link.IsPublic)
                {
                    if (!(User?.Identity?.IsAuthenticated == true))
                    {
                        var returnUrl = Url.Action("Preview", "Documents", new { id = id, token = token });
                        return RedirectToPage("/Account/Login", new { area = "Identity", ReturnUrl = returnUrl });
                    }
                }
            }
            // 2. Nếu không có token, kiểm tra quyền sở hữu hoặc quyền được chia sẻ trực tiếp
            else
            {
                if (!(User?.Identity?.IsAuthenticated == true))
                {
                    var returnUrl = Url.Action("Preview", "Documents", new { id = id });
                    return RedirectToPage("/Account/Login", new { area = "Identity", ReturnUrl = returnUrl });
                }

                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (document.OwnerId != userId)
                {
                    var hasAccess = await HasAccessToDocumentAsync(id, userId);
                    if (!hasAccess)
                    {
                        return RedirectToPage("/Account/AccessDenied", new { area = "Identity" });
                    }
                }
            }

            // --- PHẦN QUAN TRỌNG ĐỂ CHẠY LOCAL PREVIEW ---

            // Gán dữ liệu vào ViewBag để View LocalPreview có thể sử dụng
            ViewBag.DocumentId = id;
            ViewBag.DocumentName = document.FileName;

            // Đường dẫn file để JS fetch dữ liệu 
            // Use Stream endpoint so preview page can fetch blob even when Download enforces attachment
            ViewBag.Token = token;
            ViewBag.FileUrl = Url.Action("Stream", "Documents", new { id = id, token = token });

            // Trả về View Local Preview 
            return View("Preview");
        }

        // Backwards-compatibility: keep /Documents/Create route and redirect to /Documents/Upload
        [HttpGet]
        public IActionResult Create(int? folderId, string? returnUrl = null)
        {
            return RedirectToAction(nameof(Upload), new { folderId = folderId, returnUrl = returnUrl });
        }

        // Hàm phụ trợ: Lấy danh sách đuôi file cho phép từ Database
        private async Task<List<string>> GetAllowedExtensions()
        {
            // 1. Tìm setting trong bảng SystemSettings
            var setting = await _context.SystemSettings
                                        .FirstOrDefaultAsync(s => s.SettingKey == "AllowedExtensions");

            // 2. Nếu chưa có hoặc trống -> Dùng danh sách mặc định an toàn
            if (setting == null || string.IsNullOrWhiteSpace(setting.SettingValue))
            {
                return new List<string> { ".pdf", ".docx", ".xlsx", ".jpg", ".png" };
            }

            // 3. Tách chuỗi từ DB (ví dụ: ".pdf,.docx") thành danh sách
            return setting.SettingValue
                          .Split(',')
                          .Select(x => x.Trim().ToLower()) // Xóa khoảng trắng, chuyển về chữ thường
                          .Where(x => !string.IsNullOrEmpty(x))
                          .ToList();
        }
    }
}