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

            // determine actor info once for all updates
            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            var currentUserName = User.Identity?.Name ?? "Admin";

            try
            {
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

                // Additionally handle maintenance fields that may not be part of the posted list
                try
                {
                    var formMode = Request.Form["MaintenanceMode"].FirstOrDefault();
                    var formMsg = Request.Form["MaintenanceMessage"].FirstOrDefault();
                    if (formMode != null)
                    {
                        var setting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.SettingKey == "MaintenanceMode");
                        if (setting == null)
                        {
                            setting = new SystemSetting { SettingKey = "MaintenanceMode", SettingValue = formMode, Description = "Flag bật/tắt chế độ bảo trì" };
                            _context.SystemSettings.Add(setting);
                        }
                        else if (setting.SettingValue != formMode)
                        {
                            var old = setting.SettingValue;
                            setting.SettingValue = formMode;
                            _context.AuditLogs.Add(new AuditLog { Timestamp = DateTime.UtcNow, ActorId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier) ?? "", Actor = User.Identity?.Name ?? "Admin", Action = "CONFIG_UPDATE", TargetType = "SystemSetting", TargetId = setting.Id, TargetName = setting.SettingKey, Details = $"Thay đổi {setting.SettingKey} từ '{old}' thành '{formMode}'" });
                        }
                    }

                    if (formMsg != null)
                    {
                        var settingMsg = await _context.SystemSettings.FirstOrDefaultAsync(s => s.SettingKey == "MaintenanceMessage");
                        if (settingMsg == null)
                        {
                            settingMsg = new SystemSetting { SettingKey = "MaintenanceMessage", SettingValue = formMsg, Description = "Nội dung thông báo khi bảo trì" };
                            _context.SystemSettings.Add(settingMsg);
                        }
                        else if (settingMsg.SettingValue != formMsg)
                        {
                            var old = settingMsg.SettingValue;
                            settingMsg.SettingValue = formMsg;
                            _context.AuditLogs.Add(new AuditLog { Timestamp = DateTime.UtcNow, ActorId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier) ?? "", Actor = User.Identity?.Name ?? "Admin", Action = "CONFIG_UPDATE", TargetType = "SystemSetting", TargetId = settingMsg.Id, TargetName = settingMsg.SettingKey, Details = $"Thay đổi {settingMsg.SettingKey} từ '{old}' thành '{formMsg}'" });
                        }
                    }

                    await _context.SaveChangesAsync();
                }
                catch { /* ignore maintenance upsert errors */ }

                // Additionally handle support email field (posted as simple form field) inside same try so actor info is available
                var formSupport = Request.Form["supportEmail"].FirstOrDefault();
                if (formSupport != null)
                {
                    var setting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.SettingKey == "SupportEmail");
                    if (setting == null)
                    {
                        setting = new SystemSetting { SettingKey = "SupportEmail", SettingValue = formSupport, Description = "Support contact email" };
                        _context.SystemSettings.Add(setting);
                        _context.AuditLogs.Add(new AuditLog { Timestamp = DateTime.UtcNow, ActorId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier) ?? "", Actor = User.Identity?.Name ?? "Admin", Action = "CONFIG_UPDATE", TargetType = "SystemSetting", TargetId = setting.Id, TargetName = setting.SettingKey, Details = $"Added SupportEmail = '{formSupport}'" });
                    }
                    else if (setting.SettingValue != formSupport)
                    {
                        var old = setting.SettingValue;
                        setting.SettingValue = formSupport;
                        _context.AuditLogs.Add(new AuditLog { Timestamp = DateTime.UtcNow, ActorId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier) ?? "", Actor = User.Identity?.Name ?? "Admin", Action = "CONFIG_UPDATE", TargetType = "SystemSetting", TargetId = setting.Id, TargetName = setting.SettingKey, Details = $"Thay đổi {setting.SettingKey} từ '{old}' thành '{formSupport}'" });
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