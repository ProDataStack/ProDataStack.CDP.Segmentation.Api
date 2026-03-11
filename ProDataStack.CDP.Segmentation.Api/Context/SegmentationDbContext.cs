using Microsoft.EntityFrameworkCore;

namespace ProDataStack.CDP.Segmentation.Api.Context
{
    public partial class SegmentationDbContext : DbContext
    {
        public SegmentationDbContext()
        {
        }

        public SegmentationDbContext(DbContextOptions<SegmentationDbContext> options)
            : base(options)
        {
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                optionsBuilder.UseSqlServer("Server=localhost;Database=cdp-segmentation;User Id=sa;Password=Password123!;TrustServerCertificate=True");
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            OnModelCreatingPartial(modelBuilder);
        }

        partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
    }
}
