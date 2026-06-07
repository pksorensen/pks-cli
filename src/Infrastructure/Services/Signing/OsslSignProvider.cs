using System.Diagnostics;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Runner;

namespace PKS.Infrastructure.Services.Signing;

/// <summary>
/// Cross-platform Authenticode/MSIX signing via <c>osslsigncode</c>. Materializes the PKCS#12 to a
/// 0600 temp file for the duration of one sign, shells out, then shreds it. Handles self-signed and
/// imported PFX certs (both hold a local key); cloud providers handle themselves elsewhere.
/// </summary>
public sealed class OsslSignProvider : ISignProvider
{
    private readonly IProcessRunner _processRunner;

    public OsslSignProvider(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public bool CanHandle(CertProvider provider) =>
        provider is CertProvider.SelfSigned or CertProvider.ImportedPfx;

    public async Task<SignResult> SignAsync(CertRecord cert, ICertStore store, SignRequest request, CancellationToken ct = default)
    {
        using var pfx = await store.MaterializePfxAsync(cert.Id, ct);
        return await SignWithPfxAsync(pfx.Path, pfx.Password, request, ct);
    }

    /// <summary>Sign using an already-materialized PKCS#12 (host store path materializes it; the
    /// in-container path fetches it from the runner credential socket). Caller owns the temp file.</summary>
    public async Task<SignResult> SignWithPfxAsync(string pfxPath, string password, SignRequest request, CancellationToken ct = default)
    {
        var tool = LocateOsslsigncode();
        if (tool == null)
            return new SignResult(false,
                "osslsigncode not found on PATH. Install it (Debian/Ubuntu: 'sudo apt-get install -y osslsigncode').");

        if (!File.Exists(request.InputPath))
            return new SignResult(false, $"Input not found: {request.InputPath}");

        var args = BuildOsslArgs(pfxPath, password, request.InputPath, request.OutputPath, request.TimestampUrl);
        var result = await _processRunner.RunAsync(tool, JoinArgs(args), null, ct);

        if (result.ExitCode != 0)
            return new SignResult(false,
                $"osslsigncode failed (exit {result.ExitCode}). {result.StandardError.Trim()}".Trim());

        if (!File.Exists(request.OutputPath))
            return new SignResult(false, "osslsigncode reported success but produced no output file.");

        return new SignResult(true, $"Signed → {request.OutputPath}");
    }

    /// <summary>Build the osslsigncode argv. Pure + testable.</summary>
    public static IReadOnlyList<string> BuildOsslArgs(string pfxPath, string password, string input, string output, string? timestampUrl)
    {
        var args = new List<string>
        {
            "sign",
            "-pkcs12", pfxPath,
            "-pass", password,
            "-h", "sha256",
        };
        if (!string.IsNullOrWhiteSpace(timestampUrl))
        {
            args.Add("-t");
            args.Add(timestampUrl);
        }
        args.Add("-in");
        args.Add(input);
        args.Add("-out");
        args.Add(output);
        return args;
    }

    /// <summary>Quote each arg so paths-with-spaces and base64 passwords survive ProcessStartInfo.Arguments.</summary>
    private static string JoinArgs(IReadOnlyList<string> args) =>
        string.Join(' ', args.Select(Quote));

    private static string Quote(string a) =>
        a.Length > 0 && !a.Contains(' ') && !a.Contains('"') ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    private static string? LocateOsslsigncode()
    {
        // Honor an explicit override first.
        var overridePath = Environment.GetEnvironmentVariable("OSSLSIGNCODE");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;

        var exe = OperatingSystem.IsWindows() ? "osslsigncode.exe" : "osslsigncode";
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* ignore malformed PATH entries */ }
        }
        return null;
    }
}
