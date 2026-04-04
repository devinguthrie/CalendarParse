using Microsoft.EntityFrameworkCore;

namespace CalendarParse.Api.Data;

public class JobDbContext : DbContext
{
    public DbSet<Job> Jobs { get; set; } = null!;

    public JobDbContext(DbContextOptions<JobDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Job>().HasKey(j => j.Id);
        b.Entity<Job>().Property(j => j.Status).HasConversion<int>();
    }
}
