using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebDocumentManagement_FileSharing.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class ProfileController : Controller
    {
        // Redirect admin to the Identity manage page so admin can edit their profile like normal users
        public IActionResult Index()
        {
            return LocalRedirect("/Identity/Account/Manage");
        }

        // Optional: provide a route to open the manage page in a new tab/view
        public IActionResult Manage()
        {
            return LocalRedirect("/Identity/Account/Manage");
        }
    }
}
