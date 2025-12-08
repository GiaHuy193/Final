using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using WebDocumentManagement_FileSharing.Models;

namespace WebDocumentManagement_FileSharing.Data
{
    public class ApplicationDbContext : IdentityDbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }
        public DbSet<WebDocumentManagement_FileSharing.Models.Document> Document { get; set; } = default!;
    }
}
