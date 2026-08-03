namespace FieldKit.Modules.Iam;

/// <summary>
/// The permissions IAM owns, as <c>resource:action</c> strings.
/// </summary>
/// <remarks>
/// Administering roles is itself permission-guarded, and deliberately split from reading them: a
/// back-office screen listing who holds what is a different capability from being able to change it.
/// Collapsing them into one <c>role:admin</c> would mean anyone who can see the roles screen can
/// grant themselves anything on it.
/// </remarks>
public static class IamPermissions
{
    public const string RoleRead = "role:read";
    public const string RoleWrite = "role:write";
    public const string UserRead = "user:read";
    public const string UserWrite = "user:write";
}
