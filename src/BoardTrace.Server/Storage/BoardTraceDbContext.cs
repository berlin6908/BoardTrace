using BoardTrace.Server.Inspections;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using BoardTrace.Server.Identity;
using BoardTrace.Server.Recipes;

namespace BoardTrace.Server.Storage;

public sealed class BoardTraceDbContext(DbContextOptions<BoardTraceDbContext> options) : IdentityDbContext<BoardTraceUser>(options)
{
    public DbSet<InspectionAttempt> Inspections => Set<InspectionAttempt>();
    public DbSet<InspectionDefect> Defects => Set<InspectionDefect>();
    public DbSet<InspectionImage> Images => Set<InspectionImage>();
    public DbSet<RecipeDraft> RecipeDrafts => Set<RecipeDraft>();
    public DbSet<ValidationRun> ValidationRuns => Set<ValidationRun>();
    public DbSet<RecipePublication> RecipeVersions => Set<RecipePublication>();
    public DbSet<RecipeReferenceAsset> RecipeReferenceAssets => Set<RecipeReferenceAsset>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        var inspection = model.Entity<InspectionAttempt>();
        inspection.ToTable("Inspections");
        inspection.HasKey(x => x.Id);
        inspection.Property(x => x.Id).ValueGeneratedNever();
        inspection.Property(x => x.StationId).HasMaxLength(128);
        inspection.Property(x => x.ProductId).HasMaxLength(128);
        inspection.Property(x => x.OperatorId).HasMaxLength(450);
        inspection.Property(x => x.OperatorName).HasMaxLength(128);
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
        model.Entity<RecipeDraft>().ToTable("RecipeDrafts").HasKey(x => x.Id);
        model.Entity<RecipeDraft>().Property(x => x.Name).HasMaxLength(200);
        model.Entity<RecipeDraft>().Property(x => x.SnapshotHash).HasMaxLength(64).IsUnicode(false);
        model.Entity<ValidationRun>().ToTable("ValidationRuns").HasKey(x => x.Id);
        model.Entity<ValidationRun>().Property(x => x.Status).HasMaxLength(24);
        model.Entity<ValidationRun>().Property(x => x.SnapshotHash).HasMaxLength(64).IsUnicode(false);
        model.Entity<ValidationRun>().HasIndex(x => new { x.Status, x.CreatedAt });
        var version = model.Entity<RecipePublication>();
        version.ToTable("RecipeVersions").HasKey(x => x.Id);
        version.HasIndex(x => x.ValidationRunId).IsUnique();
        version.Property(x => x.Name).HasMaxLength(200);
        version.Property(x => x.BundleHash).HasMaxLength(64).IsUnicode(false);
        version.Property(x => x.PublishedById).HasMaxLength(450);
        version.Property(x => x.PublishedByName).HasMaxLength(128);
        version.HasOne<RecipeDraft>().WithMany().HasForeignKey(x => x.DraftId).OnDelete(DeleteBehavior.Restrict);
        version.HasOne<ValidationRun>().WithMany().HasForeignKey(x => x.ValidationRunId).OnDelete(DeleteBehavior.Restrict);
        version.HasMany(x => x.Assets).WithOne().HasForeignKey(x => x.RecipeVersionId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<RecipeReferenceAsset>().ToTable("RecipeReferenceAssets").HasKey(x => x.Id);
        model.Entity<RecipeReferenceAsset>().Property(x => x.Sha256).HasMaxLength(64).IsUnicode(false);
    }
}
