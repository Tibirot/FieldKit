using System.Reflection;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Org;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;

namespace FieldKit.ArchitectureTests;

/// <summary>
/// What a request record has to say about itself now that the serializer believes it.
/// </summary>
/// <remarks>
/// <para>
/// The host sets <c>RespectRequiredConstructorParameters</c>, which is what turns an omitted
/// <c>permissions</c> array from a <c>NullReferenceException</c> and a 500 into a 400. The cost is
/// that the option is indiscriminate: it makes <i>every</i> parameter without a default required, so
/// <c>Guid? ParentId</c> — nullable, plainly meant to be optional — became mandatory on the wire and
/// three category tests started failing. Under that option a <c>?</c> alone no longer means
/// "optional"; only <c>= null</c> does.
/// </para>
/// <para>
/// So this test exists because the next person to add a nullable field will write the <c>?</c> and
/// stop there, exactly as we did. Nothing else would catch it: the C# tests construct these records
/// positionally and so always pass every argument, and the failure only appears for a caller that
/// omits the field — which is to say, in the browser.
/// </para>
/// </remarks>
public class RequestContractTests
{
    private static readonly Assembly[] Modules =
    [
        typeof(IamModule).Assembly,
        typeof(ConfigurationModule).Assembly,
        typeof(OrgModule).Assembly,
        typeof(OutletsModule).Assembly,
        typeof(ProductsModule).Assembly,
    ];

    /// <summary>
    /// A nullable parameter of anything deserialized from a request body declares <c>= null</c>.
    /// </summary>
    /// <remarks>
    /// The exemption is not a list of blessed types, which would rot the first time someone added to
    /// it. It is the one case the compiler forbids: an optional parameter cannot precede a required
    /// one, so a nullable parameter followed by a required parameter <i>cannot</i> carry a default
    /// and stays mandatory on the wire.
    /// <para>
    /// Nothing exercises that exemption today, deliberately. <c>CreateOutletRequest</c> did —
    /// <c>Segment</c> and <c>Banner</c> sat before <c>TimeZoneId</c> — and rather than exempt them
    /// the record was reordered to put its required parameters first, which is what every other
    /// request record here already does. The exemption stays because the compiler rule is real and
    /// the next record may hit it; it is not a place to put a field to avoid the work.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_nullable_request_parameter_is_optional_on_the_wire()
    {
        var offenders = new List<string>();
        var context = new NullabilityInfoContext();

        foreach (var type in BodyTypes())
        {
            // The primary constructor — the one records deserialize through.
            var constructor = type.GetConstructors()
                .OrderByDescending(candidate => candidate.GetParameters().Length)
                .FirstOrDefault();

            if (constructor is null) continue;

            var parameters = constructor.GetParameters();

            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];

                if (!IsNullable(parameter, context) || parameter.HasDefaultValue) continue;

                // Required parameter later in the list => the compiler would reject `= null` here.
                if (parameters.Skip(i + 1).Any(later => !later.HasDefaultValue)) continue;

                offenders.Add($"{type.Name}.{parameter.Name}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"""
             These nullable parameters have no `= null`, so a caller that omits them gets a 400:
               {string.Join($"{Environment.NewLine}  ", offenders)}
             Add `= null`. A `?` alone no longer makes a field optional — see this test's remarks.
             """);
    }

    /// <summary>
    /// Every type a request body deserializes into: the <c>*Request</c> records and, transitively,
    /// the records nested inside them (<c>Address</c>, <c>OutletContact</c>, <c>BundleRequest</c>).
    /// </summary>
    /// <remarks>
    /// Transitive rather than name-matched because the nested ones are the easier to miss: they are
    /// not called <c>*Request</c>, they are shared with the domain, and <c>Address</c> is four
    /// nullable parameters in a row — the exact shape this rule is about.
    /// </remarks>
    private static IReadOnlyList<Type> BodyTypes()
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>(Modules
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type.Name.EndsWith("Request", StringComparison.Ordinal)));

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!IsRecord(type) || !seen.Add(type)) continue;

            var nested = type.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .SelectMany(parameter => Unwrap(parameter.ParameterType));

            foreach (var candidate in nested) queue.Enqueue(candidate);
        }

        return [.. seen];
    }

    /// <summary>A type and, for a collection or <c>Nullable&lt;T&gt;</c>, what it holds.</summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;

        if (!type.IsGenericType) yield break;

        foreach (var argument in type.GetGenericArguments()) yield return argument;
    }

    private static bool IsRecord(Type type) =>
        type is { IsClass: true, IsAbstract: false }
        && type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public) is not null;

    private static bool IsNullable(ParameterInfo parameter, NullabilityInfoContext context) =>
        Nullable.GetUnderlyingType(parameter.ParameterType) is not null
        || context.Create(parameter).WriteState == NullabilityState.Nullable;
}
