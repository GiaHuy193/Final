using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using WebDocumentManagement_FileSharing.Data;
using WebDocumentManagement_FileSharing.Helpers; // Đảm bảo bạn có class này hoặc xóa dòng log Audit nếu không dùng
using WebDocumentManagement_FileSharing.Models;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.WebUtilities;

namespace WebDocumentManagement_FileSharing.Controllers
{
    [Authorize]
    public class GroupsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        public GroupsController(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        // ==========================================
        // 1. DANH SÁCH NHÓM (Fix lỗi hiển thị số lượng)
        // ==========================================
        public async Task<IActionResult> Index()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // 1. Lấy nhóm tôi sở hữu (Kèm Members để đếm)
            var owned = await _context.Groups
                .Include(g => g.Members)
                .Where(g => g.OwnerId == userId)
                .ToListAsync();

            // 2. Lấy nhóm tôi tham gia (Kèm Members)
            var memberGroupIds = await _context.GroupMembers
                .Where(m => m.UserId == userId)
                .Select(m => m.GroupId)
                .ToListAsync();

            var member = await _context.Groups
                .Include(g => g.Members)
                .Where(g => memberGroupIds.Contains(g.Id))
                .ToListAsync();

            ViewBag.OwnedGroups = owned;
            ViewBag.MemberGroups = member;

            // 3. Tính toán thống kê (Summary) cho View hiển thị
            var allGroups = owned.Concat(member).Distinct().ToList();
            var visibleGroupIds = allGroups.Select(g => g.Id).ToList();

            // Lấy tất cả lượt chia sẻ của các nhóm này
            var allShares = await _context.Set<GroupShare>()
                .Where(gs => visibleGroupIds.Contains(gs.GroupId))
                .Select(gs => new { gs.GroupId, gs.DocumentId, gs.FolderId })
                .ToListAsync();

            var summary = new Dictionary<int, object>();

            foreach (var group in allGroups)
            {
                var groupShares = allShares.Where(s => s.GroupId == group.Id).ToList();

                var docCount = groupShares.Count(s => s.DocumentId.HasValue);
                var folderCount = groupShares.Count(s => s.FolderId.HasValue);
                var memberCount = group.Members?.Count ?? 0;

                // Tạo object chứa dữ liệu để View hiển thị (dynamic)
                summary[group.Id] = new
                {
                    DocCount = docCount,
                    FolderCount = folderCount,
                    MemberCount = memberCount,
                    DocSample = new List<string>() // Để trống cho nhẹ, nếu cần hiển thị tên file mẫu thì query thêm
                };
            }

            ViewBag.GroupSummary = summary;

            // --- NEW: fetch member user info for all groups for owner transfer UI
            var memberUserIds = allGroups.SelectMany(g => g.Members.Select(m => m.UserId)).Distinct().ToList();
            var memberInfos = await _userManager.Users
                .Where(u => memberUserIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Email, u.UserName })
                .ToListAsync();

            // Create lookup: userId -> display name
            var userLookup = memberInfos.ToDictionary(u => u.Id, u => (u.Email ?? u.UserName ?? u.Id));
            ViewBag.GroupMemberLookup = userLookup;

            return View();
        }

        // ==========================================
        // 2. TẠO NHÓM
        // ==========================================
        public IActionResult Create()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Group model)
        {
            if (!ModelState.IsValid) return View(model);

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            model.OwnerId = userId;
            model.CreatedDate = DateTime.UtcNow; // Đảm bảo có ngày tạo

            _context.Groups.Add(model);
            await _context.SaveChangesAsync();

            // Tự động thêm chủ nhóm vào làm thành viên
            _context.GroupMembers.Add(new GroupMember { GroupId = model.Id, UserId = userId, JoinedAt = DateTime.UtcNow });
            await _context.SaveChangesAsync();

            TempData["Message"] = "Tạo nhóm thành công.";
            return RedirectToAction(nameof(Index));
        }

        // ==========================================
        // 3. CHI TIẾT NHÓM (Tối ưu hiệu năng N+1)
        // ==========================================
        public async Task<IActionResult> Details(int id)
        {
            var g = await _context.Groups.Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == id);
            if (g == null) return NotFound();

            // 1. Lấy thông tin User (Email/Name) của các thành viên
            var memberIds = g.Members.Select(m => m.UserId).ToList();
            var memberInfos = await _userManager.Users
                .Where(u => memberIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Email, u.UserName })
                .ToListAsync();
            ViewBag.MemberInfos = memberInfos;

            // 1.5 Load invites (pending/declined/accepted) for this group
            var invites = await _context.Set<WebDocumentManagement_FileSharing.Models.GroupInvite>()
                .Where(i => i.GroupId == id)
                .OrderByDescending(i => i.CreatedAt)
                .ToListAsync();
            ViewBag.GroupInvites = invites;

            // 2. Check quyền chủ sở hữu
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            ViewBag.IsOwner = string.Equals(g.OwnerId, userId, StringComparison.OrdinalIgnoreCase);

            // 3. Lấy danh sách chia sẻ (Tối ưu query)
            var shares = await _context.Set<GroupShare>().Where(gs => gs.GroupId == id).ToListAsync();

            // Lấy danh sách ID để query 1 lần (tránh loop query)
            var docIds = shares.Where(s => s.DocumentId.HasValue).Select(s => s.DocumentId!.Value).Distinct().ToList();
            var folderIds = shares.Where(s => s.FolderId.HasValue).Select(s => s.FolderId!.Value).Distinct().ToList();

            var docs = await _context.Documents.Where(d => docIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id);
            var folders = await _context.Folders.Where(f => folderIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id);

            var shareList = new List<object>();
            foreach (var s in shares)
            {
                WebDocumentManagement_FileSharing.Models.Document? doc = null;
                WebDocumentManagement_FileSharing.Models.Folder? fol = null;

                if (s.DocumentId.HasValue && docs.ContainsKey(s.DocumentId.Value)) doc = docs[s.DocumentId.Value];
                if (s.FolderId.HasValue && folders.ContainsKey(s.FolderId.Value)) fol = folders[s.FolderId.Value];

                shareList.Add(new { Share = s, Document = doc, Folder = fol });
            }
            ViewBag.GroupShares = shareList;

            return View(g);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelInvite(int inviteId)
        {
            var invite = await _context.Set<WebDocumentManagement_FileSharing.Models.GroupInvite>().FindAsync(inviteId);
            if (invite == null) return NotFound();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var group = await _context.Groups.FindAsync(invite.GroupId);
            if (group == null) return NotFound();
            if (group.OwnerId != userId) return Forbid();

            _context.Set<WebDocumentManagement_FileSharing.Models.GroupInvite>().Remove(invite);
            await _context.SaveChangesAsync();

            TempData["Message"] = "Đã hủy lời mời.";
            return RedirectToAction("Details", new { id = invite.GroupId });
        }

        // ==========================================
        // 4. THAM GIA NHÓM (Fix logic cập nhật quyền)
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Join(int groupId)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (await _context.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId))
            {
                TempData["Error"] = "Bạn đã là thành viên nhóm này.";
                return RedirectToAction(nameof(Details), new { id = groupId });
            }

            // 1. Thêm thành viên
            _context.GroupMembers.Add(new GroupMember { GroupId = groupId, UserId = userId, JoinedAt = DateTime.UtcNow });

            // 2. CẤP QUYỀN (Back-fill): Duyệt qua các file đã share cho nhóm và cấp quyền cho user mới
            var shares = await _context.Set<GroupShare>().Where(gs => gs.GroupId == groupId).ToListAsync();
            foreach (var gs in shares)
            {
                if (gs.DocumentId.HasValue)
                {
                    // Check xem user đã có quyền chưa để tránh trùng
                    var exists = await _context.Permissions.AnyAsync(p => p.DocumentId == gs.DocumentId.Value && p.UserId == userId);
                    if (!exists)
                    {
                        _context.Permissions.Add(new Permission
                        {
                            DocumentId = gs.DocumentId.Value,
                            UserId = userId,
                            AccessType = gs.AccessType,
                            SharedDate = DateTime.UtcNow
                        });
                    }
                }
                if (gs.FolderId.HasValue)
                {
                    var exists = await _context.Permissions.AnyAsync(p => p.FolderId == gs.FolderId.Value && p.UserId == userId);
                    if (!exists)
                    {
                        _context.Permissions.Add(new Permission
                        {
                            FolderId = gs.FolderId.Value,
                            UserId = userId,
                            AccessType = gs.AccessType,
                            SharedDate = DateTime.UtcNow
                        });
                    }
                }
            }

            await _context.SaveChangesAsync();
            TempData["Message"] = "Tham gia nhóm thành công.";
            return RedirectToAction(nameof(Details), new { id = groupId });
        }

        // ==========================================
        // 5. THÊM THÀNH VIÊN QUA EMAIL
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddMemberByEmail(int groupId, string email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                TempData["Error"] = "Email không hợp lệ.";
                return RedirectToAction("Details", new { id = groupId });
            }

            var group = await _context.Groups.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == groupId);
            if (group == null) return NotFound();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (group.OwnerId != userId) return Forbid(); // Chỉ chủ nhóm mới được thêm

            var user = await _userManager.FindByEmailAsync(email);

            // If user already a member, show error
            if (user != null && group.Members.Any(m => m.UserId == user.Id))
            {
                TempData["Error"] = "Người này đã là thành viên nhóm.";
                return RedirectToAction("Details", new { id = groupId });
            }

            // create invite instead of adding member directly
            var token = GenerateToken();
            var invite = new GroupInvite
            {
                GroupId = groupId,
                InviterId = userId,
                InviteeUserId = user?.Id,
                InviteeEmail = email.Trim(),
                Token = token,
                Status = InviteStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };

            _context.Set<GroupInvite>().Add(invite);
            await _context.SaveChangesAsync();

            // send email (best effort)
            try
            {
                var config = HttpContext.RequestServices.GetService(typeof(Microsoft.Extensions.Configuration.IConfiguration)) as Microsoft.Extensions.Configuration.IConfiguration;
                if (config != null)
                {
                    var svc = new WebDocumentManagement_FileSharing.Service.EmailService(config);
                    var acceptUrl = Url.Action("RespondInvite", "Groups", new { token = token, response = "accept" }, Request.Scheme);
                    var declineUrl = Url.Action("RespondInvite", "Groups", new { token = token, response = "decline" }, Request.Scheme);

                    var html = $@"<p>Bạn được mời tham gia nhóm: <strong>{group.Name}</strong></p>
                                  <p>Người mời: {User.Identity?.Name}</p>
                                  <p><a href='{acceptUrl}'>Chấp nhận</a> | <a href='{declineUrl}'>Từ chối</a></p>";

                    await svc.SendEmailAsync(email, $"Lời mời tham gia nhóm {group.Name}", html);
                }
            }
            catch
            {
                // ignore
            }

            TempData["Message"] = "Lời mời đã được gửi. Trạng thái: Đang chờ phản hồi.";
            return RedirectToAction("Details", new { id = groupId });
        }

        // API: get pending invites for current logged-in user's email (used by notification bell)
        [HttpGet]
        public async Task<IActionResult> PendingInvites()
        {
            if (!(User?.Identity?.IsAuthenticated == true)) return Json(new object[0]);
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Json(new object[0]);
            var email = user.Email?.Trim();
            if (string.IsNullOrEmpty(email)) return Json(new object[0]);

            var invites = await _context.Set<GroupInvite>()
                .Where(i => i.InviteeEmail == email && i.Status == InviteStatus.Pending)
                .Include(i => i.Group)
                .OrderByDescending(i => i.CreatedAt)
                .Select(i => new { i.Id, i.GroupId, GroupName = i.Group != null ? i.Group.Name : "(Nhóm)" , i.CreatedAt, i.Token })
                .ToListAsync();

            return Json(invites);
        }

        // Allow user to respond to invite via token link or notification
        [AllowAnonymous]
        public async Task<IActionResult> RespondInvite(string token, string response)
        {
            if (string.IsNullOrEmpty(token)) return NotFound();
            var invite = await _context.Set<GroupInvite>().FirstOrDefaultAsync(i => i.Token == token);
            if (invite == null) return NotFound();

            // require login for accept/decline to bind to user
            if (!(User?.Identity?.IsAuthenticated == true))
            {
                var returnUrl = Url.Action("RespondInvite", "Groups", new { token = token, response = response });
                return RedirectToPage("/Account/Login", new { area = "Identity", ReturnUrl = returnUrl });
            }

            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Forbid();

            // ensure invite email matches logged in user's email
            if (!string.Equals(user.Email?.Trim(), invite.InviteeEmail?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Email tài khoản không khớp với lời mời.";
                return RedirectToAction("Index", "Home");
            }

            if (string.Equals(response, "accept", StringComparison.OrdinalIgnoreCase))
            {
                if (invite.Status != InviteStatus.Pending)
                {
                    TempData["Error"] = "Lời mời không còn hợp lệ.";
                    return RedirectToAction("Index", "Home");
                }

                // add member if not already
                var already = await _context.GroupMembers.AnyAsync(m => m.GroupId == invite.GroupId && m.UserId == user.Id);
                if (!already)
                {
                    _context.GroupMembers.Add(new GroupMember { GroupId = invite.GroupId, UserId = user.Id, JoinedAt = DateTime.UtcNow });

                    // backfill permissions from group shares
                    var shares = await _context.Set<GroupShare>().Where(gs => gs.GroupId == invite.GroupId).ToListAsync();
                    foreach (var gs in shares)
                    {
                        if (gs.DocumentId.HasValue)
                        {
                            if (!await _context.Permissions.AnyAsync(p => p.DocumentId == gs.DocumentId.Value && p.UserId == user.Id))
                                _context.Permissions.Add(new Permission { DocumentId = gs.DocumentId.Value, UserId = user.Id, AccessType = gs.AccessType, SharedDate = DateTime.UtcNow });
                        }
                        if (gs.FolderId.HasValue)
                        {
                            if (!await _context.Permissions.AnyAsync(p => p.FolderId == gs.FolderId.Value && p.UserId == user.Id))
                                _context.Permissions.Add(new Permission { FolderId = gs.FolderId.Value, UserId = user.Id, AccessType = gs.AccessType, SharedDate = DateTime.UtcNow });
                        }
                    }
                }

                invite.Status = InviteStatus.Accepted;
                await _context.SaveChangesAsync();

                TempData["Message"] = "Bạn đã chấp nhận lời mời và trở thành thành viên nhóm.";
                return RedirectToAction("Details", new { id = invite.GroupId });
            }
            else
            {
                // decline
                invite.Status = InviteStatus.Declined;
                await _context.SaveChangesAsync();
                TempData["Message"] = "Bạn đã từ chối lời mời.";
                return RedirectToAction("Index", "Home");
            }
        }

        // ==========================================
        // 6. CHIA SẺ FILE/FOLDER VÀO NHÓM
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ShareDocumentToGroup(int groupId, int documentId, string accessType)
        {
            var group = await _context.Groups.FirstOrDefaultAsync(g => g.Id == groupId);
            if (group == null) return NotFound();

            // Kiểm tra quyền (chỉ chủ nhóm hoặc người có quyền mới được share - ở đây demo check chủ nhóm)
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            // Allow if group owner OR the user is the owner of the document and is a member of the group
            var doc = await _context.Documents.FindAsync(documentId);
            if (doc == null) { TempData["Error"] = "Tài liệu không tồn tại."; return RedirectToAction("Details", new { id = groupId }); }

            if (group.OwnerId != userId)
            {
                // require that the user is a group member and also the owner of the document
                var isMemberOfGroup = await _context.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId);
                if (!isMemberOfGroup || !string.Equals(doc.OwnerId, userId, StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }
            }

            if (!Enum.TryParse(accessType, out AccessLevel level)) level = AccessLevel.Read;

            // Check trùng
            var already = await _context.Set<GroupShare>().AnyAsync(gs => gs.GroupId == groupId && gs.DocumentId == documentId);
            if (already)
            {
                TempData["Error"] = "Tệp đã được chia sẻ cho nhóm này.";
                return RedirectToAction("Details", new { id = groupId });
            }

            // 1. Tạo GroupShare
            _context.Set<GroupShare>().Add(new GroupShare { GroupId = groupId, DocumentId = documentId, AccessType = level, SharedDate = DateTime.UtcNow });

            // 2. Tạo Permission cho TẤT CẢ thành viên hiện tại
            var members = await _context.GroupMembers.Where(m => m.GroupId == groupId).ToListAsync();
            foreach (var m in members)
            {
                var exists = await _context.Permissions.AnyAsync(p => p.DocumentId == documentId && p.UserId == m.UserId);
                if (!exists)
                {
                    _context.Permissions.Add(new Permission { DocumentId = documentId, UserId = m.UserId, AccessType = level, SharedDate = DateTime.UtcNow });
                }
            }

            await _context.SaveChangesAsync();
            TempData["Message"] = "Đã chia sẻ tài liệu cho nhóm.";
            return RedirectToAction("Details", new { id = groupId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ShareFolderToGroup(int groupId, int folderId, string accessType)
        {
            var group = await _context.Groups.FirstOrDefaultAsync(g => g.Id == groupId);
            if (group == null) return NotFound();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            var folder = await _context.Folders.FindAsync(folderId);
            if (folder == null) { TempData["Error"] = "Thư mục không tồn tại."; return RedirectToAction("Details", new { id = groupId }); }

            // Allow if group owner OR the user is group member and owner of the folder
            if (group.OwnerId != userId)
            {
                var isMemberOfGroup = await _context.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId);
                if (!isMemberOfGroup || !string.Equals(folder.OwnerId, userId, StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }
            }

            if (!Enum.TryParse(accessType, out AccessLevel level)) level = AccessLevel.Read;

            var alreadyFolder = await _context.Set<GroupShare>().AnyAsync(gs => gs.GroupId == groupId && gs.FolderId == folderId);
            if (alreadyFolder)
            {
                TempData["Error"] = "Thư mục đã được chia sẻ cho nhóm này.";
                return RedirectToAction("Details", new { id = groupId });
            }

            // 1. Tạo GroupShare
            _context.Set<GroupShare>().Add(new GroupShare { GroupId = groupId, FolderId = folderId, AccessType = level, SharedDate = DateTime.UtcNow });

            // 2. Tạo Permission cho thành viên
            var members = await _context.GroupMembers.Where(m => m.GroupId == groupId).ToListAsync();
            foreach (var m in members)
            {
                var exists = await _context.Permissions.AnyAsync(p => p.FolderId == folderId && p.UserId == m.UserId);
                if (!exists)
                {
                    _context.Permissions.Add(new Permission { FolderId = folderId, UserId = m.UserId, AccessType = level, SharedDate = DateTime.UtcNow });
                }
            }

            await _context.SaveChangesAsync();
            TempData["Message"] = "Đã chia sẻ thư mục cho nhóm.";
            return RedirectToAction("Details", new { id = groupId });
        }

        // ==========================================
        // 7. GỎI BỎ & XÓA
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UnshareFromGroup(int groupId, int shareId)
        {
            var share = await _context.Set<GroupShare>().FindAsync(shareId);
            if (share == null || share.GroupId != groupId) return NotFound();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var group = await _context.Groups.FindAsync(groupId);
            if (group == null) return NotFound();

            // If user is not group owner, allow only if user is owner of the document/folder or has Edit permission on that target
            if (!string.Equals(group.OwnerId, userId, StringComparison.OrdinalIgnoreCase))
            {
                bool allowed = false;
                if (share.DocumentId.HasValue)
                {
                    var doc = await _context.Documents.FindAsync(share.DocumentId.Value);
                    if (doc != null && string.Equals(doc.OwnerId, userId, StringComparison.OrdinalIgnoreCase)) allowed = true;

                    var perm = await _context.Permissions.FirstOrDefaultAsync(p => p.DocumentId == share.DocumentId.Value && p.UserId == userId);
                    if (perm != null && perm.AccessType == AccessLevel.Edit) allowed = true;
                }
                else if (share.FolderId.HasValue)
                {
                    var fol = await _context.Folders.FindAsync(share.FolderId.Value);
                    if (fol != null && string.Equals(fol.OwnerId, userId, StringComparison.OrdinalIgnoreCase)) allowed = true;

                    var perm = await _context.Permissions.FirstOrDefaultAsync(p => p.FolderId == share.FolderId.Value && p.UserId == userId);
                    if (perm != null && perm.AccessType == AccessLevel.Edit) allowed = true;
                }

                if (!allowed) return Forbid();
            }

            int? docId = share.DocumentId;
            int? folId = share.FolderId;

            // Remove GroupShare
            _context.Set<GroupShare>().Remove(share);
            await _context.SaveChangesAsync();

            // Cleanup permissions for group members if no other shares exist for the same target
            var memberIds = await _context.GroupMembers.Where(m => m.GroupId == groupId).Select(m => m.UserId).ToListAsync();

            if (docId.HasValue)
            {
                var otherShares = await _context.Set<GroupShare>().AnyAsync(gs => gs.DocumentId == docId.Value && gs.GroupId != groupId);
                if (!otherShares)
                {
                    var perms = await _context.Permissions.Where(p => p.DocumentId == docId.Value && memberIds.Contains(p.UserId)).ToListAsync();
                    if (perms.Any()) _context.Permissions.RemoveRange(perms);
                }
            }
            else if (folId.HasValue)
            {
                var otherShares = await _context.Set<GroupShare>().AnyAsync(gs => gs.FolderId == folId.Value && gs.GroupId != groupId);
                if (!otherShares)
                {
                    var perms = await _context.Permissions.Where(p => p.FolderId == folId.Value && memberIds.Contains(p.UserId)).ToListAsync();
                    if (perms.Any()) _context.Permissions.RemoveRange(perms);
                }
            }

            await _context.SaveChangesAsync();
            TempData["Message"] = "Đã gỡ chia sẻ khỏi nhóm.";
            return RedirectToAction("Details", new { id = groupId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveMember(int groupId, string userId)
        {
            var group = await _context.Groups.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == groupId);
            if (group == null) return NotFound();

            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // If not authenticated, redirect to login
            if (string.IsNullOrEmpty(currentUserId))
            {
                return Challenge();
            }

            // If userId not provided, assume the current user wants to remove themselves
            if (string.IsNullOrEmpty(userId))
            {
                userId = currentUserId;
            }

            // If trying to remove the owner, disallow
            if (string.Equals(userId, group.OwnerId, StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Không thể loại bỏ chủ nhóm.";
                return RedirectToAction("Details", new { id = groupId });
            }

            // Allow if current user is owner (can remove anyone) OR current user is removing themselves
            if (!string.Equals(group.OwnerId, currentUserId, StringComparison.OrdinalIgnoreCase) && !string.Equals(currentUserId, userId, StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            var member = await _context.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId);
            if (member == null) { TempData["Error"] = "Thành viên không tồn tại."; return RedirectToAction("Details", new { id = groupId }); }

            // 1. Xóa Permission của user này đối với các file của nhóm
            var shares = await _context.Set<GroupShare>().Where(gs => gs.GroupId == groupId).ToListAsync();
            foreach (var gs in shares)
            {
                if (gs.DocumentId.HasValue)
                {
                    // Chỉ xóa nếu file này không được share từ nguồn khác (check nhanh)
                    var other = await _context.Set<GroupShare>().AnyAsync(x => x.DocumentId == gs.DocumentId && x.GroupId != groupId);
                    if (!other)
                    {
                        var perms = await _context.Permissions.Where(p => p.DocumentId == gs.DocumentId && p.UserId == userId).ToListAsync();
                        if (perms.Any()) _context.Permissions.RemoveRange(perms);
                    }
                }
                if (gs.FolderId.HasValue)
                {
                    var other = await _context.Set<GroupShare>().AnyAsync(x => x.FolderId == gs.FolderId && x.GroupId != groupId);
                    if (!other)
                    {
                        var perms = await _context.Permissions.Where(p => p.FolderId == gs.FolderId && p.UserId == userId).ToListAsync();
                        if (perms.Any()) _context.Permissions.RemoveRange(perms);
                    }
                }
            }

            // 2. Xóa thành viên
            _context.GroupMembers.Remove(member);
            await _context.SaveChangesAsync();

            TempData["Message"] = string.Equals(currentUserId, userId, StringComparison.OrdinalIgnoreCase) ? "Bạn đã rời nhóm." : "Đã loại bỏ thành viên.";
            return RedirectToAction("Details", new { id = groupId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var group = await _context.Groups.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == id);
            if (group == null) return NotFound();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (group.OwnerId != userId) return Forbid();

            // Xóa GroupShares và Permissions liên quan
            var shares = await _context.Set<GroupShare>().Where(gs => gs.GroupId == id).ToListAsync();
            var memberIds = group.Members.Select(m => m.UserId).ToList();

            if (shares.Any())
            {
                // Cleanup permissions logic similar to RemoveMember but bulk
                // (Để đơn giản hóa: ở đây chỉ xóa GroupShares, Permission có thể giữ lại hoặc clean kỹ hơn tùy nhu cầu)
                _context.Set<GroupShare>().RemoveRange(shares);
            }

            _context.Groups.Remove(group);
            await _context.SaveChangesAsync();

            TempData["Message"] = "Đã xóa nhóm.";
            return RedirectToAction("Index");
        }

        // GET: show confirmation page for deleting a group
        [HttpGet]
        public async Task<IActionResult> DeleteConfirm(int id)
        {
            var group = await _context.Groups.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == id);
            if (group == null) return NotFound();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (group.OwnerId != userId) return Forbid();

            return View(group);
        }

        // API cho Dropdown chọn nhóm ở Modal Chia sẻ
        [HttpGet]
        public async Task<IActionResult> UserGroups()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId)) return Json(new object[0]);

            var owned = await _context.Groups.Where(g => g.OwnerId == userId).Select(g => new { Id = g.Id, Name = g.Name }).ToListAsync();
            var memberGroupIds = await _context.GroupMembers.Where(m => m.UserId == userId).Select(m => m.GroupId).ToListAsync();
            var member = await _context.Groups.Where(g => memberGroupIds.Contains(g.Id)).Select(g => new { Id = g.Id, Name = g.Name }).ToListAsync();

            var combined = owned.Concat(member).GroupBy(g => g.Id).Select(g => g.First()).ToList();
            return Json(combined);
        }

        // ==========================================
        // EDIT GROUP
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var group = await _context.Groups.FirstOrDefaultAsync(g => g.Id == id);
            if (group == null) return NotFound();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (group.OwnerId != userId) return Forbid();

            return View(group);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(Group model)
        {
            if (!ModelState.IsValid) return View(model);

            var existing = await _context.Groups.FirstOrDefaultAsync(g => g.Id == model.Id);
            if (existing == null) return NotFound();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (existing.OwnerId != userId) return Forbid();

            // Update allowed fields only
            existing.Name = model.Name;
            await _context.SaveChangesAsync();

            TempData["Message"] = "Đã cập nhật tên nhóm.";
            return RedirectToAction("Details", new { id = existing.Id });
        }

        // ==========================================
        // CHUYỂN QUYỀN SỞ HỮU NHÓM
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> TransferOwnership(int groupId, string newOwnerId)
        {
            if (string.IsNullOrEmpty(newOwnerId))
            {
                TempData["Error"] = "Chưa chọn người nhận quyền.";
                return RedirectToAction("Details", new { id = groupId });
            }

            var group = await _context.Groups.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == groupId);
            if (group == null) return NotFound();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (group.OwnerId != userId) return Forbid(); // only owner can transfer

            // Ensure new owner is a member
            if (!group.Members.Any(m => m.UserId == newOwnerId))
            {
                TempData["Error"] = "Người nhận phải là thành viên của nhóm.";
                return RedirectToAction("Details", new { id = groupId });
            }

            var oldOwner = group.OwnerId;
            group.OwnerId = newOwnerId;
            await _context.SaveChangesAsync();

            // Optionally ensure new owner is in members (already checked) and keep old owner as member
            // Audit log
            // await AuditHelper.LogAsync(HttpContext, "TRANSFER_OWNERSHIP", "Group", group.Id, group.Name, $"Ownership transferred from {oldOwner} to {newOwnerId}");

            TempData["Message"] = "Đã chuyển quyền chủ nhóm.";
            return RedirectToAction("Details", new { id = groupId });
        }

        // POST: Create an invite link for group (owner only). Returns JSON { url }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateInvite(int groupId, string scope = "restricted")
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            var group = await _context.Groups.FirstOrDefaultAsync(g => g.Id == groupId);
            if (group == null) return NotFound();
            if (group.OwnerId != userId) return Forbid();

            var token = GenerateToken();
            var link = new ShareLink
            {
                Token = token,
                TargetType = "group",
                TargetId = groupId,
                AccessType = AccessLevel.Read,
                IsPublic = string.Equals(scope, "public", StringComparison.OrdinalIgnoreCase),
                CreatedBy = userId,
                CreatedAt = DateTime.UtcNow
            };

            _context.Set<ShareLink>().Add(link);
            await _context.SaveChangesAsync();

            var url = Url.Action("JoinByToken", "Groups", new { token = token }, Request.Scheme);
            return Json(new { url });
        }

        // GET: allow a user to join a group via token link
        [AllowAnonymous]
        public async Task<IActionResult> JoinByToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return NotFound();
            var link = await _context.Set<ShareLink>().FirstOrDefaultAsync(s => s.Token == token && s.TargetType == "group");
            if (link == null) return NotFound();

            // If link is restricted, require login
            if (!link.IsPublic && !(User?.Identity?.IsAuthenticated == true))
            {
                var returnUrl = Url.Action("JoinByToken", "Groups", new { token = token });
                return RedirectToPage("/Account/Login", new { area = "Identity", ReturnUrl = returnUrl });
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                // For public links, if anonymous, redirect to login to capture user context (app expects logged-in users to join)
                var returnUrl = Url.Action("JoinByToken", "Groups", new { token = token });
                return RedirectToPage("/Account/Login", new { area = "Identity", ReturnUrl = returnUrl });
            }

            var group = await _context.Groups.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == link.TargetId);
            if (group == null) return NotFound();

            // If already a member, redirect to details
            if (group.Members.Any(m => m.UserId == userId))
            {
                TempData["Message"] = "Bạn đã là thành viên nhóm.";
                return RedirectToAction("Details", new { id = group.Id });
            }

            // Add member
            _context.GroupMembers.Add(new GroupMember { GroupId = group.Id, UserId = userId, JoinedAt = DateTime.UtcNow });

            // Backfill permissions for existing group shares
            var shares = await _context.Set<GroupShare>().Where(gs => gs.GroupId == group.Id).ToListAsync();
            foreach (var gs in shares)
            {
                if (gs.DocumentId.HasValue)
                {
                    if (!await _context.Permissions.AnyAsync(p => p.DocumentId == gs.DocumentId.Value && p.UserId == userId))
                    {
                        _context.Permissions.Add(new Permission { DocumentId = gs.DocumentId.Value, UserId = userId, AccessType = gs.AccessType, SharedDate = DateTime.UtcNow });
                    }
                }
                if (gs.FolderId.HasValue)
                {
                    if (!await _context.Permissions.AnyAsync(p => p.FolderId == gs.FolderId.Value && p.UserId == userId))
                    {
                        _context.Permissions.Add(new Permission { FolderId = gs.FolderId.Value, UserId = userId, AccessType = gs.AccessType, SharedDate = DateTime.UtcNow });
                    }
                }
            }

            await _context.SaveChangesAsync();

            TempData["Message"] = "Bạn đã tham gia nhóm thông qua liên kết.";
            return RedirectToAction("Details", new { id = group.Id });
        }

        private string GenerateToken()
        {
            var bytes = new byte[12];
            RandomNumberGenerator.Fill(bytes);
            return WebEncoders.Base64UrlEncode(bytes);
        }

        // ==========================================
        // CẬP NHẬT QUYỀN TRUY CẬP CHIA SẺ
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateShareAccess(int shareId, string accessType)
        {
            var share = await _context.Set<GroupShare>().FindAsync(shareId);
            if (share == null) return NotFound();

            var group = await _context.Groups.FindAsync(share.GroupId);
            if (group == null) return NotFound();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // Only group owner or resource owner can change access
            bool isAllowed = false;
            if (group.OwnerId == userId) isAllowed = true;

            if (!isAllowed)
            {
                if (share.DocumentId.HasValue)
                {
                    var doc = await _context.Documents.FindAsync(share.DocumentId.Value);
                    if (doc != null && string.Equals(doc.OwnerId, userId, StringComparison.OrdinalIgnoreCase)) isAllowed = true;
                }
                else if (share.FolderId.HasValue)
                {
                    var fol = await _context.Folders.FindAsync(share.FolderId.Value);
                    if (fol != null && string.Equals(fol.OwnerId, userId, StringComparison.OrdinalIgnoreCase)) isAllowed = true;
                }
            }

            if (!isAllowed) return Forbid();

            if (!Enum.TryParse(accessType, out AccessLevel level)) level = AccessLevel.Read;

            // Update share
            share.AccessType = level;
            share.SharedDate = DateTime.UtcNow;
            _context.Update(share);

            // Sync Permissions for current group members
            var memberIds = await _context.GroupMembers.Where(m => m.GroupId == share.GroupId).Select(m => m.UserId).ToListAsync();

            if (share.DocumentId.HasValue)
            {
                var docId = share.DocumentId.Value;
                foreach (var mId in memberIds)
                {
                    var perm = await _context.Permissions.FirstOrDefaultAsync(p => p.DocumentId == docId && p.UserId == mId);
                    if (perm != null)
                    {
                        perm.AccessType = level;
                        perm.SharedDate = DateTime.UtcNow;
                        _context.Update(perm);
                    }
                    else
                    {
                        _context.Permissions.Add(new Permission { DocumentId = docId, UserId = mId, AccessType = level, SharedDate = DateTime.UtcNow });
                    }
                }
            }
            else if (share.FolderId.HasValue)
            {
                var folId = share.FolderId.Value;
                foreach (var mId in memberIds)
                {
                    var perm = await _context.Permissions.FirstOrDefaultAsync(p => p.FolderId == folId && p.UserId == mId);
                    if (perm != null)
                    {
                        perm.AccessType = level;
                        perm.SharedDate = DateTime.UtcNow;
                        _context.Update(perm);
                    }
                    else
                    {
                        _context.Permissions.Add(new Permission { FolderId = folId, UserId = mId, AccessType = level, SharedDate = DateTime.UtcNow });
                    }
                }
            }

            await _context.SaveChangesAsync();
            TempData["Message"] = "Đã cập nhật quyền chia sẻ.";
            return RedirectToAction("Details", new { id = share.GroupId });
        }
    }
}