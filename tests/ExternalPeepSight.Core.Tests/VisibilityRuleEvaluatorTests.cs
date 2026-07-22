namespace ExternalPeepSight.Core.Tests;

public sealed class VisibilityRuleEvaluatorTests
{
    public static TheoryData<VisibilityRule, bool, bool, bool> Cases => new()
    {
        { VisibilityRule.SwitchA, false, false, false },
        { VisibilityRule.SwitchA, true, false, true },
        { VisibilityRule.SwitchB, false, true, true },
        { VisibilityRule.SwitchB, true, false, false },
        { VisibilityRule.Both, true, true, true },
        { VisibilityRule.Both, true, false, false },
        { VisibilityRule.Either, false, false, false },
        { VisibilityRule.Either, false, true, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void EvaluateReturnsExpectedVisibility(
        VisibilityRule rule,
        bool switchA,
        bool switchB,
        bool expected)
    {
        bool actual = VisibilityRuleEvaluator.Evaluate(rule, switchA, switchB);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EvaluateRejectsUnknownRule()
    {
        const VisibilityRule invalidRule = (VisibilityRule)int.MaxValue;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => VisibilityRuleEvaluator.Evaluate(invalidRule, false, false));
    }
}

