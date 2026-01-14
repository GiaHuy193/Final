using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using WebDocumentManagement_FileSharing.Models;

namespace WebDocumentManagement_FileSharing.Data
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options) { }

        public DbSet<Folder> Folders { get; set; }
        public DbSet<Document> Documents { get; set; }
        public DbSet<DocumentVersion> DocumentVersions { get; set; }
        public DbSet<Permission> Permissions { get; set; }
        public DbSet<SystemSetting> SystemSettings { get; set; }
        public DbSet<AuditLog> AuditLogs { get; set; }
        public DbSet<ShareLink> ShareLinks { get; set; }
        public DbSet<Group> Groups { get; set; }
        public DbSet<GroupMember> GroupMembers { get; set; }
        public DbSet<GroupShare> GroupShares { get; set; }
        public DbSet<GroupInvite> groupInvites { get; set; }
        public DbSet<PaymentTransaction> PaymentTransactions { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
            base.OnModelCreating(modelBuilder);

            // Group & GroupMember
            modelBuilder.Entity<Group>()
                .HasMany(g => g.Members)
                .WithOne(m => m.Group)
                .HasForeignKey(m => m.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
            
            modelBuilder.Entity<GroupMember>()
                .HasIndex(gm => new { gm.GroupId, gm.UserId })
                .IsUnique();
            
            // GroupShare: record resources shared to group
            modelBuilder.Entity<GroupShare>(b =>
            {
                b.HasKey(gs => gs.Id);
                b.HasIndex(gs => new { gs.GroupId, gs.DocumentId, gs.FolderId }).IsUnique(false);
                b.HasOne<Group>().WithMany().HasForeignKey(gs => gs.GroupId).OnDelete(DeleteBehavior.Cascade);
            });

            // Seed system settings: AllowedExtensions, StandardQuota (15GB), PremiumQuota (100GB)
            modelBuilder.Entity<SystemSetting>().HasData(
                new SystemSetting { Id = 1, SettingKey = "AllowedExtensions", SettingValue = ".pdf,.docx,.png,.jpg", Description = "Danh sách định dạng tệp tin cho phép upload" },
                // StandardQuota: 15 GB in bytes
                new SystemSetting { Id = 2, SettingKey = "StandardQuota", SettingValue = "16106127360", Description = "Hạn mức dung lượng tối đa cho tài khoản Standard (Bytes)" },
                // PremiumQuota: 100 GB in bytes
                new SystemSetting { Id = 3, SettingKey = "PremiumQuota", SettingValue = "107374182400", Description = "Hạn mức dung lượng tối đa cho tài khoản Premium (Bytes)" }
            );

            // configure AuditLog timestamp default
            modelBuilder.Entity<AuditLog>().Property(a => a.Timestamp).HasDefaultValueSql("GETUTCDATE()");

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