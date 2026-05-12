using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AdefHelpDeskGraphExplorer.Models.HelpDesk;

[Table("ADefHelpDesk_Tasks")]
public class HdTask
{
    [Key] public int TaskID { get; set; }
    public string? Description { get; set; }
    public string? Status { get; set; }
    public string? Priority { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? DueDate { get; set; }
    public int? AssignedRoleID { get; set; }
    public int? RequesterUserID { get; set; }
    public string? RequesterName { get; set; }
    public string? RequesterEmail { get; set; }
}

[Table("ADefHelpDesk_TaskDetails")]
public class HdTaskDetail
{
    [Key] public int DetailID { get; set; }
    public int TaskID { get; set; }
    public string? DetailType { get; set; }
    public DateTime? InsertDate { get; set; }
    public int? UserID { get; set; }
    public string? Description { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? StopTime { get; set; }
}

[Table("ADefHelpDesk_TaskCategories")]
public class HdTaskCategory
{
    [Key] public int ID { get; set; }
    public int TaskID { get; set; }
    public int CategoryID { get; set; }
}

[Table("ADefHelpDesk_Categories")]
public class HdCategory
{
    [Key] public int CategoryID { get; set; }
    public int? ParentCategoryID { get; set; }
    public string? CategoryName { get; set; }
    public int? Level { get; set; }
}

[Table("ADefHelpDesk_Users")]
public class HdUser
{
    [Key] public int UserID { get; set; }
    public string? Username { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public bool? IsSuperUser { get; set; }
}
