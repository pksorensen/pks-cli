using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services;

public class JiraServiceTests
{
    [Fact]
    public async Task ValidateCredentialsAsync_ShouldUseHttpClientOnce_AndReturnSuccess()
    {
        var handler = new CountingHttpMessageHandler(HttpStatusCode.OK, "{\"self\":\"ok\"}");
        var httpClient = new HttpClient(handler);
        var config = new Mock<IConfigurationService>();
        var logger = new Mock<ILogger<JiraService>>();
        var service = new JiraService(httpClient, config.Object, logger.Object);

        var credentials = new JiraStoredCredentials
        {
            BaseUrl = "https://example.atlassian.net",
            Email = "user@example.com",
            ApiToken = "token",
            AuthMethod = JiraAuthMethod.ApiToken,
            DeploymentType = JiraDeploymentType.Cloud
        };

        var result = await service.ValidateCredentialsAsync(credentials);

        result.Should().BeTrue();
        handler.SendCount.Should().Be(1);
        handler.LastRequestUri.Should().Be("https://example.atlassian.net/rest/api/3/myself");
        handler.LastAuthorizationScheme.Should().Be("Basic");
    }

    [Fact]
    public async Task ValidateCredentialsAsync_ShouldTrimWhitespaceFromCredentials()
    {
        var handler = new CountingHttpMessageHandler(HttpStatusCode.OK, "{\"self\":\"ok\"}");
        var httpClient = new HttpClient(handler);
        var config = new Mock<IConfigurationService>();
        var logger = new Mock<ILogger<JiraService>>();
        var service = new JiraService(httpClient, config.Object, logger.Object);

        var credentials = new JiraStoredCredentials
        {
            BaseUrl = "  https://example.atlassian.net/  ",
            Email = "  user@example.com  ",
            ApiToken = "  token  ",
            AuthMethod = JiraAuthMethod.ApiToken,
            DeploymentType = JiraDeploymentType.Cloud
        };

        var result = await service.ValidateCredentialsAsync(credentials);

        result.Should().BeTrue();
        handler.SendCount.Should().Be(1);
        handler.LastRequestUri.Should().Be("https://example.atlassian.net/rest/api/3/myself");
        handler.LastAuthorizationScheme.Should().Be("Basic");
        handler.LastAuthorizationParameter.Should().NotBeNullOrEmpty();

        var decoded = Encoding.ASCII.GetString(Convert.FromBase64String(handler.LastAuthorizationParameter!));
        decoded.Should().Be("user@example.com:token");
    }

    private sealed class CountingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public int SendCount { get; private set; }
        public string? LastRequestUri { get; private set; }
        public string? LastAuthorizationScheme { get; private set; }
        public string? LastAuthorizationParameter { get; private set; }

        public CountingHttpMessageHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            LastRequestUri = request.RequestUri?.ToString();
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body)
            };

            return Task.FromResult(response);
        }
    }
}