using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WebDocumentManagement_FileSharing.Data;
using WebDocumentManagement_FileSharing.Models;

namespace WebDocumentManagement_FileSharing.Helpers
{
    public static class AuditHelper
    {
        // Use this helper from controllers: await AuditHelper.LogAsync(HttpContext, action, targetType, targetId, targetName, details);
        public static async Task LogAsync(HttpContext httpContext, string action, string targetType, int? targetId, string targetName, string details)
        {
            if (httpContext == null) return;
            try
            {
                var services = httpContext.RequestServices;
                var db = services.GetService<ApplicationDbContext>();
                if (db == null) return;

                var user = httpContext.User;
                var actorId = user?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
                var actor = user?.Identity?.Name ?? actorId ?? "Unknown";

                var log = new AuditLog
                {
                    Timestamp = DateTime.UtcNow,
                    ActorId = actorId,
                    Actor = actor,
                    Action = action,
                    TargetType = targetType,
                    TargetId = targetId,
                    TargetName = targetName,
                    Details = details
                };

                db.AuditLogs.Add(log);
                await db.SaveChangesAsync();
            }
            catch
            {
                // swallow logging errors to avoid affecting main flow
            }
        }
    }
}
