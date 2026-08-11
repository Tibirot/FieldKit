using System.Text.Json.Serialization;
using FieldKit.Infrastructure;
using FieldKit.Modules.Journey.Contracts;
using FieldKit.Web;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Modules.Journey;

/// <summary>
/// The Journey module: where a rep goes and when (<c>JRN-01…06</c>).
/// </summary>
/// <remarks>
/// <para>
/// Call frequency comes first because everything else in this module is derived from it — the
/// generator turns frequency × territory × calendar into planned visits (<c>JRN-03</c>), and the
/// compliance metric asks whether an outlet got the visits its frequency said it should
/// (<c>BR-JRN-6</c>). It is also the only part a supervisor configures by hand, so it is the part
/// that exists before there is anything to generate.
/// </para>
/// <para>
/// <b>One public contract, and it waited for a caller.</b> <c>IJourneyQuery</c> was specified from
/// W1 and built in W7 slice 9b, when check-in finally had a question for it — three slices after the
/// delivery plan first expected it, because publishing a plan, checking in and checking out all
/// turned out to need nothing from Journey.
/// </para>
/// </remarks>
public sealed class JourneyModule : IModule
{
    public string Name => "Journey";

    public IReadOnlyList<PermissionDefinition> Permissions =>
    [
        new(JourneyPermissions.Read, "View call frequencies and journey plans."),
        new(JourneyPermissions.Write, "Set call frequencies and generate journey plans."),
        new(JourneyPermissions.Annotate, "Report on the round: not-visited reasons, unplanned calls, moves."),
    ];

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("fieldkitdb")
            ?? throw new InvalidOperationException("Connection string 'fieldkitdb' is not configured.");

        services.AddModuleDbContext<JourneyDbContext>(connectionString, JourneyDbContext.SchemaName);
        services.AddHostedService<ModuleMigrator<JourneyDbContext>>();

        /*
         * Days of the week travel as their names — ["Monday","Wednesday"] — never as ordinals.
         *
         * The same rule every other enum on this API follows, and it matters more here than usual:
         * `DayOfWeek`'s ordinals start the week on *Sunday*, so a plan built from numbers would be
         * off by one in a way nobody reading the JSON would question.
         *
         * A converter rather than a `[JsonConverter]` attribute on the property, because the
         * property is a *collection* of enums and the attribute form cannot describe that — it
         * throws at first use, which is how this was found. Registered by the module rather than in
         * the host so the rule travels with the code that needs it.
         */
        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<DayOfWeek>()));

        // Registered against the interface, so a consumer takes the promise and never the class
        // behind it (W7 slice 9b).
        services.AddScoped<IJourneyQuery, JourneyQueries>();

        // Sync reads the rep's round through this rather than the journey schema (W8 slice 8a).
        services.AddScoped<IJourneyChangeFeed, JourneyChangeFeed>();

        // …and pushes the rep's annotations back through this one (W9 slice 9). The pair is the
        // shape the module registry names for every module a device both reads and writes.
        services.AddScoped<IJourneyIngest, JourneyIngestService>();

        // Internal to the module: their only caller is generation, which lives here too.
        services.AddScoped<FrequencyResolver>();
        services.AddScoped<CalendarReader>();
        services.AddScoped<JourneyPlanner>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapFrequencyEndpoints();
        endpoints.MapCalendarEndpoints();
        endpoints.MapPlanEndpoints();
    }
}

/// <summary>
/// The permissions this module owns, as <c>resource:action</c> strings.
/// </summary>
/// <remarks>
/// Constants rather than literals so a rename is a compile error rather than a silently open
/// endpoint. Named after the resource — a journey — rather than after any one screen: the same pair
/// will guard generation and the published plan.
/// </remarks>
public static class JourneyPermissions
{
    public const string Read = "journey:read";
    public const string Write = "journey:write";

    /// <summary>
    /// What a rep may do to the plan they are holding — and nothing else.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Write"/> on purpose. A rep reports on the round they walked; they do
    /// not decide what the round is. Folding the two together to let somebody record a closed shop
    /// would also let them generate and publish plans for anyone, which is the difference between a
    /// permission model and a tier list.
    /// </remarks>
    public const string Annotate = "journey:annotate";
}
