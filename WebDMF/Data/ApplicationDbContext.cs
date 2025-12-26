using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using WebDocumentManagement_FileSharing.Models;

namespace WebDocumentManagement_FileSharing.Data
{
    public class ApplicationDbContext : IdentityDbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options) { }

        public DbSet<Folder> Folders { get; set; }
        public DbSet<Document> Documents { get; set; }
        public DbSet<DocumentVersion> DocumentVersions { get; set; }
        public DbSet<Permission> Permissions { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Cấu hình Folder cha-con
            modelBuilder.Entity<Folder>()
                .HasOne(f => f.ParentFolder)
                .WithMany(f => f.SubFolders)
                .HasForeignKey(f => f.ParentId)
                .OnDelete(DeleteBehavior.Restrict);

            // Cấu hình Document trong Folder
            modelBuilder.Entity<Document>()
               .HasOne(d => d.Folder)
               .WithMany(f => f.Documents)
               .HasForeignKey(d => d.FolderId)
               .OnDelete(DeleteBehavior.Cascade);

            // Cấu hình DocumentVersion
            modelBuilder.Entity<DocumentVersion>()
                .HasOne(v => v.Document)
                .WithMany(d => d.Versions)
                .HasForeignKey(v => v.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            // Ràng buộc duy nhất cho VersionNumber
            modelBuilder.Entity<DocumentVersion>()
                .HasIndex(v => new { v.DocumentId, v.VersionNumber })
                .IsUnique();

            // Cấu hình Permission - FIX LỖI MULTIPLE CASCADE PATHS
            modelBuilder.Entity<Permission>()
                .HasOne(p => p.Document)
                .WithMany()
                .HasForeignKey(p => p.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Permission>()
                .HasOne(p => p.Folder)
                .WithMany()
                .HasForeignKey(p => p.FolderId)
                .OnDelete(DeleteBehavior.Restrict); // Đổi sang Restrict để tránh lỗi SQL
        }
    }
}