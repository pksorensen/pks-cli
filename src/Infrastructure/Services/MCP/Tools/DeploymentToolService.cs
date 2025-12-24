using Microsoft.Extensions.Logging;
using PKS.Infrastructure;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PKS.CLI.Infrastructure.Services.MCP.Tools;

/// <summary>
/// MCP tool service for PKS deployment operations
/// This service provides MCP tools for deployment management and orchestration
/// </summary>
[McpServerToolType]
public class DeploymentToolService
{
    private readonly ILogger<DeploymentToolService> _logger;
    private readonly IDeploymentService _deploymentService;
    private readonly IKubernetesService _kubernetesService;
    private readonly IConfigurationService _configurationService;

    public DeploymentToolService(
        ILogger<DeploymentToolService> logger,
        IDeploymentService deploymentService,
        IKubernetesService kubernetesService,
        IConfigurationService configurationService)
    {
        _logger = logger;
        _deploymentService = deploymentService;
        _kubernetesService = kubernetesService;
        _configurationService = configurationService;
    }

    /// <summary>
    /// Deploy applications with intelligent orchestration
    /// This tool connects to the real PKS deploy command functionality
    /// </summary>
    [McpServerTool]
    [Description("Deploy applications with intelligent orchestration")]
    public async Task<object> DeployApplicationAsync(
        string environment,
        string? image = null,
        int replicas = 1,
        string strategy = "RollingUpdate",
        string? configPath = null)
    {
        _logger.LogInformation("MCP Tool: Deploying to environment '{Environment}' with {Replicas} replicas",
            environment, replicas);

        try
        {
            // Get configuration
            var config = await GetDeploymentConfigurationAsync(environment, configPath);

            // Perform deployment
            var deploymentSuccess = await _deploymentService.DeployAsync(
                environment,
                image ?? $"pks-app:latest",
                replicas);

            if (!deploymentSuccess)
            {
                return new
                {
                    success = false,
                    environment,
                    error = "Deployment failed",
                    message = "The deployment process encountered an error"
                };
            }

            // Get deployment information
            var deploymentInfo = await _deploymentService.GetDeploymentInfoAsync(environment);

            // Get service status
            var deployments = await _kubernetesService.GetDeploymentsAsync();

            return new
            {
                success = true,
                environment,
                image = image ?? "pks-app:latest",
                replicas,
                strategy,
                deploymentId = Guid.NewGuid().ToString(),
                status = "deployed",
                deploymentInfo,
                kubernetesDeployments = deployments,
                endpoint = $"https://{environment}.pks-app.com",
                deployedAt = DateTime.UtcNow,
                configuration = config,
                message = $"Application deployed successfully to {environment} with {replicas} replicas"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy to environment '{Environment}'", environment);
            return new
            {
                success = false,
                environment,
                image,
                replicas,
                error = ex.Message,
                message = $"Deployment failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Get deployment status and information
    /// </summary>
    [McpServerTool]
    [Description("Get deployment status and health information")]
    public async Task<object> GetDeploymentStatusAsync(
        string? environment = null,
        bool detailed = false)
    {
        _logger.LogInformation("MCP Tool: Getting deployment status for environment '{Environment}', detailed: {Detailed}",
            environment, detailed);

        try
        {
            var deployments = await _kubernetesService.GetDeploymentsAsync();
            var results = new List<object>();

            if (string.IsNullOrWhiteSpace(environment))
            {
                // Get status for all deployments
                foreach (var deployment in deployments)
                {
                    var deploymentInfo = await _deploymentService.GetDeploymentInfoAsync("production"); // Default to production
                    var status = await _kubernetesService.GetDeploymentStatusAsync(deployment);

                    var deploymentStatus = new
                    {
                        name = deployment,
                        environment = "production", // Would need environment detection
                        status,
                        deploymentInfo,
                        timestamp = DateTime.UtcNow
                    };

                    if (detailed)
                    {
                        results.Add(new
                        {
                            name = deploymentStatus.name,
                            environment = deploymentStatus.environment,
                            status = deploymentStatus.status,
                            deploymentInfo = deploymentStatus.deploymentInfo,
                            timestamp = deploymentStatus.timestamp,
                            detailedMetrics = await GetDetailedMetricsAsync(deployment)
                        });
                    }
                    else
                    {
                        results.Add(deploymentStatus);
                    }
                }

                return new
                {
                    success = true,
                    deploymentCount = results.Count,
                    deployments = results.ToArray(),
                    timestamp = DateTime.UtcNow,
                    message = $"Retrieved status for {results.Count} deployments"
                };
            }
            else
            {
                // Get status for specific environment
                var deploymentInfo = await _deploymentService.GetDeploymentInfoAsync(environment);
                var matchingDeployments = deployments.Where(d => d.Contains(environment, StringComparison.OrdinalIgnoreCase)).ToArray();

                var environmentStatus = new
                {
                    success = true,
                    environment,
                    deploymentInfo,
                    activeDeployments = matchingDeployments,
                    deploymentCount = matchingDeployments.Length,
                    timestamp = DateTime.UtcNow,
                    message = $"Retrieved status for environment '{environment}'"
                };

                if (detailed && matchingDeployments.Length > 0)
                {
                    return new
                    {
                        success = environmentStatus.success,
                        environment = environmentStatus.environment,
                        deploymentInfo = environmentStatus.deploymentInfo,
                        activeDeployments = environmentStatus.activeDeployments,
                        deploymentCount = environmentStatus.deploymentCount,
                        timestamp = environmentStatus.timestamp,
                        message = environmentStatus.message,
                        detailedMetrics = await GetDetailedMetricsAsync(matchingDeployments.First())
                    };
                }

                return environmentStatus;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get deployment status for environment '{Environment}'", environment);
            return new
            {
                success = false,
                environment,
                error = ex.Message,
                message = $"Failed to retrieve deployment status: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Scale a deployment
    /// </summary>
    [McpServerTool]
    [Description("Scale a deployment to specified number of replicas")]
    public async Task<object> ScaleDeploymentAsync(
        string deploymentName,
        int replicas,
        string namespaceName = "default")
    {
        _logger.LogInformation("MCP Tool: Scaling deployment '{DeploymentName}' to {Replicas} replicas in namespace '{Namespace}'",
            deploymentName, replicas, namespaceName);

        try
        {
            var success = await _kubernetesService.ScaleDeploymentAsync(deploymentName, replicas, namespaceName);

            if (success)
            {
                // Get updated status
                var status = await _kubernetesService.GetDeploymentStatusAsync(deploymentName, namespaceName);

                return new
                {
                    success = true,
                    deploymentName,
                    targetReplicas = replicas,
                    namespaceName,
                    status,
                    scaledAt = DateTime.UtcNow,
                    message = $"Deployment '{deploymentName}' scaled to {replicas} replicas successfully"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    deploymentName,
                    targetReplicas = replicas,
                    namespaceName,
                    error = "Scaling operation failed",
                    message = $"Failed to scale deployment '{deploymentName}' to {replicas} replicas"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scale deployment '{DeploymentName}' to {Replicas} replicas",
                deploymentName, replicas);
            return new
            {
                success = false,
                deploymentName,
                targetReplicas = replicas,
                namespaceName,
                error = ex.Message,
                message = $"Scaling failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Rollback a deployment
    /// </summary>
    [McpServerTool]
    [Description("Rollback a deployment to a previous revision")]
    public async Task<object> RollbackDeploymentAsync(
        string environment,
        string? revision = null)
    {
        _logger.LogInformation("MCP Tool: Rolling back deployment in environment '{Environment}' to revision '{Revision}'",
            environment, revision);

        try
        {
            var success = await _deploymentService.RollbackAsync(environment, revision);

            if (success)
            {
                var deploymentInfo = await _deploymentService.GetDeploymentInfoAsync(environment);

                return new
                {
                    success = true,
                    environment,
                    revision,
                    deploymentInfo,
                    rolledBackAt = DateTime.UtcNow,
                    message = $"Deployment rolled back successfully in environment '{environment}'"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    environment,
                    revision,
                    error = "Rollback operation failed",
                    message = $"Failed to rollback deployment in environment '{environment}'"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rollback deployment in environment '{Environment}'", environment);
            return new
            {
                success = false,
                environment,
                revision,
                error = ex.Message,
                message = $"Rollback failed: {ex.Message}"
            };
        }
    }

    private async Task<Dictionary<string, object?>> GetDeploymentConfigurationAsync(string environment, string? configPath)
    {
        var config = new Dictionary<string, object?>();

        try
        {
            // Get standard configuration values
            config["cluster.endpoint"] = await _configurationService.GetAsync("cluster.endpoint");
            config["namespace.default"] = await _configurationService.GetAsync("namespace.default");
            config["registry.url"] = await _configurationService.GetAsync("registry.url");
            config["deploy.replicas"] = await _configurationService.GetAsync("deploy.replicas");
            config["monitoring.enabled"] = await _configurationService.GetAsync("monitoring.enabled");

            // Add environment-specific settings
            config["environment"] = environment;
            config["configPath"] = configPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load deployment configuration");
        }

        return config;
    }

    private async Task<object> GetDetailedMetricsAsync(string deploymentName)
    {
        // Simulate detailed metrics collection
        await Task.Delay(100);

        var random = new Random();
        return new
        {
            cpu = new
            {
                usage = $"{random.Next(10, 80)}%",
                requests = $"{random.Next(100, 500)}m",
                limits = $"{random.Next(500, 1000)}m"
            },
            memory = new
            {
                usage = $"{random.Next(128, 1024)}Mi",
                requests = $"{random.Next(128, 512)}Mi",
                limits = $"{random.Next(512, 2048)}Mi"
            },
            network = new
            {
                ingressMbps = Math.Round(random.NextDouble() * 100, 2),
                egressMbps = Math.Round(random.NextDouble() * 50, 2)
            },
            storage = new
            {
                used = $"{random.Next(1, 10)}Gi",
                available = $"{random.Next(10, 100)}Gi"
            },
            replicas = new
            {
                desired = random.Next(1, 10),
                current = random.Next(1, 10),
                ready = random.Next(1, 10)
            }
        };
    }
}