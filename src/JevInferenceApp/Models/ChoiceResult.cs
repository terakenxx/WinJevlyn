namespace JevInferenceApp.Models;

/// <summary>
/// Result of Jev-style logit extraction for a single answer choice (A/B/C).
/// </summary>
public sealed class ChoiceResult
{
    public required string Label { get; init; }

    public required string Text { get; init; }

    /// <summary>Probability in the range 0.0-100.0, from softmax over the candidate choices only.</summary>
    public double Probability { get; init; }

    public bool IsTop { get; init; }
}
