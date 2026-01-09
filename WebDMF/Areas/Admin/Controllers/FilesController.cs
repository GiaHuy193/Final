using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebDocumentManagement_FileSharing.Data;
using WebDocumentManagement_FileSharing.Models;

namespace WebDocumentManagement_FileSharing.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class FilesController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        public FilesController(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        // GET: Admin/Files
        // Supports 'search' (file name or owner email) and 'ownerEmail' (filter by uploader email)
        public async Task<IActionResult> Index(string search, string ownerEmail)
        {
            var query = _context.Documents.AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                // find user ids that match email search
                var matchingUserIds = await _userManager.Users
                    .Where(u => u.Email != null && u.Email.Contains(search))
                    .Select(u => u.Id)
                    .ToListAsync();

                query = query.Where(f => f.FileName.Contains(search) || matchingUserIds.Contains(f.OwnerId));
            }

            if (!string.IsNullOrWhiteSpace(ownerEmail))
            {
                var ownerMatchIds = await _userManager.Users
                    .Where(u => u.Email != null && u.Email.Contains(ownerEmail))
                    .Select(u => u.Id)
                    .ToListAsync();

                if (ownerMatchIds.Any())
                    query = query.Where(d => ownerMatchIds.Contains(d.OwnerId));
                else
                    query = query.Where(d => false);
            }

            var files = await query.OrderByDescending(f => f.UploadedDate).ToListAsync();

            // Build OwnerId -> Email map for display
            var ownerIds = files.Select(f => f.OwnerId).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
            var ownerEmails = new Dictionary<string, string>();
            if (ownerIds.Any())
            {
                var users = await _userManager.Users
                    .Where(u => ownerIds.Contains(u.Id))
                    .Select(u => new { u.Id, u.Email })
                    .ToListAsync();

                foreach (var u in users)
                {
                    ownerEmails[u.Id] = u.Email ?? u.Id;
                }
            }

            ViewBag.CurrentSearch = search;
            ViewBag.CurrentOwnerEmail = ownerEmail;
            ViewBag.UserEmails = ownerEmails;
            return View(files);
        }

        // POST: Admin/Files/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var document = await _context.Documents.FindAsync(id);
            if (document == null) return NotFound();

            try
            {
                var relative = (document.FilePath ?? document.FileName) ?? string.Empty;
                relative = relative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", relative);

                if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath);

                var alt = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", document.FileName);
                if (System.IO.File.Exists(alt) && !string.Equals(alt, filePath, StringComparison.OrdinalIgnoreCase))
                    System.IO.File.Delete(alt);

                _context.Documents.Remove(document);
                await _context.SaveChangesAsync();

                TempData["Success"] = $"Đã xóa tệp tin '{document.FileName}' vĩnh viễn.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Có lỗi xảy ra khi xóa file: " + ex.Message;
            }

            return RedirectToAction(nameof(Index));
        }
    }
}
