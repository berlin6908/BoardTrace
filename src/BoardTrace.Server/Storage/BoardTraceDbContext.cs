using BoardTrace.Server.Inspections;
using Microsoft.EntityFrameworkCore;

namespace BoardTrace.Server.Storage;

public sealed class BoardTraceDbContext(DbContextOptions<BoardTraceDbContext> options) : DbContext(options)
{
    public DbSet<InspectionAttempt> Inspections => Set<InspectionAttempt>();
    public DbSet<InspectionDefect> Defects => Set<InspectionDefect>();
    public DbSet<InspectionImage> Images => Set<InspectionImage>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var inspection = model.Entity<InspectionAttempt>();
        inspection.ToTable("Inspections");
        inspection.HasKey(x => x.Id);
        inspection.Property(x => x.Id).ValueGeneratedNever();
        inspection.Property(x => x.StationId).HasMaxLength(128);
        inspection.Property(x => x.ProductId).HasMaxLength(128);
        inspection.Property(x => x.SampleId).HasMaxLength(128);
        inspection.Property(x => x.SourceKind).HasMaxLength(32);
        inspection.Property(x => x.RecipeId).HasMaxLength(128);
        inspection.Property(x => x.ContentHash).HasMaxLength(64).IsUnicode(false);
        inspection.Property(x => x.ExecutionStatus).HasConversion<string>().HasMaxLength(24);
        inspection.Property(x => x.Decision).HasConversion<string>().HasMaxLength(24);
        inspection.HasIndex(x => new { x.StationId, x.StartedAt });
        inspection.HasIndex(x => new { x.ProductId, x.StartedAt });
        inspection.HasIndex(x => x.StartedAt);
        inspection.HasMany(x => x.Defects).WithOne().HasForeignKey(x => x.InspectionId).OnDelete(DeleteBehavior.Cascade);
        inspection.HasMany(x => x.Images).WithOne().HasForeignKey(x => x.InspectionId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<InspectionDefect>().ToTable("Defects").HasKey(x => new { x.InspectionId, x.Ordinal });
        model.Entity<InspectionImage>().ToTable("InspectionImages").HasKey(x => new { x.InspectionId, x.Kind });
        model.Entity<InspectionImage>().Property(x => x.Kind).HasMaxLength(16);
    }
}
