namespace PKS.CLI.Tests.Services.Security;

/// <summary>A <see cref="TimeProvider"/> whose now is settable, for deterministic TOTP tests.</summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; }
    public MutableTimeProvider(DateTimeOffset now) => Now = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
