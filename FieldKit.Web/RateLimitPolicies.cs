namespace FieldKit.Web;

/// <summary>
/// The names of the rate-limit policies, so a module can ask for one — W13 slice 6.
/// </summary>
/// <remarks>
/// <para>
/// <b>The names live here and the policies live in the host</b>, which is the same split every other
/// cross-cutting web concern in this project takes: <c>Problems</c> defines the refusal envelope and
/// each module chooses when to refuse. A module asks for a budget by name; how wide that budget is,
/// and how it is partitioned, is a deployment question the host answers.
/// </para>
/// <para>
/// Constants rather than literals because the failure is silent in the worst way: ASP.NET throws on
/// an unknown policy name at request time rather than at start-up, so a typo ships and surfaces as a
/// 500 on the one endpoint nobody exercised.
/// </para>
/// </remarks>
public static class RateLimitPolicies
{
    /// <summary>Everything a device does on its own behalf: bind, pull, push, confirm.</summary>
    public const string Sync = "sync";
}
