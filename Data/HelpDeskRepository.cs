using AdefHelpDeskGraphExplorer.Models.HelpDesk;
using Microsoft.EntityFrameworkCore;

namespace AdefHelpDeskGraphExplorer.Data;

public class HelpDeskRepository
{
    private readonly HelpDeskDbContext _db;
    public HelpDeskRepository(HelpDeskDbContext db) => _db = db;

    public Task<List<HdTask>> GetTasksAsync(DateTime? since, CancellationToken ct)
    {
        var q = _db.Tasks.AsNoTracking().AsQueryable();
        if (since.HasValue) q = q.Where(t => t.CreatedDate >= since.Value);
        return q.ToListAsync(ct);
    }

    public Task<List<HdTaskDetail>> GetTaskDetailsAsync(IReadOnlyCollection<int> taskIds, CancellationToken ct)
    {
        if (taskIds.Count == 0) return Task.FromResult(new List<HdTaskDetail>());
        return _db.TaskDetails.AsNoTracking()
            .Where(d => taskIds.Contains(d.TaskID))
            .ToListAsync(ct);
    }

    public Task<List<HdTaskCategory>> GetTaskCategoriesAsync(IReadOnlyCollection<int> taskIds, CancellationToken ct)
    {
        if (taskIds.Count == 0) return Task.FromResult(new List<HdTaskCategory>());
        return _db.TaskCategories.AsNoTracking()
            .Where(tc => taskIds.Contains(tc.TaskID))
            .ToListAsync(ct);
    }

    public Task<List<HdCategory>> GetCategoriesAsync(CancellationToken ct) =>
        _db.Categories.AsNoTracking().ToListAsync(ct);

    public Task<List<HdUser>> GetUsersAsync(IReadOnlyCollection<int> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0) return Task.FromResult(new List<HdUser>());
        return _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.UserID))
            .ToListAsync(ct);
    }
}
