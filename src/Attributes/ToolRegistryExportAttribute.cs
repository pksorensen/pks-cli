namespace PKS.Attributes;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ToolRegistryExportAttribute : Attribute
{
    public string Slug { get; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] Tags { get; set; } = [];
    public string Status { get; set; } = "stable";
    public string[] Examples { get; set; } = [];
    public string? Usage { get; set; }
    public string? Icon { get; set; }

    public ToolRegistryExportAttribute(string slug) => Slug = slug;
}
