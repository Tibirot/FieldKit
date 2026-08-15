using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// Calls a module contract directly, inside a tenant context that matches a real token.
/// </summary>
/// <remarks>
/// <para>
/// <b>Some contracts have no HTTP route, on purpose.</b> An audit and an order are worked with no
/// signal and arrive through <c>/sync/push</c>; <c>IVisitQuery</c> is a read for a host composition
/// that does not exist yet. Testing any of them means resolving the service from the running
/// server's own container — which means standing up a tenant context by hand.
/// </para>
/// <para>
/// <c>KeycloakTenantContext</c> reads the tenant and the subject off the current request's principal
/// and <b>throws</b> when there is no authenticated one — deliberately, so that a tenant-owned query
/// can never run unscoped. That guard is what makes a plain <c>CreateScope()</c> useless here, and
/// reaching around it with a stub would test a different tenant context from the one the server
/// actually uses.
/// </para>
/// <para>
/// So the principal is rebuilt from the fixture's own token: the claims are the ones the server
/// would have seen had this arrived over HTTP, and every filter, interceptor and scope check runs
/// exactly as it does in production. <c>IHttpContextAccessor</c> stores its context in an
/// <c>AsyncLocal</c>, so setting it here reaches the scope's services and nothing outside this call.
/// </para>
/// <para>
/// <b>This is a file rather than another copy.</b> <c>AuditIngestTests</c> wrote it in W10,
/// <c>OrderIngestTests</c> copied it in W11 and left a note naming the extraction as due at the next
/// caller, and W12 slice 1 was that caller.
/// </para>
/// <para>
/// <b>Correction, W12 slice 2c: there were more copies than that note found.</b> Slice 1 followed
/// <c>OrderIngestTests</c>' pointer, extracted the two files it named, and said so — but five more
/// files carried one, and two of those had already dropped the <c>token</c> parameter. Following a
/// comment is not the same as searching, and the comment was written before four of the copies
/// existed.
/// </para>
/// <para>
/// <b>Paid off after W12, and the search found a sixth.</b> <c>OrderRejectionTests</c>,
/// <c>OrderRepriceTests</c>, <c>PhotoConfirmTests</c>, <c>PricingServiceTests</c> and
/// <c>SyncPullOrderTests</c> now call this file; so does <c>RepScopeTests</c>, which the 2c note
/// missed because its copy was written differently — <c>JwtSecurityTokenHandler</c> for the claims,
/// <c>"Token"</c> for the authentication type, and a <c>RequestServices</c> nothing reads. Searching
/// for the <i>name</i> would have missed it a second time; what found it was searching for what the
/// harness <i>does</i> (<c>IHttpContextAccessor</c>). It keeps a one-line wrapper because it resolves
/// <c>IRepScope</c> for eight call sites — a typed helper over this one, not a copy of it.
/// </para>
/// <para>
/// <b>The two principals were not identical, and the difference was checked rather than assumed.</b>
/// <c>RepScopeTests</c> built its principal from <i>every</i> claim in the token; this file builds it
/// from two. Both answer for the same tenant and subject because those are the only claims
/// <c>KeycloakTenantContext</c> reads — and the file's own "a plain scope has no tenant" test still
/// fails first when the harness is broken, which is what makes that claim checkable rather than
/// hopeful.
/// </para>
/// </remarks>
public static class AsTenant
{
    /// <summary>Runs <paramref name="work"/> in a scope whose tenant context matches the token.</summary>
    public static async Task<T> RunAsync<T>(
        ServerFixture fixture, string token, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = fixture.Services.CreateScope();

        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;

        accessor.HttpContext = new DefaultHttpContext { User = PrincipalOf(token) };

        try
        {
            return await work(scope.ServiceProvider);
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }

    /// <summary>The claims inside a JWT, without validating it — the server already did that.</summary>
    public static ClaimsPrincipal PrincipalOf(string token)
    {
        var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var padded = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

        using var document = JsonDocument.Parse(Convert.FromBase64String(padded));

        // Only the two the tenant context reads. Permissions are not needed: the service is being
        // called directly, so no endpoint filter runs — and adding them would make this look like a
        // test of authorization, which it is not.
        var claims = new List<Claim>
        {
            new("tenant", document.RootElement.GetProperty("tenant").GetString()!),
            new("sub", document.RootElement.GetProperty("sub").GetString()!),
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    /// <summary>Who the token says is asking.</summary>
    public static string SubjectOf(string token) => PrincipalOf(token).FindFirst("sub")!.Value;
}
