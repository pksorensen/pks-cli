namespace PKS.Infrastructure.Services.Models;

/// <summary>Configuration stored in .confluence/config.json at the project root.</summary>
public class ConfluenceWorkspaceConfig
{
    public string SpaceKey { get; set; } = string.Empty;
    public string? RootPageId { get; set; }
    public string SiteUrl { get; set; } = string.Empty;
    /// <summary>Relative path from config location to the workspace folder (e.g. "docs/confluence").</summary>
    public string WorkDir { get; set; } = "docs/confluence";
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Absolute path to the directory containing <c>.confluence/config.json</c> (the project root).
    /// Populated during <c>LoadWorkspaceConfigAsync</c>, never persisted.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ConfigRoot { get; set; } = string.Empty;
}

/// <summary>YAML frontmatter metadata embedded in each synced markdown file</summary>
public class ConfluenceFrontmatter
{
    public string Id { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Space { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public DateTime LastSynced { get; set; }
}

/// <summary>Confluence page from REST API</summary>
public class ConfluencePage
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Type { get; set; } = "page";
    public string Status { get; set; } = string.Empty;
    public ConfluenceVersion Version { get; set; } = new();
    public ConfluenceBody? Body { get; set; }
    public ConfluenceSpace? Space { get; set; }
    public List<ConfluenceAncestor> Ancestors { get; set; } = new();
    public ConfluenceChildren? Children { get; set; }
}

public class ConfluenceVersion
{
    public int Number { get; set; }
    public bool MinorEdit { get; set; }
    public string? Message { get; set; }
}

public class ConfluenceBody
{
    public ConfluenceStorage? Storage { get; set; }
}

public class ConfluenceStorage
{
    public string Value { get; set; } = string.Empty;
    public string Representation { get; set; } = "storage";
}

public class ConfluenceSpace
{
    public string Key { get; set; } = string.Empty;
    public string? Name { get; set; }
}

public class ConfluenceAncestor
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}

public class ConfluenceChildren
{
    public ConfluencePageResults? Page { get; set; }
}

public class ConfluencePageResults
{
    public List<ConfluencePage> Results { get; set; } = new();
    public int Size { get; set; }
}

/// <summary>Confluence space summary from /wiki/rest/api/space</summary>
public class ConfluenceSpaceInfo
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? HomepageId { get; set; }
}

/// <summary>Page comment (footer or inline), including recursive replies.</summary>
public class ConfluenceComment
{
    public string Id { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public string? AuthorEmail { get; set; }
    public DateTime Created { get; set; }
    public DateTime? Updated { get; set; }
    /// <summary>Confluence storage format HTML of the comment body.</summary>
    public string BodyStorageHtml { get; set; } = string.Empty;
    /// <summary>"inline" or "footer".</summary>
    public string Location { get; set; } = "footer";
    /// <summary>Selected text the inline comment is anchored to (null for footer comments).</summary>
    public string? InlineSelection { get; set; }
    /// <summary>"open", "resolved", "reopened", or "dangling" — or null if not an inline comment.</summary>
    public string? ResolutionStatus { get; set; }
    public List<ConfluenceComment> Replies { get; set; } = new();
}
