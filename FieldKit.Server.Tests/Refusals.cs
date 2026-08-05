using System.Net.Http.Json;
using FieldKit.Web;

namespace FieldKit.Server.Tests;

/// <summary>
/// Reads what a refused write said, whatever its status.
/// </summary>
/// <remarks>
/// Shared because the envelope is shared: every refusal is <c>{ "errors": [...] }</c>
/// (api-contracts §3), and a second copy of this would be a second thing to update the day it isn't.
/// </remarks>
internal static class Refusals
{
    private sealed record Envelope(IReadOnlyList<FieldProblem> Errors);

    /// <summary>The problems a response carried, or none if it carried none.</summary>
    public static async Task<IReadOnlyList<FieldProblem>> ProblemsOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Envelope>())?.Errors ?? [];
}
