using FieldKit.Modules.Iam.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Iam;

/// <summary>
/// Reads user display information for other modules. Internal — consumers bind to
/// <see cref="IUserDirectory"/> (AT-2).
/// </summary>
internal sealed class UserDirectory(IamDbContext db) : IUserDirectory
{
    public async Task<UserSummary?> FindAsync(string userId, CancellationToken cancellationToken = default) =>
        await Project(db.Users.Where(user => user.SubjectId == userId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<UserSummary>> FindManyAsync(
        IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0) return [];

        return await Project(db.Users.Where(user => userIds.Contains(user.SubjectId)))
            .ToListAsync(cancellationToken);
    }

    // No tenant predicate anywhere in this file: the global query filter supplies it. Writing one by
    // hand would be the beginning of a codebase where some queries have it and some do not.
    private static IQueryable<UserSummary> Project(IQueryable<User> users) =>
        users.Select(user => new UserSummary(
            user.SubjectId, user.DisplayName, user.Email, user.TimeZone, user.IsActive));
}
