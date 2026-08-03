namespace FieldKit.Modules.Org;

/// <summary>
/// The permissions the Organization module owns, as <c>resource:action</c> strings.
/// </summary>
/// <remarks>
/// Read is split from write for the same reason it is everywhere else: seeing the shape of the sales
/// organization is what a supervisor needs to do their job, and redrawing it is not. Territories and
/// rep assignments get their own permissions when they land — reorganizing the hierarchy and moving
/// a rep between territories are different jobs, usually different people.
/// </remarks>
public static class OrgPermissions
{
    public const string OrgUnitRead = "orgunit:read";
    public const string OrgUnitWrite = "orgunit:write";
}
