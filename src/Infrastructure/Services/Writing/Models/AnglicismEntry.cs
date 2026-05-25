namespace PKS.Infrastructure.Services.Writing.Models;

public sealed class AnglicismEntry
{
    public string English { get; set; } = "";
    public List<string> DanishAlternatives { get; set; } = new();
    public string? Note { get; set; }
}

public sealed class ChannelConfig
{
    public string DefaultChannel { get; set; } = "blog";
}

/// One reference writing sample. Filename is used as a stable id and human label.
public sealed class ReferenceSample
{
    public string Id { get; set; } = "";        // file stem (e.g. "post-12")
    public string Content { get; set; } = "";   // raw markdown body
}
