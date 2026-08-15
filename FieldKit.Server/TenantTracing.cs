using System.Diagnostics;
using FieldKit.BuildingBlocks;

namespace FieldKit.Server;

/// <summary>
/// Puts the tenant and the subject on the request's own span (<c>observability §4</c>) — W13 slice 2.
/// </summary>
/// <remarks>
/// <para>
/// <b>On the root span rather than on a span of our own.</b> ASP.NET already opens one activity per
/// request and every database call, outbound HTTP call and domain span hangs off it. Stamping that
/// one means "show me everything that happened for this tenant" is a filter rather than a join, and
/// it costs no extra span.
/// </para>
/// <para>
/// <b>After authentication and before the endpoint</b>, because the tenant comes from the validated
/// token and from nowhere else (<see cref="KeycloakTenantContext"/>). Ordering this earlier would
/// read claims that have not been checked yet, which is the vector that whole class exists to close.
/// </para>
/// <para>
/// <b>The subject is here and not on any metric.</b> A rep id is unbounded — one time series per
/// employee, forever — and the doc's own reason for wanting it is to "trace one rep's sync end to
/// end", which is a trace question. <c>Telemetry.Tags</c> marks it span-only.
/// </para>
/// <para>
/// Reading <c>ITenantContext</c> here is safe for an unauthenticated request because it is never
/// reached: the guard below returns first. The context resolves lazily and <b>throws</b> without a
/// principal, so asking unconditionally would turn every anonymous 401 into a 500.
/// </para>
/// </remarks>
public static class TenantTracing
{
    public static IApplicationBuilder UseTenantTracing(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated == true
                && Activity.Current is { } activity
                && context.RequestServices.GetService<ITenantContext>() is { } tenant)
            {
                activity.SetTag(Telemetry.Tags.Tenant, tenant.TenantId.Value.ToString());
                activity.SetTag(Telemetry.Tags.Subject, tenant.UserId);
            }

            await next(context);
        });
}
