namespace ExternalPeepSight.Core;

/// <summary>
/// Defines how logical switches control overlay visibility.
/// </summary>
public enum VisibilityRule
{
    SwitchA,
    SwitchB,
    Both,
    Either,
}

/// <summary>
/// Evaluates the configured visibility rule against the current switch state.
/// </summary>
public static class VisibilityRuleEvaluator
{
    public static bool Evaluate(VisibilityRule rule, bool switchA, bool switchB) => rule switch
    {
        VisibilityRule.SwitchA => switchA,
        VisibilityRule.SwitchB => switchB,
        VisibilityRule.Both => switchA && switchB,
        VisibilityRule.Either => switchA || switchB,
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown visibility rule."),
    };
}

