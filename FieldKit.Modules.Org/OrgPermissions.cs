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

    /// <summary>
    /// Positions are their own capability, separate from the units they hang on.
    /// </summary>
    /// <remarks>
    /// Redrawing the sales organization and deciding who staffs it are different jobs, usually done
    /// by different people — org design against sales ops. Folding them together would mean anyone
    /// who can move a team can also decide who runs it.
    /// </remarks>
    public const string PositionRead = "position:read";
    public const string PositionWrite = "position:write";

    /// <summary>
    /// Territories again separate, because deciding which outlets a rep covers is a third job.
    /// </summary>
    /// <remarks>
    /// It is the one with the most operational weight: a territory's membership is the rep's offline
    /// data scope (BR-ORG-3) and the input to journey generation, so moving an outlet between
    /// territories changes what somebody's device downloads tomorrow morning.
    /// </remarks>
    public const string TerritoryRead = "territory:read";
    public const string TerritoryWrite = "territory:write";
}
