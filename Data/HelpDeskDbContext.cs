using AdefHelpDeskGraphExplorer.Models.HelpDesk;
using Microsoft.EntityFrameworkCore;

namespace AdefHelpDeskGraphExplorer.Data;

public class HelpDeskDbContext : DbContext
{
    public HelpDeskDbContext(DbContextOptions<HelpDeskDbContext> options) : base(options) { }

    public DbSet<HdTask> Tasks => Set<HdTask>();
    public DbSet<HdTaskDetail> TaskDetails => Set<HdTaskDetail>();
    public DbSet<HdTaskCategory> TaskCategories => Set<HdTaskCategory>();
    public DbSet<HdCategory> Categories => Set<HdCategory>();
    public DbSet<HdUser> Users => Set<HdUser>();
}
