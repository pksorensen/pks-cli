using FluentAssertions;
using PKS.Infrastructure.Services.TypeSafe;
using Xunit;

namespace PKS.CLI.Tests.Services;

public class TypeSafeCredentialServiceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ParseModels_ReadsDocumentedShape()
    {
        var models = TypeSafeCredentialService.ParseModels(
            "{\"models\":[{\"name\":\"jev-latest\",\"description\":\"stable\",\"release_date\":\"2026-09-15\"},{\"name\":\"jev-preview\"}]}");
        models.Should().NotBeNull();
        models!.Select(m => m.Name).Should().Equal("jev-latest", "jev-preview");
        models[0].ReleaseDate.Should().Be("2026-09-15");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ParseModels_ToleratesBareArrayAndStrings()
    {
        TypeSafeCredentialService.ParseModels("[\"jev-latest\"]")!.Single().Name.Should().Be("jev-latest");
        TypeSafeCredentialService.ParseModels("{\"data\":[{\"id\":\"jev-1.13.0\"}]}")!.Single().Name.Should().Be("jev-1.13.0");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ParseModels_NullOnGarbage()
    {
        TypeSafeCredentialService.ParseModels("<html>").Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ParseRepositoryList_SplitsAndDropsMalformed()
    {
        var repos = TypeSafeCredentialService.ParseRepositoryList(" pksorensen/commuteconnects, KjeldagerIO/app ;junk; a/b/c ,pksorensen/CommuteConnects");
        repos.Should().Equal("pksorensen/commuteconnects", "KjeldagerIO/app");
        TypeSafeCredentialService.ParseRepositoryList(null).Should().BeEmpty();
    }
}
