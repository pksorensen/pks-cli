namespace PKS.Infrastructure.Services.Oidc;

/// What every interactive grant in here returns — device code, loopback
/// authorization code, and refresh alike. One shape, because the caller's job is
/// the same afterwards in all three cases: store it, or say why not.
///
/// <param name="Error">Null on success. The provider's OAuth error code where
/// there was one, a transport message otherwise.</param>
public sealed record OidcLoginResult(
    string? AccessToken,
    string? RefreshToken,
    string? IdToken,
    long ExpiresAtUnix,
    string? Error)
{
    public bool Ok => Error is null && !string.IsNullOrEmpty(AccessToken);

    public static OidcLoginResult Failed(string error) => new(null, null, null, 0, error);
}
