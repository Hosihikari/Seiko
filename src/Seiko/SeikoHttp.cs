using System.Reflection;

namespace Seiko;

// The client identity every request carries. A server can refuse or stall a
// request without one — minecraft.net drops a plain GET — so both HTTP entry
// points (the source client and the index verifier) read it from here.
internal static class SeikoHttp
{
    public const string UserAgentVariable = "SEIKO_USER_AGENT";

    // `SEIKO_USER_AGENT` replaces the default outright: a deployment behind a
    // gateway may have to present its own name, and bolting the default on
    // would make that impossible. An empty value counts as unset.
    public static string UserAgent =>
        Environment.GetEnvironmentVariable(UserAgentVariable) is { } custom
            && !string.IsNullOrWhiteSpace(custom)
                ? custom
                : DefaultUserAgent;

    private static string DefaultUserAgent
    {
        get
        {
            var version = typeof(SeikoHttp).Assembly
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(version))
            {
                version = typeof(SeikoHttp).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            }

            // The informational version carries the commit after '+'; a user
            // agent that changes on every commit is noise in a server log.
            var plus = version.IndexOf('+', StringComparison.Ordinal);
            if (plus > 0)
            {
                version = version[..plus];
            }

            // Neither a scheme URL nor the word "github" may appear: the
            // Bedrock download host drops a request whose user agent contains
            // either, which is exactly the file this header exists for.
            return $"sk/{version} (+hoshi.srcz.one/seiko)";
        }
    }
}
