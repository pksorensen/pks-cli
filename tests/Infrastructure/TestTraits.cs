using System;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// Test trait categories for organizing and filtering tests
/// </summary>
public static class TestTraits
{
    /// <summary>
    /// Test category trait name
    /// </summary>
    public const string Category = "Category";

    /// <summary>
    /// Test speed trait name
    /// </summary>
    public const string Speed = "Speed";

    /// <summary>
    /// Test reliability trait name
    /// </summary>
    public const string Reliability = "Reliability";
}

/// <summary>
/// Test categories
/// </summary>
public static class TestCategories
{
    public const string Unit = "Unit";
    public const string Integration = "Integration";
    public const string EndToEnd = "EndToEnd";
    public const string Performance = "Performance";
    public const string Smoke = "Smoke";
    public const string Regression = "Regression";
}

/// <summary>
/// Test speed classifications
/// </summary>
public static class TestSpeed
{
    public const string Fast = "Fast";     // < 1 second
    public const string Medium = "Medium"; // 1-10 seconds
    public const string Slow = "Slow";     // > 10 seconds
}

/// <summary>
/// Test reliability classifications
/// </summary>
public static class TestReliability
{
    public const string Stable = "Stable";       // Reliable, no flakiness
    public const string Unstable = "Unstable";   // Known to be flaky
    public const string Experimental = "Experimental"; // New, reliability unknown
}

/// <summary>
/// Attribute for marking unit tests
/// </summary>
[TraitAttribute(TestTraits.Category, TestCategories.Unit)]
public class UnitTestAttribute : Attribute
{
}

/// <summary>
/// Attribute for marking integration tests
/// </summary>
[TraitAttribute(TestTraits.Category, TestCategories.Integration)]
public class IntegrationTestAttribute : Attribute
{
}

/// <summary>
/// Attribute for marking end-to-end tests
/// </summary>
[TraitAttribute(TestTraits.Category, TestCategories.EndToEnd)]
public class EndToEndTestAttribute : Attribute
{
}

/// <summary>
/// Attribute for marking performance tests
/// </summary>
[TraitAttribute(TestTraits.Category, TestCategories.Performance)]
public class PerformanceTestAttribute : Attribute
{
}

/// <summary>
/// Attribute for marking smoke tests
/// </summary>
[TraitAttribute(TestTraits.Category, TestCategories.Smoke)]
public class SmokeTestAttribute : Attribute
{
}

/// <summary>
/// Attribute for marking fast tests
/// </summary>
[TraitAttribute(TestTraits.Speed, TestSpeed.Fast)]
public class FastTestAttribute : Attribute
{
}

/// <summary>
/// Attribute for marking medium speed tests
/// </summary>
[TraitAttribute(TestTraits.Speed, TestSpeed.Medium)]
public class MediumTestAttribute : Attribute
{
}

/// <summary>
/// Attribute for marking slow tests
/// </summary>
[TraitAttribute(TestTraits.Speed, TestSpeed.Slow)]
public class SlowTestAttribute : Attribute
{
}

/// <summary>
/// Attribute for marking stable tests
/// </summary>
[TraitAttribute(TestTraits.Reliability, TestReliability.Stable)]
public class StableTestAttribute : Attribute
{
}

/// <summary>
/// Attribute for marking unstable/flaky tests
/// </summary>
[TraitAttribute(TestTraits.Reliability, TestReliability.Unstable)]
public class UnstableTestAttribute : Attribute
{
}

/// <summary>
/// Attribute for marking experimental tests
/// </summary>
[TraitAttribute(TestTraits.Reliability, TestReliability.Experimental)]
public class ExperimentalTestAttribute : Attribute
{
}