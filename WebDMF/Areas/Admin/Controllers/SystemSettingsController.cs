using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebDocumentManagement_FileSharing.Data;
using WebDocumentManagement_FileSharing.Models;
using Microsoft.EntityFrameworkCore;

namespace WebDocumentManagement_FileSharing.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class SystemSettingsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public SystemSettingsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: Admin/SystemSettings
        public async Task<IActionResult> Index()
        {
            // Lấy tất cả cài đặt ra để Admin chỉnh sửa
            var settings = await _context.SystemSettings.ToListAsync();
            return View(settings);
        }

        // POST: Admin/SystemSettings/SaveSettings
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveSettings(List<SystemSetting> settings)
        {
            if (settings == null || !settings.Any())
            {
                return RedirectToAction(nameof(Index));
            }

            try
            {
                var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
                var currentUserName = User.Identity?.Name ?? "Admin";

                foreach (var item in settings)
                {
                    var dbSetting = await _context.SystemSettings.FindAsync(item.Id);
                    if (dbSetting != null && dbSetting.SettingValue != item.SettingValue)
                    {
                        // Lưu lại giá trị cũ để ghi nhật ký
                        var oldValue = dbSetting.SettingValue;

                        // Cập nhật giá trị mới
                        dbSetting.SettingValue = item.SettingValue;

                        // Ghi nhật ký hệ thống (Audit Log) into AuditLogs table
                        var audit = new AuditLog
                        {
                            Timestamp = DateTime.UtcNow,
                            ActorId = currentUserId,
                            Actor = currentUserName,
                            Action = "CONFIG_UPDATE",
                            TargetType = "SystemSetting",
                            TargetId = dbSetting.Id,
                            TargetName = dbSetting.SettingKey,
                            Details = $"Thay đổi {dbSetting.SettingKey} từ '{oldValue}' thành '{item.SettingValue}'"
                        };

                        _context.AuditLogs.Add(audit);
                    }
                }

                await _context.SaveChangesAsync();
                TempData["Success"] = "Cấu hình hệ thống đã được cập nhật và lưu vào nhật ký!";
            }
            catch (Exception ex)
            {
                // Nếu có lỗi (ví dụ Database mất kết nối)
                TempData["Error"] = "Có lỗi xảy ra: " + ex.Message;
            }

            return RedirectToAction(nameof(Index));
        }
    }
}