using FluentAssertions;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Unit tests for QueuedWorkflowRunExtensions.GetCoolifyLookupBranch — the helper that
/// chooses between HeadBranch (default) and the PR base ref (for pull_request events) so
/// that Coolify app lookup matches the deploy-target branch rather than the PR source.
/// </summary>
public class QueuedWorkflowRunExtensionsTests
{
    [Fact]
    public void GetCoolifyLookupBranch_WhenPullRequestEvent_WithBaseRef_ReturnsBaseRef()
    {
        // Arrange — release-please PR targeting main
        var run = new QueuedWorkflowRun
        {
            Id = 1,
            Event = "pull_request",
            HeadBranch = "release-please--branches--main",
            PullRequests = new List<PullRequestRef>
            {
                new()
                {
                    Number = 42,
                    Head = new PullRequestRefBranch { Ref = "release-please--branches--main" },
                    Base = new PullRequestRefBranch { Ref = "main" }
                }
            }
        };

        // Act
        var lookupBranch = run.GetCoolifyLookupBranch();

        // Assert — Coolify is bound to git_branch=main, so we must look up "main", not the PR source
        lookupBranch.Should().Be("main");
    }

    [Fact]
    public void GetCoolifyLookupBranch_WhenPushEvent_ReturnsHeadBranch()
    {
        // Arrange — normal push to main (no PR involved)
        var run = new QueuedWorkflowRun
        {
            Id = 2,
            Event = "push",
            HeadBranch = "main",
            PullRequests = new List<PullRequestRef>()
        };

        // Act
        var lookupBranch = run.GetCoolifyLookupBranch();

        // Assert
        lookupBranch.Should().Be("main");
    }

    [Fact]
    public void GetCoolifyLookupBranch_WhenPullRequestEvent_ButNoBaseRef_FallsBackToHeadBranch()
    {
        // Arrange — defensive: pull_request event, but pull_requests array empty (e.g. forked PR
        // where GitHub omits it, or partial deserialization). Falling back avoids a NullRef and
        // keeps the previous behavior.
        var run = new QueuedWorkflowRun
        {
            Id = 3,
            Event = "pull_request",
            HeadBranch = "feature/something",
            PullRequests = new List<PullRequestRef>()
        };

        // Act
        var lookupBranch = run.GetCoolifyLookupBranch();

        // Assert
        lookupBranch.Should().Be("feature/something");
    }

    [Fact]
    public void GetCoolifyLookupBranch_WhenPullRequestEvent_BaseRefEmpty_FallsBackToHeadBranch()
    {
        // Arrange — pull_requests entry exists but base.ref is empty/missing
        var run = new QueuedWorkflowRun
        {
            Id = 4,
            Event = "pull_request",
            HeadBranch = "feature/x",
            PullRequests = new List<PullRequestRef>
            {
                new()
                {
                    Number = 7,
                    Head = new PullRequestRefBranch { Ref = "feature/x" },
                    Base = new PullRequestRefBranch { Ref = "" }
                }
            }
        };

        // Act
        var lookupBranch = run.GetCoolifyLookupBranch();

        // Assert
        lookupBranch.Should().Be("feature/x");
    }

    [Fact]
    public void GetCoolifyLookupBranch_WhenEventEmpty_ReturnsHeadBranch()
    {
        // Arrange — older API responses or partial fixtures may omit "event"
        var run = new QueuedWorkflowRun
        {
            Id = 5,
            Event = "",
            HeadBranch = "main"
        };

        // Act
        var lookupBranch = run.GetCoolifyLookupBranch();

        // Assert
        lookupBranch.Should().Be("main");
    }
}
