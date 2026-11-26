using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Aps.Agent.Agents;

// Structured outputs of the agents. RunAsync<T> turns these records into a JSON schema for the model; the
// [Description] attributes become the schema's field descriptions, so they double as instructions.

public sealed record ExampleProposal(
    [property: Description("Example input copied EXACTLY from a code block of the puzzle (verbatim, no edits).")]
    string Input,
    [property: Description("The answer the puzzle explicitly states for THIS part's question for that input, or null.")]
    [property: JsonPropertyName("expected_output")] string? ExpectedOutput,
    [property: Description("Short note, e.g. if the example uses different parameters than the real input.")]
    string Note);

/// <summary>The Analyst's understanding of the puzzle.</summary>
public sealed record Analysis(
    [property: Description("1-3 sentences restating what must be computed.")] string Summary,
    [property: Description("The approach in plain language, 1-4 sentences.")] string Algorithm,
    [property: Description("Time complexity, e.g. O(n log n).")] string Complexity,
    [property: Description("Worked examples for THIS part.")] IReadOnlyList<ExampleProposal> Examples,
    [property: Description("Parameters that differ between examples and the real input, or empty.")]
    [property: JsonPropertyName("parameter_notes")] string ParameterNotes);

/// <summary>A Coder's program.</summary>
public sealed record ProgramProposal(
    [property: Description("For revisions: one or two sentences on what was wrong and what changed; otherwise empty.")]
    string Diagnosis,
    [property: Description("The approach this code implements, 1-3 sentences.")] string Algorithm,
    [property: Description("Complete Python 3 module defining solve(input_text: str) -> str.")] string Code);

/// <summary>What the Coder hands to the Escalation Coder (bounded, no full conversation).</summary>
public sealed record Handover(
    [property: Description("What was tried, one line per attempt.")] [property: JsonPropertyName("what_was_tried")] string WhatWasTried,
    [property: Description("The failing cases or symptoms.")] [property: JsonPropertyName("failing_cases")] string FailingCases,
    [property: Description("The suspected root cause.")] [property: JsonPropertyName("suspected_cause")] string SuspectedCause,
    [property: Description("Constraints from the puzzle that must hold.")] [property: JsonPropertyName("key_constraints")] string KeyConstraints)
{
    public string ToSummary() =>
        $"Tried: {WhatWasTried}\nFailing: {FailingCases}\nSuspected cause: {SuspectedCause}\nConstraints: {KeyConstraints}";
}
