namespace Fx.ControlKit.AI;
public sealed record AIPromptCommand(string Key, string Text, string Instruction);
public sealed record AIPromptRequest(string Prompt, AIPromptCommand? Command);
public sealed record AIPromptResult(string Prompt, string Output, string? Command, DateTimeOffset CreatedAt);
