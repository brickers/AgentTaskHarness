using AgentTaskHarness.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
	public DbSet<Board> Boards => Set<Board>();
	public DbSet<Feature> Features => Set<Feature>();
	public DbSet<Step> Steps => Set<Step>();
	public DbSet<FeatureDependency> FeatureDependencies => Set<FeatureDependency>();
	public DbSet<StepDependency> StepDependencies => Set<StepDependency>();
	public DbSet<Comment> Comments => Set<Comment>();
	public DbSet<AgentDefinition> AgentDefinitions => Set<AgentDefinition>();
	public DbSet<AgentComponent> AgentComponents => Set<AgentComponent>();
	public DbSet<AgentDefinitionComponent> AgentDefinitionComponents => Set<AgentDefinitionComponent>();
	public DbSet<AgentColumnAssignment> AgentColumnAssignments => Set<AgentColumnAssignment>();
	public DbSet<AgentRun> AgentRuns => Set<AgentRun>();

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		modelBuilder.Entity<Board>(entity =>
		{
			entity.Property(board => board.Name).HasMaxLength(200).IsRequired();
			entity.Property(board => board.RepoPath).HasMaxLength(1_024).IsRequired();
			entity.ToTable(table =>
			{
				table.HasCheckConstraint("CK_Boards_ConcurrencyLimit", "ConcurrencyLimit > 0");
				table.HasCheckConstraint("CK_Boards_AgentReviewFailThreshold", "AgentReviewFailThreshold >= 0");
				table.HasCheckConstraint("CK_Boards_HumanReviewFailThreshold", "HumanReviewFailThreshold >= 0");
			});
		});

		modelBuilder.Entity<Feature>(entity =>
		{
			entity.Property(feature => feature.Title).HasMaxLength(500).IsRequired();
			entity.Property(feature => feature.Requirements).IsRequired();
			entity.Property(feature => feature.AcceptanceCriteria).IsRequired();
			entity.Property(feature => feature.SuggestedSolution).IsRequired();
			entity.Property(feature => feature.BranchName).HasMaxLength(200);
			entity.Property(feature => feature.WorktreePath).HasMaxLength(1_024);
			entity.Property(feature => feature.MergeConflictPending).HasDefaultValue(false);
			entity.HasIndex(feature => new { feature.BoardId, feature.WorkflowColumn });
			entity.HasOne(feature => feature.Board)
				.WithMany(board => board.Features)
				.HasForeignKey(feature => feature.BoardId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		modelBuilder.Entity<Step>(entity =>
		{
			entity.Property(step => step.Title).HasMaxLength(500).IsRequired();
			entity.Property(step => step.Description).IsRequired();
			entity.Property(step => step.GuidanceNotes).IsRequired();
			entity.Property(step => step.BranchName).HasMaxLength(200);
			entity.Property(step => step.WorktreePath).HasMaxLength(1_024);
			entity.Property(step => step.MergeConflictPending).HasDefaultValue(false);
			entity.HasIndex(step => new { step.FeatureId, step.WorkflowColumn });
			entity.HasOne(step => step.Feature)
				.WithMany(feature => feature.Steps)
				.HasForeignKey(step => step.FeatureId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		modelBuilder.Entity<FeatureDependency>(entity =>
		{
			entity.HasKey(dependency => new { dependency.FeatureId, dependency.DependsOnFeatureId });
			entity.HasOne(dependency => dependency.Feature)
				.WithMany(feature => feature.Dependencies)
				.HasForeignKey(dependency => dependency.FeatureId)
				.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(dependency => dependency.DependsOnFeature)
				.WithMany(feature => feature.DependedOnBy)
				.HasForeignKey(dependency => dependency.DependsOnFeatureId)
				.OnDelete(DeleteBehavior.Restrict);
		});

		modelBuilder.Entity<StepDependency>(entity =>
		{
			entity.HasKey(dependency => new { dependency.StepId, dependency.DependsOnStepId });
			entity.HasOne(dependency => dependency.Step)
				.WithMany(step => step.Dependencies)
				.HasForeignKey(dependency => dependency.StepId)
				.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(dependency => dependency.DependsOnStep)
				.WithMany(step => step.DependedOnBy)
				.HasForeignKey(dependency => dependency.DependsOnStepId)
				.OnDelete(DeleteBehavior.Restrict);
		});

		modelBuilder.Entity<Comment>(entity =>
		{
			entity.Property(comment => comment.Author).HasMaxLength(200).IsRequired();
			entity.Property(comment => comment.Body).IsRequired();
			entity.HasIndex(comment => new { comment.CardType, comment.CardId, comment.CreatedAt });
		});

		modelBuilder.Entity<AgentDefinition>(entity =>
		{
			entity.Property(definition => definition.Name).HasMaxLength(200).IsRequired();
			entity.Property(definition => definition.FolderPath).HasMaxLength(1_024).IsRequired();
			entity.HasIndex(definition => new { definition.BoardId, definition.Name }).IsUnique();
			entity.HasOne(definition => definition.Board)
				.WithMany(board => board.AgentDefinitions)
				.HasForeignKey(definition => definition.BoardId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		modelBuilder.Entity<AgentComponent>(entity =>
		{
			entity.Property(component => component.Name).HasMaxLength(200).IsRequired();
			entity.Property(component => component.ConfigContent).IsRequired();
		});

		modelBuilder.Entity<AgentDefinitionComponent>(entity =>
		{
			entity.HasKey(component => new { component.AgentDefinitionId, component.ComponentId });
			entity.HasIndex(component => new { component.AgentDefinitionId, component.Order }).IsUnique();
			entity.HasOne(component => component.AgentDefinition)
				.WithMany(definition => definition.Components)
				.HasForeignKey(component => component.AgentDefinitionId)
				.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(component => component.Component)
				.WithMany(agentComponent => agentComponent.AgentDefinitions)
				.HasForeignKey(component => component.ComponentId)
				.OnDelete(DeleteBehavior.Restrict);
		});

		modelBuilder.Entity<AgentColumnAssignment>(entity =>
		{
			entity.Property(assignment => assignment.MatchCriteria).HasMaxLength(1_024);
			entity.HasIndex(assignment => new { assignment.BoardId, assignment.ColumnScope });
			entity.HasOne(assignment => assignment.Board)
				.WithMany(board => board.ColumnAssignments)
				.HasForeignKey(assignment => assignment.BoardId)
				.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(assignment => assignment.AgentDefinition)
				.WithMany(definition => definition.ColumnAssignments)
				.HasForeignKey(assignment => assignment.AgentDefinitionId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		modelBuilder.Entity<AgentRun>(entity =>
		{
			entity.Property(run => run.SessionLink).HasMaxLength(2_048);
			entity.HasIndex(run => new { run.CardType, run.CardId });
		});
	}
}