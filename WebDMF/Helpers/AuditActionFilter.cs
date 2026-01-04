using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using WebDocumentManagement_FileSharing.Data;
using WebDocumentManagement_FileSharing.Models;

namespace WebDocumentManagement_FileSharing.Helpers
{
    // Global action filter to create audit log entries for controller actions
    public class AuditActionFilter : IAsyncActionFilter
    {
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var executedContext = await next();

            try
            {
                var http = context.HttpContext;

                // If controller explicitly logged via AuditHelper, skip global logging
                try
                {
                    if (http.Items.ContainsKey("AuditLogged") && http.Items["AuditLogged"] is bool b && b) return;
                }
                catch { }

                var db = http.RequestServices.GetService<ApplicationDbContext>();
                if (db == null) return;

                var user = http.User;
                var actorId = user?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
                var actor = user?.Identity?.Name ?? actorId ?? "Anonymous";

                var controller = context.RouteData.Values["controller"]?.ToString() ?? string.Empty;
                var action = context.RouteData.Values["action"]?.ToString() ?? string.Empty;

                // Compose short args summary
                var shortArgs = string.Join(", ", context.ActionArguments.Select(kv => kv.Value == null ? kv.Key + "=null" : kv.Key + "=" + kv.Value.ToString()));
                if (string.IsNullOrWhiteSpace(shortArgs)) shortArgs = executedContext.Exception != null ? ("Exception: " + executedContext.Exception.Message) : string.Empty;

                // detect common id args
                int? targetId = null;
                string targetName = null;
                var idKeys = new[] { "id", "folderId", "documentId", "userId", "permissionId" };
                foreach (var k in idKeys)
                {
                    if (context.ActionArguments.ContainsKey(k) && context.ActionArguments[k] != null)
                    {
                        var sval = context.ActionArguments[k].ToString();
                        targetName = sval;
                        if (int.TryParse(sval, out var i)) targetId = i;
                        break;
                    }
                }

                // map to action label
                var label = MapActionLabel(controller, action, context.ActionArguments);

                // try resolve nicer target names from db
                try
                {
                    if (targetId.HasValue)
                    {
                        if (label.Contains("FOLDER") || controller.Equals("folders", StringComparison.OrdinalIgnoreCase) || context.ActionArguments.ContainsKey("folderId"))
                        {
                            var f = await db.Folders.FindAsync(targetId.Value);
                            if (f != null) targetName = f.Name;
                        }
                        else if (label.Contains("DOCUMENT") || controller.Equals("documents", StringComparison.OrdinalIgnoreCase) || context.ActionArguments.ContainsKey("documentId"))
                        {
                            var d = await db.Documents.FindAsync(targetId.Value);
                            if (d != null) targetName = d.FileName;
                        }
                    }

                    if (context.ActionArguments.ContainsKey("userId") && context.ActionArguments["userId"] != null)
                    {
                        var uid = context.ActionArguments["userId"].ToString();
                        if (!string.IsNullOrEmpty(uid))
                        {
                            var u = await db.Users.FindAsync(uid);
                            if (u != null) targetName = u.Email ?? u.UserName ?? uid;
                        }
                    }
                }
                catch { }

                // Build Vietnamese details
                // Normalize target display name: preserve uploaded folder name; if name indicates UI DMF show standardized label
                var displayTargetName = targetName;
                if (!string.IsNullOrEmpty(displayTargetName))
                {
                    var tn = displayTargetName.Trim();
                    if (tn.IndexOf("ui dmf", StringComparison.OrdinalIgnoreCase) >= 0 || tn.IndexOf("uidmf", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        displayTargetName = "folder UI DMF";
                    }
                }

                string details;
                switch (label)
                {
                    case "UPLOAD_FOLDER":
                        details = !string.IsNullOrEmpty(displayTargetName)
                            ? $"Người dùng '{actor}' đã tải lên thư mục '{displayTargetName}'."
                            : $"Người dùng '{actor}' đã tải lên một thư mục.";
                        break;
                    case "UPLOAD_DOCUMENT":
                        details = !string.IsNullOrEmpty(displayTargetName)
                            ? $"Người dùng '{actor}' đã tải lên tệp '{displayTargetName}'."
                            : $"Người dùng '{actor}' đã tải lên một tệp.";
                        break;
                    case "DELETE_FOLDER_PERMANENT":
                        details = !string.IsNullOrEmpty(targetName)
                            ? $"Người dùng '{actor}' đã xóa vĩnh viễn thư mục '{targetName}'."
                            : $"Người dùng '{actor}' đã xóa vĩnh viễn một thư mục.";
                        break;
                    case "DELETE_FOLDER_SOFT":
                        // include counts when available in args
                        if (context.ActionArguments.ContainsKey("subtreeCount") || context.ActionArguments.ContainsKey("docCount"))
                        {
                            var sc = context.ActionArguments.ContainsKey("subtreeCount") ? context.ActionArguments["subtreeCount"]?.ToString() : null;
                            var dc = context.ActionArguments.ContainsKey("docCount") ? context.ActionArguments["docCount"]?.ToString() : null;
                            details = !string.IsNullOrEmpty(targetName)
                                ? $"Người dùng '{actor}' đã chuyển thư mục '{targetName}' vào thùng rác. (Thư mục con: {sc ?? "-"}, tệp: {dc ?? "-"})"
                                : $"Người dùng '{actor}' đã chuyển một thư mục vào thùng rác. (Thư mục con: {sc ?? "-"}, tệp: {dc ?? "-"})";
                        }
                        else
                        {
                            details = !string.IsNullOrEmpty(targetName)
                                ? $"Người dùng '{actor}' đã chuyển thư mục '{targetName}' vào thùng rác."
                                : $"Người dùng '{actor}' đã chuyển một thư mục vào thùng rác.";
                        }
                        break;
                    case "DELETE_DOCUMENT_PERMANENT":
                        details = !string.IsNullOrEmpty(targetName)
                            ? $"Người dùng '{actor}' đã xóa vĩnh viễn tệp '{targetName}'."
                            : $"Người dùng '{actor}' đã xóa vĩnh viễn một tệp.";
                        break;
                    case "DELETE_DOCUMENT_SOFT":
                        details = !string.IsNullOrEmpty(targetName)
                            ? $"Người dùng '{actor}' đã chuyển tệp '{targetName}' vào thùng rác."
                            : $"Người dùng '{actor}' đã chuyển một tệp vào thùng rác.";
                        break;
                    case "SHARE_DOCUMENT":
                    case "SHARE_FOLDER":
                        var rcpt = context.ActionArguments.ContainsKey("receiverEmail") ? context.ActionArguments["receiverEmail"]?.ToString() : null;
                        if (!string.IsNullOrEmpty(targetName) && !string.IsNullOrEmpty(rcpt))
                            details = $"Người dùng '{actor}' đã chia sẻ '{targetName}' với '{rcpt}'.";
                        else if (!string.IsNullOrEmpty(targetName))
                            details = $"Người dùng '{actor}' đã chia sẻ '{targetName}'.";
                        else
                            details = $"Người dùng '{actor}' đã thực hiện chia sẻ.";
                        break;
                    case "STAR_DOCUMENT":
                        details = !string.IsNullOrEmpty(targetName)
                            ? $"Người dùng '{actor}' đã gắn sao cho tệp '{targetName}'."
                            : $"Người dùng '{actor}' đã gắn sao cho một tệp.";
                        break;
                    case "UNSTAR_DOCUMENT":
                        details = !string.IsNullOrEmpty(targetName)
                            ? $"Người dùng '{actor}' đã bỏ gắn sao cho tệp '{targetName}'."
                            : $"Người dùng '{actor}' đã bỏ gắn sao cho một tệp.";
                        break;
                    case "STAR_FOLDER":
                        details = !string.IsNullOrEmpty(targetName)
                            ? $"Người dùng '{actor}' đã gắn sao cho thư mục '{targetName}'."
                            : $"Người dùng '{actor}' đã gắn sao cho một thư mục.";
                        break;
                    case "UNSTAR_FOLDER":
                        details = !string.IsNullOrEmpty(targetName)
                            ? $"Người dùng '{actor}' đã bỏ gắn sao cho thư mục '{targetName}'."
                            : $"Người dùng '{actor}' đã bỏ gắn sao cho một thư mục.";
                        break;
                    case "RENAME_DOCUMENT":
                    case "RENAME_FOLDER":
                        var newName = context.ActionArguments.ContainsKey("newName") ? context.ActionArguments["newName"]?.ToString() : null;
                        if (!string.IsNullOrEmpty(targetName) && !string.IsNullOrEmpty(newName))
                            details = $"Người dùng '{actor}' đã đổi tên '{targetName}' thành '{newName}'.";
                        else if (!string.IsNullOrEmpty(targetName))
                            details = $"Người dùng '{actor}' đã đổi tên '{targetName}'.";
                        else
                            details = $"Người dùng '{actor}' đã đổi tên một mục.";
                        break;
                    case "UPDATE_TIER":
                        var userArg = context.ActionArguments.ContainsKey("userId") ? context.ActionArguments["userId"]?.ToString() : null;
                        var tier = context.ActionArguments.ContainsKey("tier") ? context.ActionArguments["tier"]?.ToString() : null;
                        if (!string.IsNullOrEmpty(userArg))
                        {
                            var u = await db.Users.FindAsync(userArg);
                            var display = u != null ? (u.Email ?? u.UserName) : userArg;
                            details = $"Quản trị viên '{actor}' đã thay đổi gói của {display} thành '{tier ?? "<không rõ>"}'.";
                        }
                        else
                        {
                            details = $"Quản trị viên '{actor}' đã thay đổi gói thành '{tier ?? "<không rõ>"}'.";
                        }
                        break;
                    case "LOCK_USER":
                        details = !string.IsNullOrEmpty(targetName)
                            ? $"Quản trị viên '{actor}' đã khóa tài khoản {targetName}."
                            : $"Quản trị viên '{actor}' đã khóa một tài khoản.";
                        break;
                    case "UNLOCK_USER":
                        details = !string.IsNullOrEmpty(targetName)
                            ? $"Quản trị viên '{actor}' đã mở khóa tài khoản {targetName}."
                            : $"Quản trị viên '{actor}' đã mở khóa một tài khoản.";
                        break;
                    default:
                        details = !string.IsNullOrEmpty(shortArgs)
                            ? $"Người dùng '{actor}' thực hiện {controller}.{action}: {shortArgs}"
                            : $"Người dùng '{actor}' thực hiện {controller}.{action}.";
                        break;
                }

                var log = new AuditLog
                {
                    Timestamp = DateTime.UtcNow,
                    ActorId = actorId,
                    Actor = actor,
                    Action = label,
                    TargetType = controller,
                    TargetId = targetId,
                    TargetName = displayTargetName,
                    Details = details
                };

                db.AuditLogs.Add(log);
                await db.SaveChangesAsync();
            }
            catch
            {
                // swallow errors to avoid affecting request
            }
        }

        private string MapActionLabel(string controller, string action, System.Collections.Generic.IDictionary<string, object> args)
        {
            controller ??= string.Empty;
            action ??= string.Empty;
            var key = (controller + "." + action).ToLowerInvariant();

            bool A(string s) => action.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;
            bool K(string s) => key.Contains(s);

            if (A("upload") || K("uploadfolder") || K("upload"))
                return controller.Equals("folders", StringComparison.OrdinalIgnoreCase) ? "UPLOAD_FOLDER" : "UPLOAD_DOCUMENT";

            if (A("delete") || A("trash") || K("delete"))
            {
                if (controller.Equals("folders", StringComparison.OrdinalIgnoreCase))
                {
                    if (args != null && args.ContainsKey("confirmAction") && args["confirmAction"]?.ToString() == "permanent") return "DELETE_FOLDER_PERMANENT";
                    return "DELETE_FOLDER_SOFT";
                }
                if (controller.Equals("documents", StringComparison.OrdinalIgnoreCase))
                {
                    if (args != null && args.ContainsKey("confirmAction") && args["confirmAction"]?.ToString() == "permanent") return "DELETE_DOCUMENT_PERMANENT";
                    return "DELETE_DOCUMENT_SOFT";
                }
                return "DELETE";
            } 

            if (A("share") || K("processshare") || K("share")) return "SHARE_DOCUMENT";
            if (A("star") || A("toggle")) return "STAR_DOCUMENT";
            if (A("rename")) return controller.Equals("folders", StringComparison.OrdinalIgnoreCase) ? "RENAME_FOLDER" : "RENAME_DOCUMENT";
            if (A("setusertier") || A("settier")) return "UPDATE_TIER";
            if (A("lockuser")) return "LOCK_USER";
            if (A("unlockuser")) return "UNLOCK_USER";

            return (controller + "." + action).ToUpperInvariant();
        }
    }
}
