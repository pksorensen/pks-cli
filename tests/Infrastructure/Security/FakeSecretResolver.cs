using PKS.Infrastructure.Services.Security;

namespace PKS.CLI.Tests.Security;

/// <summary>
/// The resolver every service under test gets instead of the real <c>SecretStore</c>.
///
/// This type exists because of a defect, not a convenience: the thirteen services that read stored
/// credentials used to take <c>ISecretResolver? secrets = null</c> and fall back to
/// <c>new SecretStore()</c> — the developer's own <c>~/.pks-cli</c>. A test that forgot the argument
/// silently authenticated as whoever was logged in, and an assertion that then compared the token to
/// an expected literal printed the real credential in the failure message. That happened twice, and
/// the second time a live Scaleway secret key ended up in an agent transcript.
///
/// The constructors are now required, so forgetting the resolver is a compile error and this is the
/// thing you reach for. Empty by default: an unseeded key resolves to absent, which is what "no
/// credential configured" should look like in a test.
/// </summary>
public sealed class FakeSecretResolver : ISecretResolver
{
    private readonly Dictionary<string, string> _values;

    public FakeSecretResolver(params (string Key, string Value)[] values)
        => _values = values.ToDictionary(v => v.Key, v => v.Value);

    /// <summary>Nothing is stored — every credential reads as absent.</summary>
    public static FakeSecretResolver Empty => new();

    /// <summary>Seeds one credential; chainable for the handful of tests that need several.</summary>
    public FakeSecretResolver With(string key, string value)
    {
        _values[key] = value;
        return this;
    }

    public Task<string?> RevealAsync(string key)
        => Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

    /// <summary>
    /// Resolves through someone else's lookup — normally a test's <c>IConfigurationService</c> mock,
    /// for the services whose credentials were seeded there before the store existed.
    /// </summary>
    public static ISecretResolver BackedBy(Func<string, Task<string?>> lookup) => new Delegating(lookup);

    private sealed class Delegating(Func<string, Task<string?>> lookup) : ISecretResolver
    {
        public Task<string?> RevealAsync(string key) => lookup(key);
    }
}
