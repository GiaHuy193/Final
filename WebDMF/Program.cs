using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebDocumentManagement_FileSharing.Data;
using WebDocumentManagement_FileSharing.Service;
using WebDocumentManagement_FileSharing.Helpers;
using WebDocumentManagement_FileSharing.Models; // Bắt buộc có dòng này để dùng ApplicationUser
using System.Text;
using PayPalCheckoutSdk.Core;

var builder = WebApplication.CreateBuilder(args);

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
 options.UseSqlServer(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddTransient<EmailService>();

// Cấu hình PayPal Client
builder.Services.AddSingleton(x => {
    var config = builder.Configuration.GetSection("PayPal");
    var clientId = config["ClientId"];
    var secret = config["Secret"];

    PayPalEnvironment environment = new SandboxEnvironment(clientId, secret);
    return new PayPalHttpClient(environment);
});

builder.Services.AddDefaultIdentity<ApplicationUser>(options => {
    options.SignIn.RequireConfirmedEmail = true;
    options.SignIn.RequireConfirmedAccount = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 6;
})
 .AddRoles<IdentityRole>()
 .AddEntityFrameworkStores<ApplicationDbContext>();
// --------------------------

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuditActionFilter>();

builder.Services.AddControllersWithViews(options => {
    options.Filters.AddService<AuditActionFilter>();
});
builder.Services.AddRazorPages();

var app = builder.Build();

// Seed admin role and user
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    // --- SỬA Ở ĐÂY (Bước 2) ---
    var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
    SeedData(userManager, roleManager).GetAwaiter().GetResult();
}

// --- SỬA Ở ĐÂY (Bước 3) ---
static async Task SeedData(UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
{
    var adminEmail = "Admin@gmail.com";
    var adminPassword = "Admin@123";

    if (!await roleManager.RoleExistsAsync("Admin"))
    {
        await roleManager.CreateAsync(new IdentityRole("Admin"));
    }

    var adminUser = await userManager.FindByEmailAsync(adminEmail);
    if (adminUser == null)
    {
        // Khởi tạo ApplicationUser
        adminUser = new ApplicationUser
        {
            UserName = adminEmail,
            Email = adminEmail,
            EmailConfirmed = true,
            IsPremium = true
        };

        var result = await userManager.CreateAsync(adminUser, adminPassword);
        if (result.Succeeded)
        {
            await userManager.AddToRoleAsync(adminUser, "Admin");
        }
    }
    else
    {
        if (!await userManager.IsInRoleAsync(adminUser, "Admin"))
        {
            await userManager.AddToRoleAsync(adminUser, "Admin");
        }
    }
}
// --------------------------

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
 name: "areas",
 pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");

app.MapControllerRoute(
 name: "default",
 pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapRazorPages();

app.MapGet("/Identity", () => Results.Redirect("/Identity/Account/Login"));

app.Run();