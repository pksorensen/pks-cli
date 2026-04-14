using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.MsGraph;

public class MsGraphEmailExportServiceTests
{
    private readonly Mock<IMsGraphEmailService> _emailServiceMock;
    private readonly Mock<ILogger<MsGraphEmailExportService>> _loggerMock;

    public MsGraphEmailExportServiceTests()
    {
        _emailServiceMock = new Mock<IMsGraphEmailService>();
        _loggerMock = new Mock<ILogger<MsGraphEmailExportService>>();
    }

    private MsGraphEmailExportService CreateService()
    {
        return new MsGraphEmailExportService(
            _emailServiceMock.Object,
            _loggerMock.Object);
    }

    private static MsGraphMessage CreateFullMessage()
    {
        return new MsGraphMessage
        {
            Id = "msg-001",
            Subject = "Re: Weekly Standup Notes",
            From = new MsGraphRecipient
            {
                EmailAddress = new MsGraphEmailAddress { Name = "Jane Doe", Address = "jane@contoso.com" }
            },
            ToRecipients = new List<MsGraphRecipient>
            {
                new() { EmailAddress = new MsGraphEmailAddress { Name = null, Address = "team@contoso.com" } },
                new() { EmailAddress = new MsGraphEmailAddress { Name = "Bob Smith", Address = "bob@contoso.com" } }
            },
            CcRecipients = new List<MsGraphRecipient>
            {
                new() { EmailAddress = new MsGraphEmailAddress { Name = null, Address = "manager@contoso.com" } }
            },
            Body = new MsGraphBody { ContentType = "html", Content = "<p>Hello <b>team</b></p>" },
            ReceivedDateTime = new DateTime(2026, 3, 15, 9, 30, 0, DateTimeKind.Utc),
            SentDateTime = new DateTime(2026, 3, 15, 9, 29, 0, DateTimeKind.Utc),
            HasAttachments = true,
            ConversationId = "AAQkAG123",
            InternetMessageId = "<AAMkAG456>",
            Importance = "normal",
            IsRead = true,
            Categories = new List<string> { "Project Alpha" },
            WebLink = "https://outlook.office365.com/owa/test"
        };
    }

    [Fact]
    public void GenerateMarkdown_ProducesCorrectFrontmatter()
    {
        var service = CreateService();
        var message = CreateFullMessage();

        var markdown = service.GenerateMarkdown(message);

        markdown.Should().StartWith("---");
        markdown.Should().Contain("subject: \"Re: Weekly Standup Notes\"");
        markdown.Should().Contain("from: \"Jane Doe <jane@contoso.com>\"");
        markdown.Should().Contain("to:");
        markdown.Should().Contain("  - \"team@contoso.com\"");
        markdown.Should().Contain("  - \"Bob Smith <bob@contoso.com>\"");
        markdown.Should().Contain("cc:");
        markdown.Should().Contain("  - \"manager@contoso.com\"");
        markdown.Should().Contain("date: 2026-03-15T09:30:00Z");
        markdown.Should().Contain("messageId: \"<AAMkAG456>\"");
        markdown.Should().Contain("conversationId: \"AAQkAG123\"");
        markdown.Should().Contain("importance: normal");
        markdown.Should().Contain("isRead: true");
        markdown.Should().Contain("hasAttachments: true");
        markdown.Should().Contain("categories:");
        markdown.Should().Contain("  - \"Project Alpha\"");
        markdown.Should().Contain("webLink: \"https://outlook.office365.com/owa/test\"");
        markdown.Should().Contain("exported_at:");
    }

    [Fact]
    public void GenerateMarkdown_ConvertsHtmlBodyToMarkdown()
    {
        var service = CreateService();
        var message = new MsGraphMessage
        {
            Id = "msg-002",
            Subject = "HTML Test",
            Body = new MsGraphBody
            {
                ContentType = "html",
                Content = "<p>This is <b>bold</b> and <i>italic</i> text.</p>"
            }
        };

        var markdown = service.GenerateMarkdown(message);

        markdown.Should().Contain("**bold**");
        markdown.Should().NotContain("<b>");
        markdown.Should().NotContain("<p>");
    }

    [Fact]
    public void GenerateMarkdown_HandlesPlainTextBody()
    {
        var service = CreateService();
        var message = new MsGraphMessage
        {
            Id = "msg-003",
            Subject = "Plain Text Test",
            Body = new MsGraphBody
            {
                ContentType = "text",
                Content = "Hello, this is plain text.\nWith a new line."
            }
        };

        var markdown = service.GenerateMarkdown(message);

        markdown.Should().Contain("Hello, this is plain text.\nWith a new line.");
    }

    [Fact]
    public void GenerateMarkdown_ListsAttachmentsInFooter()
    {
        var service = CreateService();
        var message = CreateFullMessage();
        var attachments = new List<MsGraphAttachment>
        {
            new() { Id = "att-1", Name = "meeting-notes.pdf", ContentType = "application/pdf", Size = 250880, ContentBytes = "dGVzdA==" },
            new() { Id = "att-2", Name = "screenshot.png", ContentType = "image/png", Size = 131072, ContentBytes = "dGVzdA==" }
        };

        var markdown = service.GenerateMarkdown(message, attachments);

        markdown.Should().Contain("## Attachments");
        markdown.Should().Contain("[meeting-notes.pdf](attachments/meeting-notes.pdf)");
        markdown.Should().Contain("[screenshot.png](attachments/screenshot.png)");
        markdown.Should().Contain("KB");
    }

    [Fact]
    public void GenerateOutputPath_CreatesCorrectDirectoryStructure()
    {
        var service = CreateService();
        var message = new MsGraphMessage
        {
            Id = "msg-004",
            Subject = "Weekly Standup Notes",
            ReceivedDateTime = new DateTime(2026, 3, 15, 9, 30, 0, DateTimeKind.Utc)
        };

        var path = service.GenerateOutputPath(message, "/tmp/base");

        path.Should().Be(Path.Combine("/tmp/base", "raw", "2026", "03", "15", "093000-weekly-standup-notes", "weekly-standup-notes.md"));
    }

    [Fact]
    public void Slugify_HandlesSpecialCharacters()
    {
        var service = CreateService();

        var result = service.Slugify("Re: Hello World! (2)");

        result.Should().Be("re-hello-world-2");
    }

    [Fact]
    public void Slugify_TruncatesLongSubjects()
    {
        var service = CreateService();
        var longSubject = new string('a', 100);

        var result = service.Slugify(longSubject);

        result.Length.Should().BeLessOrEqualTo(60);
    }

    [Fact]
    public void GenerateMarkdown_HandlesEmptyFields()
    {
        var service = CreateService();
        var message = new MsGraphMessage
        {
            Id = "msg-005",
            Subject = ""
        };

        var action = () => service.GenerateMarkdown(message);

        action.Should().NotThrow();
        var markdown = service.GenerateMarkdown(message);
        markdown.Should().StartWith("---");
        markdown.Should().Contain("subject:");
    }
}
