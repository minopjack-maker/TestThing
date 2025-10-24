using Microsoft.EntityFrameworkCore;

public class VisitDbContext : DbContext
{
    public VisitDbContext(DbContextOptions<VisitDbContext> options) : base(options) { }
    public DbSet<Visit> Visits => Set<Visit>();
}
