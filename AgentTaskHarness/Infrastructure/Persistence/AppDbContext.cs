using AgentTaskHarness.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
	public DbSet<Board> Boards => Set<Board>();
	public DbSet<Column> Columns => Set<Column>();
	public DbSet<ColumnTransition> ColumnTransitions => Set<ColumnTransition>();
	public DbSet<TaskItem> Tasks => Set<TaskItem>();
	public DbSet<TaskDependency> TaskDependencies => Set<TaskDependency>();
	public DbSet<AgentDefinition> AgentDefinitions => Set<AgentDefinition>();
	public DbSet<AgentComponent> AgentComponents => Set<AgentComponent>();
	public DbSet<AgentDefinitionComponent> AgentDefinitionComponents => Set<AgentDefinitionComponent>();
	public DbSet<AgentRun> AgentRuns => Set<AgentRun>();

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		modelBuilder.Entity<Board>(entity =>
		{
			entity.Property(board => board.Name).HasMaxLength(200).IsRequired();
			entity.Property(board => board.RepoPath).HasMaxLength(1_024).IsRequired();
			entity.ToTable(table => table.HasCheckConstraint("CK_Boards_ConcurrencyLimit", "ConcurrencyLimit > 0"));
		});

		modelBuilder.Entity<Column>(entity =>
		{
			entity.Property(column => column.Name).HasMaxLength(200).IsRequired();
			entity.HasIndex(column => new { column.BoardId, column.Order }).IsUnique();
			entity.HasIndex(column => new { column.BoardId, column.IsBacklog }).IsUnique().HasFilter("IsBacklog = 1");
			entity.HasIndex(column => new { column.BoardId, column.IsTerminal }).IsUnique().HasFilter("IsTerminal = 1");
			entity.HasOne(column => column.Board)
				.WithMany(board => board.Columns)
				.HasForeignKey(column => column.BoardId)
				.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(column => column.AgentDefinition)
				.WithMany(definition => definition.Columns)
				.HasForeignKey(column => column.AgentDefinitionId)
				.OnDelete(DeleteBehavior.SetNull);
		});

		modelBuilder.Entity<ColumnTransition>(entity =>
		{
			entity.HasKey(transition => new { transition.FromColumnId, transition.ToColumnId });
			entity.HasOne(transition => transition.FromColumn)
				.WithMany(column => column.OutgoingTransitions)
				.HasForeignKey(transition => transition.FromColumnId)
				.OnDelete(DeleteBehavior.Restrict);
			entity.HasOne(transition => transition.ToColumn)
				.WithMany(column => column.IncomingTransitions)
				.HasForeignKey(transition => transition.ToColumnId)
				.OnDelete(DeleteBehavior.Restrict);
		});

		modelBuilder.Entity<TaskItem>(entity =>
		{
			entity.Property(task => task.Title).HasMaxLength(500).IsRequired();
			entity.Property(task => task.Description).IsRequired();
			entity.Property(task => task.BranchName).HasMaxLength(200);
			entity.Property(task => task.WorktreePath).HasMaxLength(1_024);
			entity.Property(task => task.MergeConflictPending).HasDefaultValue(false);
			entity.HasIndex(task => new { task.BoardId, task.ColumnId });
			entity.HasOne(task => task.Board)
				.WithMany(board => board.Tasks)
				.HasForeignKey(task => task.BoardId)
				.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(task => task.Column)
				.WithMany(column => column.Tasks)
				.HasForeignKey(task => task.ColumnId)
				.OnDelete(DeleteBehavior.Restrict);
		});

		modelBuilder.Entity<TaskDependency>(entity =>
		{
			entity.HasKey(dependency => new { dependency.TaskId, dependency.DependsOnTaskId });
			entity.HasOne(dependency => dependency.Task)
				.WithMany(task => task.Dependencies)
				.HasForeignKey(dependency => dependency.TaskId)
				.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(dependency => dependency.DependsOnTask)
				.WithMany(task => task.DependedOnBy)
				.HasForeignKey(dependency => dependency.DependsOnTaskId)
				.OnDelete(DeleteBehavior.Restrict);
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

		modelBuilder.Entity<AgentRun>(entity =>
		{
			entity.Property(run => run.SessionLink).HasMaxLength(2_048);
			entity.HasOne(run => run.Task)
				.WithMany(task => task.AgentRuns)
				.HasForeignKey(run => run.TaskId)
				.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(run => run.Column)
				.WithMany(column => column.AgentRuns)
				.HasForeignKey(run => run.ColumnId)
				.OnDelete(DeleteBehavior.Restrict);
		});
	}
}