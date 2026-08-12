using ContosoDashboard.Data;
using ContosoDashboard.Models;
using Microsoft.EntityFrameworkCore;

namespace ContosoDashboard.Tests.TestHelpers;

/// <summary>
/// Provides an isolated in-memory ApplicationDbContext per test, reusing the app's own
/// ApplicationDbContext.HasData seed (Admin=1, ProjectManager=2, TeamLead=3, Employee=4,
/// Project=1, Tasks=1-3, ProjectMembers), plus one extra "outsider" user who belongs to
/// neither the seeded project nor department, for negative authorization test cases.
/// </summary>
public class DocumentTestFixture : IDisposable
{
    public const int AdminUserId = 1;
    public const int ProjectManagerUserId = 2;
    public const int TeamLeadUserId = 3;
    public const int EmployeeUserId = 4;
    public const int OutsiderUserId = 5;

    public const int ProjectId = 1;
    public const int TaskId = 1;

    public ApplicationDbContext Context { get; }

    public DocumentTestFixture()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        Context = new ApplicationDbContext(options);
        Context.Database.EnsureCreated();

        Context.Users.Add(new User
        {
            UserId = OutsiderUserId,
            Email = "outsider@contoso.com",
            DisplayName = "Outsider User",
            Department = "Sales",
            Role = UserRole.Employee
        });
        Context.SaveChanges();
    }

    public void Dispose()
    {
        Context.Dispose();
    }
}
