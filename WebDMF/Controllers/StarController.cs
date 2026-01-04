using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebDocumentManagement_FileSharing.Data;
using WebDocumentManagement_FileSharing.Helpers;

namespace WebDocumentManagement_FileSharing.Controllers
{
    [Authorize]
    public class StarController : Controller
    {
        private readonly ApplicationDbContext _context;
        public StarController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpPost]
        public async Task<IActionResult> ToggleDocument(int id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var doc = await _context.Documents.FirstOrDefaultAsync(d => d.Id == id && d.OwnerId == userId);
            if (doc == null) return NotFound();
            doc.IsStarred = !doc.IsStarred;
            await _context.SaveChangesAsync();

            // audit
            await AuditHelper.LogAsync(HttpContext, doc.IsStarred ? "STAR_DOCUMENT" : "UNSTAR_DOCUMENT", "Document", doc.Id, doc.FileName, $"User toggled star to {doc.IsStarred}");

            return Json(new { success = true, isStarred = doc.IsStarred });
        }

        [HttpPost]
        public async Task<IActionResult> ToggleFolder(int id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var folder = await _context.Folders.FirstOrDefaultAsync(f => f.Id == id && f.OwnerId == userId);
            if (folder == null) return NotFound();
            folder.IsStarred = !folder.IsStarred;
            await _context.SaveChangesAsync();

            await AuditHelper.LogAsync(HttpContext, folder.IsStarred ? "STAR_FOLDER" : "UNSTAR_FOLDER", "Folder", folder.Id, folder.Name, $"User toggled star to {folder.IsStarred}");

            return Json(new { success = true, isStarred = folder.IsStarred });
        }

        public async Task<IActionResult> Index()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var docs = await _context.Documents.Where(d => d.OwnerId == userId && d.IsStarred && !d.IsDeleted).ToListAsync();
            var folders = await _context.Folders.Where(f => f.OwnerId == userId && f.IsStarred && !f.IsDeleted).ToListAsync();
            var vm = new WebDocumentManagement_FileSharing.Models.FileSystemViewModel { Documents = docs, Folders = folders };
            return View(vm);
        }
    }
}
