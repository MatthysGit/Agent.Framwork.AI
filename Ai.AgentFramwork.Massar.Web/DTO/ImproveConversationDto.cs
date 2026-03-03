using System.Text.Json.Serialization;

namespace Ai.AgentFramwork.Massar.Web.DTO;

public sealed record ImproveConversationResultDto(
    [property: JsonPropertyName("thread_summary")] string ThreadSummary,
    [property: JsonPropertyName("key_decisions")] List<ImproveDecisionDto> KeyDecisions,
    [property: JsonPropertyName("action_items")] List<ImproveActionItemDto> ActionItems,
    [property: JsonPropertyName("open_questions")] List<ImproveOpenQuestionDto> OpenQuestions,
    [property: JsonPropertyName("artifacts")] ImproveArtifactsDto Artifacts
);

public sealed record ImproveDecisionDto(
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("context")] string? Context,
    [property: JsonPropertyName("owners")] List<string>? Owners
);

public sealed record ImproveActionItemDto(
    [property: JsonPropertyName("task")] string Task,
    [property: JsonPropertyName("owner")] string? Owner,
    [property: JsonPropertyName("due_date")] string? DueDate,
    [property: JsonPropertyName("priority")] string? Priority,
    [property: JsonPropertyName("status")] string? Status
);

public sealed record ImproveOpenQuestionDto(
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("blocked_action_refs")] List<string>? BlockedActionRefs
);

public sealed record ImproveArtifactsDto(
    [property: JsonPropertyName("email")] ImproveEmailDto Email,
    [property: JsonPropertyName("spec")] ImproveSpecDto Spec
);

public sealed record ImproveEmailDto(
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("to")] List<string> To,
    [property: JsonPropertyName("cc")] List<string> Cc,
    [property: JsonPropertyName("body_markdown")] string BodyMarkdown
);

public sealed record ImproveSpecDto(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("overview")] string Overview,
    [property: JsonPropertyName("goals")] List<string> Goals,
    [property: JsonPropertyName("non_goals")] List<string> NonGoals,
    [property: JsonPropertyName("requirements")] List<ImproveRequirementDto> Requirements,
    [property: JsonPropertyName("acceptance_criteria")] List<string> AcceptanceCriteria,
    [property: JsonPropertyName("risks")] List<string> Risks,
    [property: JsonPropertyName("assumptions")] List<string> Assumptions
);

public sealed record ImproveRequirementDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("priority")] string Priority
);