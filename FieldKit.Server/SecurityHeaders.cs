namespace FieldKit.Server;

/// <summary>
/// The headers every API response carries (<c>security §6</c>) — W13 slice 7.
/// </summary>
/// <remarks>
/// <para>
/// <b>The front end's proxy sets none of these on <c>/api/</c>, by design.</b> It returns early for
/// API paths — no locale, no nonce, no CSP — because the response is JSON for a <c>fetch</c> rather
/// than a document for a browser to render, and everything it does after that point is about
/// rendering. Correct, and it left the API answering with no security headers at all.
/// </para>
/// <para>
/// <b>An API's headers are not a document's.</b> `frame-ancestors` and a referrer policy are about
/// navigation, which nothing here does. What matters for JSON is the case where a response is
/// treated as something it is not: a browser sniffing a content type, or an endpoint one day
/// returning HTML by accident and having it executed.
/// </para>
/// </remarks>
public static class SecurityHeaders
{
    /// <summary>
    /// The policy an API sends: permit nothing.
    /// </summary>
    /// <remarks>
    /// <c>default-src 'none'</c> is the whole document policy an API needs, and it is not decoration.
    /// If any response here ever renders as HTML — an error page from a proxy, a misconfigured static
    /// handler, an endpoint returning a string that starts with <c>&lt;</c> — this is what stops the
    /// browser executing anything in it. <c>frame-ancestors 'none'</c> rides along because it is the
    /// modern spelling of <c>X-Frame-Options</c> and costs one clause.
    /// </remarks>
    public const string ApiContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

    public static IApplicationBuilder UseApiSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            /*
             * Set before the pipeline runs rather than after.
             *
             * Headers are gone the moment the first byte of the body is written, and an endpoint that
             * streams — or a middleware that short-circuits — has written it before control comes
             * back here. Setting them on the way in is the only placement that covers every response,
             * including the 401 that never reaches an endpoint at all.
             */
            var headers = context.Response.Headers;

            // The one that matters for JSON: stop a browser deciding this is HTML because it looks
            // like it. Everything this API returns declares its type, and `nosniff` is what makes the
            // declaration binding.
            headers["X-Content-Type-Options"] = "nosniff";

            headers["Content-Security-Policy"] = ApiContentSecurityPolicy;

            /*
             * A full URL here names a tenant's data — `/api/outlets/{id}`, `/api/visits/{id}` — and a
             * referrer is one of the few ways that leaves the origin without anybody asking. Little
             * navigates *from* a JSON response, so this is close to free; the front end sets the same
             * value on documents, and one policy across both is one sentence to remember.
             */
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

            /*
             * <b>Every response, not just the authenticated ones.</b> This API is tenant-scoped
             * throughout: what a caller may read depends on the token they sent, so a shared cache
             * keyed on a URL is a cross-tenant read waiting for two people to ask the same question.
             * `no-store` says the response may not be written down at all — stronger than
             * `no-cache`, which permits storing and requires revalidation.
             *
             * Nothing loses by it. Every read here is already cached client-side by TanStack Query
             * against a key that includes the subject, which is a cache that knows whose data it
             * holds — the property an intermediary cannot have.
             */
            headers.CacheControl = "no-store";

            await next(context);
        });
}
