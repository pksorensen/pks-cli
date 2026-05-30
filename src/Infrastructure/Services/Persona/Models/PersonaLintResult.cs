namespace PKS.Infrastructure.Services.Persona.Models;

public sealed class PersonaLintError
{
    public string Field { get; set; } = "";
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class PersonaLintResult
{
    public string SourcePath { get; set; } = "";
    public bool Ok => Errors.Count == 0;
    public List<PersonaLintError> Errors { get; set; } = new();
    public List<PersonaLintError> Warnings { get; set; } = new();
}
