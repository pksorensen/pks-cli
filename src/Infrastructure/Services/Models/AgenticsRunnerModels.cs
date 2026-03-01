namespace PKS.Infrastructure.Services.Models;

public class AgenticsRunnerRegistration
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Token { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Project { get; set; } = "";
    public string Server { get; set; } = "";
    public DateTime RegisteredAt { get; set; }
}

public class AgenticsRunnerConfiguration
{
    public List<AgenticsRunnerRegistration> Registrations { get; set; } = new();
    public DateTime? LastModified { get; set; }
}
