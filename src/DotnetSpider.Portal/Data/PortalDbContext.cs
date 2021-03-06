using DotnetSpider.AgentRegister.Store;
using DotnetSpider.Statistics.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace DotnetSpider.Portal.Data
{
	public class PortalDbContext : DbContext, IDesignTimeDbContextFactory<PortalDbContext>
	{
		private readonly bool _isDesignTime;

		public DbSet<DockerRepository> DockerRepositories { get; set; }

		public DbSet<Spider> Spiders { get; set; }

		public DbSet<SpiderContainer> SpiderContainers { get; set; }

		public PortalDbContext()
		{
			_isDesignTime = true;
		}

		public PortalDbContext(DbContextOptions<PortalDbContext> options, bool isDesignTime = false)
			: base(options)
		{
			_isDesignTime = isDesignTime;
		}

		protected override void OnModelCreating(ModelBuilder builder)
		{
			base.OnModelCreating(builder);

			if (!_isDesignTime)
			{
				builder.Model.AddEntityType(typeof(AgentInfo));
				builder.Model.AddEntityType(typeof(AgentHeartbeat));
				builder.Model.AddEntityType(typeof(SpiderStatistics));
				builder.Model.AddEntityType(typeof(AgentStatistics));
			}

			builder.Entity<DockerRepository>().HasIndex(x => x.Name).IsUnique();
			builder.Entity<DockerRepository>().HasIndex(x => new {x.Repository, x.Registry}).IsUnique();
			builder.Entity<DockerRepository>().HasIndex(x => x.CreationTime);

			builder.Entity<Spider>().HasIndex(x => x.Name);
			builder.Entity<Spider>().HasIndex(x => x.CreationTime);

			builder.Entity<SpiderContainer>().HasIndex(x => x.Batch);
			builder.Entity<SpiderContainer>().HasIndex(x => x.SpiderId);
			builder.Entity<SpiderContainer>().HasIndex(x => x.CreationTime);
		}

		public PortalDbContext CreateDbContext(string[] args)
		{
			var builder = new DbContextOptionsBuilder<PortalDbContext>();

			var configurationBuilder = new ConfigurationBuilder();
			configurationBuilder.AddJsonFile("appsettings.json");
			var configuration = configurationBuilder.Build();
			var options = new PortalOptions(configuration);
			switch (options.DatabaseType?.ToLower())
			{
				case "mysql":
				{
					builder.UseMySql(options.ConnectionString);
					break;
				}

				default:
				{
					builder.UseSqlServer(options.ConnectionString);
					break;
				}
			}

			return new PortalDbContext(builder.Options, true);
		}
	}
}
