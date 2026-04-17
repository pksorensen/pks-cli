using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.MsGraph;

public class MsGraphEmailServiceTests
{
    private readonly Mock<IMsGraphAuthenticationService> _authServiceMock;
    private readonly Mock<HttpMessageHandler> _httpHandlerMock;
    private readonly HttpClient _httpClient;
    private readonly Mock<ILogger<MsGraphEmailService>> _loggerMock;
    private readonly MsGraphAuthConfig _config;
    private readonly JsonSerializerOptions _jsonOptions;

    public MsGraphEmailServiceTests()
    {
        _authServiceMock = new Mock<IMsGraphAuthenticationService>();
        _httpHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpHandlerMock.Object);
        _loggerMock = new Mock<ILogger<MsGraphEmailService>>();
        _config = new MsGraphAuthConfig();

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // Default: return a valid token
        _authServiceMock
            .Setup(x => x.GetValidAccessTokenAsync())
            .ReturnsAsync("test-token");
    }

    private MsGraphEmailService CreateService()
    {
        return new MsGraphEmailService(
            _httpClient,
            _authServiceMock.Object,
            _loggerMock.Object,
            _config);
    }

    private void SetupHttpResponse(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private void SetupHttpResponseSequence(params string[] responseJsons)
    {
        var sequence = _httpHandlerMock
            .Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());

        foreach (var json in responseJsons)
        {
            sequence.ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task GetMessages_ReturnsList_OnSuccess()
    {
        // Arrange
        var service = CreateService();
        var response = new MsGraphMessageListResponse
        {
            Value = new List<MsGraphMessage>
            {
                new() { Id = "msg-1", Subject = "Hello World", Importance = "high", IsRead = false },
                new() { Id = "msg-2", Subject = "Test Email", Importance = "normal", IsRead = true }
            },
            ODataNextLink = null
        };

        SetupHttpResponse(JsonSerializer.Serialize(response, _jsonOptions));

        var query = new MsGraphEmailQuery { Folder = "inbox" };

        // Act
        var result = await service.GetMessagesAsync(query);

        // Assert
        result.Should().HaveCount(2);
        result[0].Id.Should().Be("msg-1");
        result[0].Subject.Should().Be("Hello World");
        result[0].Importance.Should().Be("high");
        result[1].Id.Should().Be("msg-2");
        result[1].Subject.Should().Be("Test Email");
        result[1].IsRead.Should().BeTrue();
    }

    [Fact]
    public async Task GetMessages_HandlesPagination_WithOdataNextLink()
    {
        // Arrange
        var service = CreateService();

        var page1 = new MsGraphMessageListResponse
        {
            Value = new List<MsGraphMessage>
            {
                new() { Id = "msg-1", Subject = "Page 1 Message" }
            },
            ODataNextLink = "https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages?$skip=1"
        };

        var page2 = new MsGraphMessageListResponse
        {
            Value = new List<MsGraphMessage>
            {
                new() { Id = "msg-2", Subject = "Page 2 Message" }
            },
            ODataNextLink = null
        };

        SetupHttpResponseSequence(
            JsonSerializer.Serialize(page1, _jsonOptions),
            JsonSerializer.Serialize(page2, _jsonOptions));

        var query = new MsGraphEmailQuery { Folder = "inbox" };

        // Act
        var result = await service.GetMessagesAsync(query);

        // Assert
        result.Should().HaveCount(2);
        result[0].Id.Should().Be("msg-1");
        result[0].Subject.Should().Be("Page 1 Message");
        result[1].Id.Should().Be("msg-2");
        result[1].Subject.Should().Be("Page 2 Message");

        _httpHandlerMock
            .Protected()
            .Verify(
                "SendAsync",
                Times.Exactly(2),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetMessages_AppliesDateFilter()
    {
        // Arrange
        var service = CreateService();
        var response = new MsGraphMessageListResponse
        {
            Value = new List<MsGraphMessage>(),
            ODataNextLink = null
        };

        HttpRequestMessage? capturedRequest = null;
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(response, _jsonOptions), System.Text.Encoding.UTF8, "application/json")
            });

        var afterDate = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var query = new MsGraphEmailQuery
        {
            Folder = "inbox",
            After = afterDate
        };

        // Act
        await service.GetMessagesAsync(query);

        // Assert
        capturedRequest.Should().NotBeNull();
        var requestUrl = capturedRequest!.RequestUri!.ToString();
        requestUrl.Should().Contain("$filter=receivedDateTime ge 2025-01-15T00:00:00Z");
    }

    [Fact]
    public async Task GetMessages_RespectsMaxMessages()
    {
        // Arrange
        var service = CreateService();

        var response = new MsGraphMessageListResponse
        {
            Value = new List<MsGraphMessage>
            {
                new() { Id = "msg-1", Subject = "First" },
                new() { Id = "msg-2", Subject = "Second" }
            },
            ODataNextLink = "https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages?$skip=2"
        };

        SetupHttpResponse(JsonSerializer.Serialize(response, _jsonOptions));

        var query = new MsGraphEmailQuery
        {
            Folder = "inbox",
            MaxMessages = 1
        };

        // Act
        var result = await service.GetMessagesAsync(query);

        // Assert
        result.Should().HaveCount(1);
        result[0].Id.Should().Be("msg-1");
    }

    [Fact]
    public async Task GetAttachments_ReturnsList_OnSuccess()
    {
        // Arrange
        var service = CreateService();

        var attachments = new List<MsGraphAttachment>
        {
            new()
            {
                Id = "att-1",
                Name = "document.pdf",
                ContentType = "application/pdf",
                Size = 12345,
                ContentBytes = "dGVzdA==",
                IsInline = false
            }
        };

        var responseJson = JsonSerializer.Serialize(new { value = attachments }, _jsonOptions);
        SetupHttpResponse(responseJson);

        // Act
        var result = await service.GetAttachmentsAsync("msg-1");

        // Assert
        result.Should().HaveCount(1);
        result[0].Id.Should().Be("att-1");
        result[0].Name.Should().Be("document.pdf");
        result[0].ContentType.Should().Be("application/pdf");
        result[0].Size.Should().Be(12345);
        result[0].ContentBytes.Should().Be("dGVzdA==");
        result[0].IsInline.Should().BeFalse();
    }

    [Fact]
    public async Task GetMessages_ThrowsWhenNotAuthenticated()
    {
        // Arrange
        _authServiceMock
            .Setup(x => x.GetValidAccessTokenAsync())
            .ReturnsAsync((string?)null);

        var service = CreateService();
        var query = new MsGraphEmailQuery { Folder = "inbox" };

        // Act
        var act = () => service.GetMessagesAsync(query);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Not authenticated*");
    }
}
