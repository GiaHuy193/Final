using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebDocumentManagement_FileSharing.Data;
using WebDocumentManagement_FileSharing.Models; // QUAN TRỌNG: Để dùng ApplicationUser

namespace WebDocumentManagement_FileSharing.Areas.Identity.Pages.Account
{
    public class LoginModel : PageModel
    {
        // 1. Đổi IdentityUser -> ApplicationUser
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILogger<LoginModel> _logger;
        private readonly ApplicationDbContext _context;

        // 2. Cập nhật Constructor
        public LoginModel(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager, ILogger<LoginModel> logger, ApplicationDbContext context)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _logger = logger;
            _context = context;
        }

        [BindProperty]
        public InputModel Input { get; set; }

        public IList<AuthenticationScheme> ExternalLogins { get; set; }

        public string ReturnUrl { get; set; }

        [TempData]
        public string ErrorMessage { get; set; }

        public class InputModel
        {
            [Required]
            [EmailAddress]
            public string Email { get; set; }

            [Required]
            [DataType(DataType.Password)]
            public string Password { get; set; }

            [Display(Name = "Remember me?")]
            public bool RememberMe { get; set; }
        }

        public async Task OnGetAsync(string returnUrl = null)
        {
            if (!string.IsNullOrEmpty(ErrorMessage))
            {
                ModelState.AddModelError(string.Empty, ErrorMessage);
            }

            returnUrl ??= Url.Content("~/");

            // Xóa cookie bên ngoài để đảm bảo quy trình đăng nhập sạch sẽ
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

            ExternalLogins = (await _signInManager.GetExternalAuthenticationSchemesAsync()).ToList();

            // Show maintenance message if enabled (for display purposes)
            try
            {
                var mode = await _context.SystemSettings.FirstOrDefaultAsync(s => s.SettingKey == "MaintenanceMode");
                var msg = await _context.SystemSettings.FirstOrDefaultAsync(s => s.SettingKey == "MaintenanceMessage");
                if (mode != null && (mode.SettingValue ?? "").ToLower() == "true")
                {
                    ViewData["MaintenanceActive"] = true;
                    ViewData["MaintenanceMessage"] = msg?.SettingValue ?? "Hệ thống đang bảo trì.";
                }
                else
                {
                    ViewData["MaintenanceActive"] = false;
                }
            }
            catch { ViewData["MaintenanceActive"] = false; }

            // Hiển thị thông báo nếu cần xác thực email sau khi đăng ký
            if (Request.Query.ContainsKey("requireConfirmation") && Request.Query["requireConfirmation"] == "true")
            {
                ModelState.AddModelError(string.Empty, "Vui lòng xác thực email của bạn. Kiểm tra hộp thư đến để lấy link xác nhận.");
            }

            ReturnUrl = returnUrl;
        }

        public async Task<IActionResult> OnPostAsync(string returnUrl = null)
        {
            returnUrl ??= Url.Content("~/");

            ExternalLogins = (await _signInManager.GetExternalAuthenticationSchemesAsync()).ToList();

            if (ModelState.IsValid)
            {
                // Tìm user bằng Email trước (để hỗ trợ đăng nhập bằng Email)
                var user = await _userManager.FindByEmailAsync(Input.Email);
                if (user == null)
                {
                    // Nếu không tìm thấy user, chuyển hướng sang trang Đăng ký
                    TempData["Error"] = "Bạn chưa tạo tài khoản";
                    return RedirectToPage("/Account/Register", new { area = "Identity" });
                }

                // Check maintenance mode: if active, block non-admin users from logging in
                try
                {
                    var mode = await _context.SystemSettings.FirstOrDefaultAsync(s => s.SettingKey == "MaintenanceMode");
                    var msg = await _context.SystemSettings.FirstOrDefaultAsync(s => s.SettingKey == "MaintenanceMessage");
                    var isMaintenance = mode != null && (mode.SettingValue ?? "").ToLower() == "true";
                    if (isMaintenance)
                    {
                        // allow admins through
                        if (!await _userManager.IsInRoleAsync(user, "Admin"))
                        {
                            var message = msg?.SettingValue ?? "Hệ thống đang trong thời gian bảo trì. Vui lòng quay lại sau.";
                            ModelState.AddModelError(string.Empty, message);
                            return Page();
                        }
                    }
                }
                catch { /* ignore errors reading settings */ }

                // Kiểm tra xem email đã được xác thực chưa
                if (!await _userManager.IsEmailConfirmedAsync(user))
                {
                    ModelState.AddModelError(string.Empty, "Tài khoản chưa xác thực.");
                    return Page();
                }

                // Thực hiện đăng nhập (Sử dụng UserName lấy từ user vừa tìm được)
                var result = await _signInManager.PasswordSignInAsync(user.UserName, Input.Password, Input.RememberMe, lockoutOnFailure: false);

                if (result.Succeeded)
                {
                    _logger.LogInformation("User logged in.");

                    // LOGIC RIÊNG CỦA BẠN: Nếu là Admin thì vào trang Dashboard
                    if (await _userManager.IsInRoleAsync(user, "Admin"))
                    {
                        return Redirect("/Admin/Dashboard");
                    }

                    return LocalRedirect(returnUrl);
                }
                if (result.RequiresTwoFactor)
                {
                    return RedirectToPage("./LoginWith2fa", new { ReturnUrl = returnUrl, RememberMe = Input.RememberMe });
                }
                if (result.IsLockedOut)
                {
                    _logger.LogWarning("User account locked out.");
                    return RedirectToPage("./Lockout");
                }
                else
                {
                    // Sai mật khẩu
                    ModelState.AddModelError(string.Empty, "Bạn nhập sai mật khẩu hoặc tên email.");
                    return Page();
                }
            }

            // Nếu form không hợp lệ, hiển thị lại
            return Page();
        }
    }
}