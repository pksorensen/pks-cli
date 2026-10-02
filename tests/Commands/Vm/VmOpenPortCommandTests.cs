using FluentAssertions;
using PKS.Commands.Vm;
using Xunit;

namespace PKS.CLI.Tests.Commands.Vm;

public class VmOpenPortCommandTests
{
    [Theory]
    [InlineData("80,443", new[] { "80", "443" })]
    [InlineData(" 80 , 443 ,80", new[] { "80", "443" })]
    [InlineData("8000-8010", new[] { "8000-8010" })]
    public void ParsePorts_accepts_ports_and_ranges(string spec, string[] expected)
    {
        VmOpenPortCommand.ParsePorts(spec).Should().Equal(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("443-80")]
    [InlineData("http")]
    [InlineData("*")]
    public void ParsePorts_rejects_invalid_specs(string spec)
    {
        VmOpenPortCommand.ParsePorts(spec).Should().BeNull();
    }
}
