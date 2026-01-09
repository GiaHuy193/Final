using Microsoft.AspNetCore.Mvc;
using PayPalCheckoutSdk.Orders; // Namespace quan trọng của PayPal
using PayPalCheckoutSdk.Core;
using WebDocumentManagement_FileSharing.Data; // DB Context của bạn
using WebDocumentManagement_FileSharing.Models; // Model PaymentTransaction
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Security.Claims;
using System;

public class PaymentController : Controller
{
    private readonly PayPalHttpClient _client;
    private readonly ApplicationDbContext _context;

    public PaymentController(PayPalHttpClient client, ApplicationDbContext context)
    {
        _client = client;
        _context = context;
    }

    // 1. Hiển thị trang chọn gói (Giao diện cũ)
    public IActionResult Premium()
    {
        return View();
    }

    // 2. Xử lý khi nhấn nút "Thanh toán bằng PayPal"
    [HttpPost]
    public async Task<IActionResult> PayPalCheckout()
    {
        // Tạo đơn hàng (Order)
        var request = new OrdersCreateRequest();
        request.Prefer("return=representation");
        request.RequestBody(new OrderRequest()
        {
            CheckoutPaymentIntent = "CAPTURE", 
            PurchaseUnits = new List<PurchaseUnitRequest>()
            {
                new PurchaseUnitRequest()
                {
                    AmountWithBreakdown = new AmountWithBreakdown()
                    {
                        CurrencyCode = "USD", 
                        Value = "8.99" 
                    },
                    Description = "Nâng cấp DocNest Premium (1 Tháng)"
                }
            },
            ApplicationContext = new ApplicationContext()
            {
                ReturnUrl = Url.Action("Success", "Payment", null, Request.Scheme), // Link khi thành công
                CancelUrl = Url.Action("Cancel", "Payment", null, Request.Scheme)   // Link khi hủy
            }
        });

        try
        {
            // Gọi API PayPal
            var response = await _client.Execute(request);
            var result = response.Result<Order>();

            // Lấy link approve (link để người dùng đăng nhập PayPal trả tiền)
            var approveLink = result.Links.Find(x => x.Rel == "approve");

            if (approveLink != null)
            {
                return Redirect(approveLink.Href);
            }
        }
        catch (Exception ex)
        {
            // Log lỗi (ex.Message)
            return View("Failed");
        }

        return View("Failed");
    }

    // 3. Xử lý khi thanh toán thành công (Người dùng quay lại từ PayPal)
    public async Task<IActionResult> Success(string token, string PayerID)
    {
        // Capture Order (Xác thực lấy tiền)
        var request = new OrdersCaptureRequest(token);
        request.RequestBody(new OrderActionRequest());

        try
        {
            var response = await _client.Execute(request);
            var result = response.Result<Order>();

            if (result.Status == "COMPLETED")
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

                // 1. Lưu lịch sử giao dịch
                var transaction = new PaymentTransaction
                {
                    OrderId = result.Id,
                    UserId = userId,
                    Amount = 5.00m,
                    PaymentMethod = "PayPal",
                    Status = "Success",
                    CreatedDate = DateTime.UtcNow // Dùng UtcNow cho chuẩn
                };
                _context.Add(transaction);

                // 2. KÍCH HOẠT PREMIUM (Đã sửa lại code này)
                var user = await _context.Users.FindAsync(userId);
                if (user != null)
                {
                    user.IsPremium = true;

                    // Nếu user đã có hạn Premium thì cộng dồn, nếu chưa thì tính từ hôm nay
                    if (user.PremiumUntil != null && user.PremiumUntil > DateTime.UtcNow)
                    {
                        user.PremiumUntil = user.PremiumUntil.Value.AddDays(30);
                    }
                    else
                    {
                        user.PremiumUntil = DateTime.UtcNow.AddDays(30);
                    }

                    _context.Update(user);
                }

                await _context.SaveChangesAsync();

                return View("Success");
            }
        }
        catch (Exception ex)
        {
            // Log lỗi
        }

        return View("Failed");
    }

    // 4. Xử lý Hủy gói Premium (Downgrade)
    [HttpPost]
    [ValidateAntiForgeryToken] // Bảo mật: Chống giả mạo request từ bên ngoài
    public async Task<IActionResult> Downgrade()
    {
        // 1. Lấy ID người dùng đang đăng nhập
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId == null)
        {
            return Redirect("/Identity/Account/Login");
        }

        // 2. Tìm người dùng trong DB
        var user = await _context.Users.FindAsync(userId);

        // 3. Thực hiện hủy gói
        if (user != null && user.IsPremium)
        {
            user.IsPremium = false; // Tắt trạng thái Premium
            user.PremiumUntil = null; // Xóa ngày hết hạn (hoặc giữ lại tùy bạn)

            _context.Update(user);
            await _context.SaveChangesAsync();
        }

        // 4. Quay lại trang Profile để người dùng thấy kết quả
        // Lưu ý: Đường dẫn này trỏ về Area Identity
        return Redirect("/Identity/Account/Manage");
    }

    public IActionResult Cancel()
    {
        return View("Cancel"); // View thông báo người dùng đã hủy
    }
}