using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Web;

namespace FieldKit.Server.Tests;

/// <summary>
/// Deserializes responses the way a .NET client of this API has to.
/// </summary>
/// <remarks>
/// <para>
/// The default <c>ReadFromJsonAsync&lt;T&gt;()</c> is enough for everything until a response carries
/// <c>Money</c>. That crosses the wire as <c>{ "amount": "12.50", "currency": "EUR" }</c> — a string
/// amount, by <c>BR-PRD-8</c> — which the default deserializer cannot map onto
/// <see cref="SharedKernel.Money"/>. Without the converter it silently produces a default value:
/// amount 0, currency null, and a test that asserts on a price passes for the wrong reason.
/// </para>
/// <para>
/// That is worth a shared helper rather than options declared per test, because it says something
/// about the API rather than about the tests: <b>any .NET consumer needs this converter too.</b> The
/// TypeScript client does not — it reads a string and hands it to <c>decimal.js</c>, which is the
/// point of the format.
/// </para>
/// </remarks>
internal static class WireJson
{
    /// <summary>The options a client of this API needs. Camel-case, plus the money converter.</summary>
    public static readonly JsonSerializerOptions Options = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new MoneyJsonConverter());
        return options;
    }

    /// <summary>Reads a response body with those options.</summary>
    public static async Task<T?> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(Options);

    /// <summary>Gets and reads in one step, for the many tests that only want the body.</summary>
    public static async Task<T?> GetAsync<T>(HttpClient client, string url) =>
        await ReadAsync<T>(await client.GetAsync(url));
}
