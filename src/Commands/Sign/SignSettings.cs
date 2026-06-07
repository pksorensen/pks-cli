using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.Sign;

public class SignSettings : CommandSettings
{
    [CommandArgument(0, "<INPUT>")]
    [Description("Artifact to sign (.msix/.msixbundle/.exe/.dll/.msi)")]
    public string Input { get; set; } = "";

    [CommandOption("-o|--output <OUTPUT>")]
    [Description("Output path (default: <input>-signed<ext>)")]
    public string? Output { get; set; }

    [CommandOption("-c|--cert <CERT>")]
    [Description("Cert id or label (default: the sole pks-held cert)")]
    public string? Cert { get; set; }

    [CommandOption("--timestamp <URL>")]
    [Description("RFC3161/Authenticode timestamp server URL (optional)")]
    public string? Timestamp { get; set; }
}
