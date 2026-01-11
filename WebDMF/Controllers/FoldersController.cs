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
using System.IO.Compression;
using System.Collections.Generic;
using WebDocumentManagement_FileSharing.Helpers;

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

        // sanitize a string to be safe as folder name
        private string GetSafeFolderName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "unknown";
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(raw.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            cleaned = cleaned.Replace('@', '_').Replace(' ', '_');
            return cleaned.Trim();
        }

        // ======================================================
        // INDEX (redirect to Documents Index for folder view)
        // ======================================================
        public IActionResult Index(int? folderId)
        {
            if (folderId.HasValue && folderId.Value != 0)
                return RedirectToAction("Index", "Documents", new { folderId = folderId.Value });

            return RedirectToAction("Index", "Home");
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

            // Audit log: folder created (Vietnamese friendly)
            var creator = User?.Identity?.Name ?? userId;
            await AuditHelper.LogAsync(HttpContext, "CREATE_FOLDER", "Folder", folder.Id, folder.Name, $"Người dùng '{creator}' đã tạo thư mục '{folder.Name}'.");

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

        // ======================================================
        // DETAILS - show folder detail page
        // ======================================================
        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            var folder = await _context.Folders
                .Include(f => f.ParentFolder)
                .Include(f => f.SubFolders)
                .Include(f => f.Documents)
                .FirstOrDefaultAsync(f => f.Id == id && !f.IsDeleted);

            if (folder == null) return NotFound();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            // allow owner or explicit permission on folder
            if (folder.OwnerId != userId)
            {
                var hasPerm = await _context.Permissions.AnyAsync(p => p.FolderId == id && p.UserId == userId);
                if (!hasPerm)
                {
                    // if anonymous or no permission, challenge/login
                    if (!(User?.Identity?.IsAuthenticated == true))
                        return RedirectToPage("/Account/Login", new { area = "Identity", ReturnUrl = Url.Action("Details", "Folders", new { id }) });

                    return Forbid();
                }
            }

            // Resolve owner email for display
            try
            {
                var owner = await _context.Users.FirstOrDefaultAsync(u => u.Id == folder.OwnerId);
                ViewBag.OwnerEmail = owner?.Email ?? owner?.UserName ?? folder.OwnerId;
            }
            catch
            {
                ViewBag.OwnerEmail = folder.OwnerId;
            }
            
            return View(folder);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Rename(int id, string newName)
        {
            if (id <= 0) return NotFound();

            if (string.IsNullOrWhiteSpace(newName))
            {
                TempData["Error"] = "Tên mới không hợp lệ.";
                return RedirectToAction("Index", new { folderId = (int?)null });
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var folder = await _context.Folders.FirstOrDefaultAsync(f => f.Id == id && f.OwnerId == userId);

            if (folder == null) return NotFound();

            bool exists = await _context.Folders.AnyAsync(f =>
                f.ParentId == folder.ParentId &&
                f.Name == newName &&
                f.Id != id &&
                f.OwnerId == userId &&
                !f.IsDeleted);

            if (exists)
            {
                TempData["Error"] = "Tên thư mục đã tồn tại.";
                return RedirectAfterFolderAction(folder.ParentId);
            }

            var oldName = folder.Name;
            folder.Name = newName;
            await _context.SaveChangesAsync();

            var renamer = User?.Identity?.Name ?? userId;
            await AuditHelper.LogAsync(HttpContext, "RENAME_FOLDER", "Folder", folder.Id, folder.Name, $"Người dùng '{renamer}' đã đổi tên thư mục từ '{oldName}' thành '{folder.Name}'.");

            TempData["Message"] = "Đổi tên thư mục thành công.";
            return RedirectAfterFolderAction(folder.ParentId);
        }

        // ======================================================
        // DELETE (SOFT DELETE RECURSIVE)
        // ======================================================
        // GET: show confirmation when folder is shared with others or perform immediate soft-delete when not shared
        [HttpGet]
        public async Task<IActionResult> Delete(int id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // load folder owned by user
            var folder = await _context.Folders
                .Include(f => f.SubFolders)
                .Include(f => f.Documents)
                .FirstOrDefaultAsync(f => f.Id == id && f.OwnerId == userId);

            if (folder == null)
            {
                TempData["Error"] = "Thư mục không tồn tại hoặc bạn không có quyền xóa.";
                return RedirectToAction("Index", "Home");
            }

            // collect subtree ids
            var subtreeFolderIds = new List<int>();
            async Task CollectFolderIds(int fid)
            {
                subtreeFolderIds.Add(fid);
                var children = await _context.Folders.Where(f => f.ParentId == fid && !f.IsDeleted).Select(f => f.Id).ToListAsync();
                foreach (var c in children) await CollectFolderIds(c);
            }
            await CollectFolderIds(folder.Id);

            // collect document ids in subtree
            var docIds = await _context.Documents.Where(d => d.FolderId != null && subtreeFolderIds.Contains(d.FolderId.Value)).Select(d => d.Id).ToListAsync();

            // find permissions referencing subtree that belong to other users
            var sharedPerms = await _context.Permissions.Where(p => (p.FolderId != null && subtreeFolderIds.Contains(p.FolderId.Value) || p.DocumentId != null && docIds.Contains(p.DocumentId.Value)) && p.UserId != userId).ToListAsync();

            if (sharedPerms.Any())
            {
                ViewBag.FolderId = id;
                ViewBag.FolderName = folder.Name;
                ViewBag.SharedWith = await _context.Users.Where(u => sharedPerms.Select(p => p.UserId).Contains(u.Id)).Select(u => u.Email ?? u.UserName).ToListAsync();
                return View(folder); // Views/Folders/Delete.cshtml will present options
            }

            // not shared: perform soft-delete immediately and remove permissions referencing subtree
            await SoftDeleteFolderRecursive(folder);
            try
            {
                var permsForFolders = await _context.Permissions.Where(p => p.FolderId != null && subtreeFolderIds.Contains(p.FolderId.Value)).ToListAsync();
                if (permsForFolders.Any()) _context.Permissions.RemoveRange(permsForFolders);

                if (docIds != null && docIds.Count > 0)
                {
                    var permsForDocs = await _context.Permissions.Where(p => p.DocumentId != null && docIds.Contains(p.DocumentId.Value)).ToListAsync();
                    if (permsForDocs.Any()) _context.Permissions.RemoveRange(permsForDocs);
                }
            }
            catch { }

            await _context.SaveChangesAsync();

            // Audit: soft delete (Vietnamese friendly)
            var deleter = User?.Identity?.Name ?? userId;
            await AuditHelper.LogAsync(HttpContext, "DELETE_FOLDER_SOFT", "Folder", folder.Id, folder.Name, $"Người dùng '{deleter}' đã chuyển thư mục '{folder.Name}' vào thùng rác. (Thư mục con: {subtreeFolderIds.Count}, tệp: {docIds.Count})");

            TempData["Message"] = "Đã chuyển thư mục và nội dung bên trong vào thùng rác.";
            return RedirectAfterFolderAction(folder.ParentId);
        }

        [HttpPost]
        [ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
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

            // Collect all folder ids in subtree
            var subtreeFolderIds = new List<int>();
            async Task CollectFolderIds(int fid)
            {
                subtreeFolderIds.Add(fid);
                var children = await _context.Folders.Where(f => f.ParentId == fid && !f.IsDeleted).Select(f => f.Id).ToListAsync();
                foreach (var c in children) await CollectFolderIds(c);
            }
            await CollectFolderIds(folder.Id);

            // Collect documents inside subtree
            var docIds = await _context.Documents.Where(d => d.FolderId != null && subtreeFolderIds.Contains(d.FolderId.Value)).Select(d => d.Id).ToListAsync();

            // Find permissions referencing this folder subtree or documents in it, belonging to other users
            var sharedPerms = await _context.Permissions.Where(p => (p.FolderId != null && subtreeFolderIds.Contains(p.FolderId.Value) || p.DocumentId != null && docIds.Contains(p.DocumentId.Value)) && p.UserId != userId).ToListAsync();

            // If shared with other users and no confirmation flag, show confirmation page
            if (sharedPerms.Any() && Request.Form["confirmAction"].FirstOrDefault() == null)
            {
                ViewBag.FolderId = id;
                ViewBag.FolderName = folder.Name;
                ViewBag.SharedWith = await _context.Users.Where(u => sharedPerms.Select(p => p.UserId).Contains(u.Id)).Select(u => u.Email ?? u.UserName).ToListAsync();
                // Return the standard Delete view (not a separate Confirm view) so UI is unified in Views/Folders/Delete.cshtml
                return View(folder);
            }

            var action = Request.Form["confirmAction"].FirstOrDefault();

            if (action == "permanent")
            {
                // Permanent delete: delete files and DB entries for all docs in subtree, remove permissions
                // Collect documents with versions
                var docs = await _context.Documents.Where(d => d.FolderId != null && subtreeFolderIds.Contains(d.FolderId.Value)).ToListAsync();
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

                    // remove permissions for this document
                    var docPerms = await _context.Permissions.Where(p => p.DocumentId == d.Id).ToListAsync();
                    _context.Permissions.RemoveRange(docPerms);

                    _context.Documents.Remove(d);

                    // Audit: permanent delete per document (Vietnamese friendly)
                    var deleter2 = User?.Identity?.Name ?? userId;
                    await AuditHelper.LogAsync(HttpContext, "DELETE_DOCUMENT_PERMANENT", "Document", d.Id, d.FileName, $"Người dùng '{deleter2}' đã xóa vĩnh viễn tệp '{d.FileName}'.");
                }

                // remove permissions for folders in subtree
                var folderPerms = await _context.Permissions.Where(p => p.FolderId != null && subtreeFolderIds.Contains(p.FolderId.Value)).ToListAsync();
                _context.Permissions.RemoveRange(folderPerms);

                // remove folders recursively
                foreach (var fid in subtreeFolderIds.OrderByDescending(i => i))
                {
                    var f = await _context.Folders.FindAsync(fid);
                    if (f != null) _context.Folders.Remove(f);

                    // Audit: permanent delete folder (Vietnamese friendly)
                    if (f != null)
                    {
                        var deleter3 = User?.Identity?.Name ?? userId;
                        await AuditHelper.LogAsync(HttpContext, "DELETE_FOLDER_PERMANENT", "Folder", f.Id, f.Name, $"Người dùng '{deleter3}' đã xóa vĩnh viễn thư mục '{f.Name}'.");
                    }
                }

                await _context.SaveChangesAsync();
                TempData["Message"] = "Đã xóa vĩnh viễn thư mục và nội dung. Những người được chia sẻ sẽ không còn truy cập.";
                return RedirectAfterFolderAction(folder.ParentId);
            }

            // default or 'trash' action -> Soft delete
            // Thực hiện đánh dấu xóa mềm toàn bộ cây thư mục và tệp tin bên trong
            await SoftDeleteFolderRecursive(folder);

            // Remove any permissions referencing this subtree (folders or documents) so shared items in trash are no longer accessible
            try
            {
                // remove folder-level perms
                var permsForFolders = await _context.Permissions.Where(p => p.FolderId != null && subtreeFolderIds.Contains(p.FolderId.Value)).ToListAsync();
                if (permsForFolders.Any()) _context.Permissions.RemoveRange(permsForFolders);

                // remove document-level perms for documents under subtree
                if (docIds != null && docIds.Count > 0)
                {
                    var permsForDocs = await _context.Permissions.Where(p => p.DocumentId != null && docIds.Contains(p.DocumentId.Value)).ToListAsync();
                    if (permsForDocs.Any()) _context.Permissions.RemoveRange(permsForDocs);
                }
            }
            catch
            {
                // ignore permission removal errors during soft-delete; it's non-fatal
            }

            await _context.SaveChangesAsync();

            // Audit: soft delete (Vietnamese friendly)
            var deleterFinal = User?.Identity?.Name ?? userId;
            await AuditHelper.LogAsync(HttpContext, "DELETE_FOLDER_SOFT", "Folder", folder.Id, folder.Name, $"Người dùng '{deleterFinal}' đã chuyển thư mục '{folder.Name}' vào thùng rác. (Thư mục con: {subtreeFolderIds.Count}, tệp: {docIds.Count})");

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
        // Modified to create wwwroot/uploads/{username} and store files there
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

            // determine username and safe folder name
            var username = User?.Identity?.Name ?? userId ?? "unknown";
            var userFolderName = GetSafeFolderName(username);

            // Ensure uploads/user folder exists
            var uploadsRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
            if (!Directory.Exists(uploadsRoot)) Directory.CreateDirectory(uploadsRoot);

            var uploadsFolder = Path.Combine(uploadsRoot, userFolderName);
            if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

            int uploadedCount = 0;
            string uploadedRootName = null;

            if (files.Count > 0)
            {
                // derive top-level folder name from first file path (webkitdirectory provides relative paths)
                var firstRel = files[0].FileName.Replace('\\', '/').TrimStart('/');
                if (!string.IsNullOrEmpty(firstRel))
                {
                    var seg = firstRel.Split('/');
                    if (seg.Length > 0) uploadedRootName = seg[0];
                }
            }

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
                    FilePath = "/uploads/" + userFolderName + "/" + storedFileName,
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

                uploadedCount++;

                // Audit: log document upload (Vietnamese friendly)
                var uploader = User?.Identity?.Name ?? userId;
                await AuditHelper.LogAsync(HttpContext, "UPLOAD_DOCUMENT", "Document", document.Id, document.FileName, $"Người dùng '{uploader}' đã tải tệp '{document.FileName}' ({document.FileSize} bytes) lên.");
            }

            TempData["Message"] = "Tải thư mục thành công.";

            // Audit: folder upload summary (Vietnamese friendly)
            var uploader2 = User?.Identity?.Name ?? userId;
            var auditTargetName = !string.IsNullOrEmpty(uploadedRootName) ? uploadedRootName : uploadsFolder;
            await AuditHelper.LogAsync(HttpContext, "UPLOAD_FOLDER", "FolderUpload", parentId, auditTargetName, $"Người dùng '{uploader2}' đã tải thư mục '{auditTargetName}' lên chứa {uploadedCount} tệp.");

            return RedirectAfterFolderAction(parentId);
        }

        // ======================================================
        // DOWNLOAD FOLDER - create .zip of folder contents
        // ======================================================
        public async Task<IActionResult> Download(int id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // Only allow download if user owns the folder
            var folder = await _context.Folders
                .Include(f => f.SubFolders)
                .Include(f => f.Documents)
                .FirstOrDefaultAsync(f => f.Id == id && f.OwnerId == userId && !f.IsDeleted);

            if (folder == null)
            {
                TempData["Error"] = "Thư mục không tồn tại hoặc không có quyền tải xuống.";
                return RedirectToAction("Index", "Home");
            }

            // Collect all documents under this folder recursively
            var docPaths = new List<string>();

            async Task CollectDocs(Folder f)
            {
                await _context.Entry(f).Collection(x => x.Documents).LoadAsync();
                foreach (var d in f.Documents)
                {
                    if (!string.IsNullOrEmpty(d.FilePath) && !d.IsDeleted)
                    {
                        // physical path
                        var phys = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", d.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                        if (System.IO.File.Exists(phys)) docPaths.Add(phys);
                    }
                }

                await _context.Entry(f).Collection(x => x.SubFolders).LoadAsync();
                foreach (var s in f.SubFolders)
                {
                    await CollectDocs(s);
                }
            }

            await CollectDocs(folder);

            if (docPaths.Count == 0)
            {
                TempData["Error"] = "Thư mục không có tệp để tải xuống.";
                return RedirectToAction("Index", "Home");
            }

            // Create zip in memory
            var zipName = $"{folder.Name}-{DateTime.UtcNow:yyyyMMddHHmmss}.zip";
            using (var ms = new MemoryStream())
            {
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    foreach (var path in docPaths)
                    {
                        try
                        {
                            var entryName = Path.GetFileName(path);
                            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                            using var entryStream = entry.Open();
                            using var fileStream = System.IO.File.OpenRead(path);
                            await fileStream.CopyToAsync(entryStream);
                        }
                        catch { }
                    }
                }

                ms.Seek(0, SeekOrigin.Begin);
                return File(ms.ToArray(), "application/zip", zipName);
            }
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