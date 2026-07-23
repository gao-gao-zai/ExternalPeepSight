namespace ExternalPeepSight.Core;

/// <summary>
/// Defines how logical switches control overlay visibility.
/// </summary>
public enum VisibilityRule
{
    /// <summary>
    /// Shows the overlay when switch A is enabled.
    /// </summary>
    SwitchA,

    /// <summary>
    /// Shows the overlay when switch B is enabled.
    /// </summary>
    SwitchB,

    /// <summary>
    /// Shows the overlay when both switches are enabled.
    /// </summary>
    Both,

    /// <summary>
    /// Shows the overlay when either switch is enabled.
    /// </summary>
    Either,
}

/// <summary>
/// Evaluates the configured visibility rule against the current switch state.
/// </summary>
public static class VisibilityRuleEvaluator
{
    /// <summary>
    /// Evaluates the configured rule against the current switch state.
    /// </summary>
    /// <param name="rule">The configured visibility rule.</param>
    /// <param name="switchA">Whether switch A is enabled.</param>
    /// <param name="switchB">Whether switch B is enabled.</param>
    /// <returns><see langword="true"/> when the overlay should be visible.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rule is unknown.</exception>
    public static bool Evaluate(VisibilityRule rule, bool switchA, bool switchB) => rule switch
    {
        VisibilityRule.SwitchA => switchA,
        VisibilityRule.SwitchB => switchB,
        VisibilityRule.Both => switchA && switchB,
        VisibilityRule.Either => switchA || switchB,
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown visibility rule."),
    };
}

