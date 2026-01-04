using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebDocumentManagement_FileSharing.Data;

namespace WebDocumentManagement_FileSharing.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class AuditLogsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AuditLogsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: Admin/AuditLogs
        public async Task<IActionResult> Index()
        {
            // Lấy 500 bản ghi mới nhất để tránh làm nặng trang nếu log quá nhiều
            var logs = await _context.AuditLogs
                .OrderByDescending(l => l.Timestamp)
                .Take(500)
                .ToListAsync();

            return View(logs);
        }
    }
}