using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// Helper class for setting up and working with mock file systems in tests
/// </summary>
public class FileSystemTestHelper
{
    private readonly MockFileSystem _fileSystem;

    public FileSystemTestHelper(MockFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public IFileSystem FileSystem => _fileSystem;

    public void SetupProjectStructure(string projectPath, string projectName, string template = "console")
    {
        _fileSystem.AddDirectory(projectPath);
        
        switch (template.ToLowerInvariant())
        {
            case "console":
                SetupConsoleProject(projectPath, projectName);
                break;
            case "api":
                SetupApiProject(projectPath, projectName);
                break;
            case "web":
                SetupWebProject(projectPath, projectName);
                break;
            case "agent":
                SetupAgentProject(projectPath, projectName);
                break;
            default:
                SetupConsoleProject(projectPath, projectName);
                break;
        }
    }

    private void SetupConsoleProject(string projectPath, string projectName)
    {
        var projectFile = Path.Combine(projectPath, $"{projectName}.csproj");
        _fileSystem.AddFile(projectFile, new MockFileData(GetConsoleProjectContent()));

        var programFile = Path.Combine(projectPath, "Program.cs");
        _fileSystem.AddFile(programFile, new MockFileData(GetConsoleProgramContent()));
    }

    private void SetupApiProject(string projectPath, string projectName)
    {
        var projectFile = Path.Combine(projectPath, $"{projectName}.csproj");
        _fileSystem.AddFile(projectFile, new MockFileData(GetApiProjectContent()));

        var programFile = Path.Combine(projectPath, "Program.cs");
        _fileSystem.AddFile(programFile, new MockFileData(GetApiProgramContent()));

        var controllersPath = Path.Combine(projectPath, "Controllers");
        _fileSystem.AddDirectory(controllersPath);

        var weatherController = Path.Combine(controllersPath, "WeatherForecastController.cs");
        _fileSystem.AddFile(weatherController, new MockFileData(GetWeatherControllerContent()));
    }

    private void SetupWebProject(string projectPath, string projectName)
    {
        SetupApiProject(projectPath, projectName); // Start with API base
        
        var viewsPath = Path.Combine(projectPath, "Views");
        _fileSystem.AddDirectory(viewsPath);
        
        var sharedPath = Path.Combine(viewsPath, "Shared");
        _fileSystem.AddDirectory(sharedPath);
        
        var layoutFile = Path.Combine(sharedPath, "_Layout.cshtml");
        _fileSystem.AddFile(layoutFile, new MockFileData(GetLayoutContent()));
    }

    private void SetupAgentProject(string projectPath, string projectName)
    {
        SetupConsoleProject(projectPath, projectName); // Start with console base
        
        var agentsPath = Path.Combine(projectPath, "Agents");
        _fileSystem.AddDirectory(agentsPath);
        
        var agentFile = Path.Combine(agentsPath, "SampleAgent.cs");
        _fileSystem.AddFile(agentFile, new MockFileData(GetSampleAgentContent()));
        
        var configPath = Path.Combine(projectPath, "appsettings.json");
        _fileSystem.AddFile(configPath, new MockFileData(GetAgentConfigContent()));
    }

    public void SetupTemplateDirectory(string templatePath, Dictionary<string, string> templateFiles)
    {
        _fileSystem.AddDirectory(templatePath);
        
        foreach (var (fileName, content) in templateFiles)
        {
            var filePath = Path.Combine(templatePath, fileName);
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !_fileSystem.Directory.Exists(directory))
            {
                _fileSystem.AddDirectory(directory);
            }
            _fileSystem.AddFile(filePath, new MockFileData(content));
        }
    }

    public void AssertProjectStructureExists(string projectPath, string projectName, string template = "console")
    {
        Assert.True(_fileSystem.Directory.Exists(projectPath), $"Project directory should exist: {projectPath}");
        
        var projectFile = Path.Combine(projectPath, $"{projectName}.csproj");
        Assert.True(_fileSystem.File.Exists(projectFile), $"Project file should exist: {projectFile}");
        
        var programFile = Path.Combine(projectPath, "Program.cs");
        Assert.True(_fileSystem.File.Exists(programFile), $"Program file should exist: {programFile}");
        
        // Template-specific assertions
        switch (template.ToLowerInvariant())
        {
            case "api":
                var controllersPath = Path.Combine(projectPath, "Controllers");
                Assert.True(_fileSystem.Directory.Exists(controllersPath), $"Controllers directory should exist: {controllersPath}");
                break;
            case "web":
                var viewsPath = Path.Combine(projectPath, "Views");
                Assert.True(_fileSystem.Directory.Exists(viewsPath), $"Views directory should exist: {viewsPath}");
                break;
            case "agent":
                var agentsPath = Path.Combine(projectPath, "Agents");
                Assert.True(_fileSystem.Directory.Exists(agentsPath), $"Agents directory should exist: {agentsPath}");
                break;
        }
    }

    private static string GetConsoleProjectContent() => """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net8.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private static string GetApiProjectContent() => """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="8.0.0" />
            <PackageReference Include="Swashbuckle.AspNetCore" Version="6.4.0" />
          </ItemGroup>
        </Project>
        """;

    private static string GetConsoleProgramContent() => """
        // See https://aka.ms/new-console-template for more information
        Console.WriteLine("Hello, World!");
        """;

    private static string GetApiProgramContent() => """
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();
        app.UseAuthorization();
        app.MapControllers();

        app.Run();
        """;

    private static string GetWeatherControllerContent() => """
        using Microsoft.AspNetCore.Mvc;

        namespace Controllers;

        [ApiController]
        [Route("[controller]")]
        public class WeatherForecastController : ControllerBase
        {
            [HttpGet(Name = "GetWeatherForecast")]
            public IEnumerable<WeatherForecast> Get()
            {
                return Enumerable.Range(1, 5).Select(index => new WeatherForecast
                {
                    Date = DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                    TemperatureC = Random.Shared.Next(-20, 55),
                    Summary = new[] { "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching" }[Random.Shared.Next(10)]
                });
            }
        }

        public record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
        {
            public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
        }
        """;

    private static string GetLayoutContent() => """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <title>@ViewData["Title"]</title>
        </head>
        <body>
            <div class="container">
                @RenderBody()
            </div>
        </body>
        </html>
        """;

    private static string GetSampleAgentContent() => """
        namespace Agents;

        public class SampleAgent
        {
            public async Task<string> ProcessAsync(string input)
            {
                // Agent processing logic
                await Task.Delay(100);
                return $"Processed: {input}";
            }
        }
        """;

    private static string GetAgentConfigContent() => """
        {
          "Logging": {
            "LogLevel": {
              "Default": "Information",
              "Microsoft.AspNetCore": "Warning"
            }
          },
          "Agent": {
            "EnableAgentic": true,
            "MaxConcurrentTasks": 5
          }
        }
        """;
}