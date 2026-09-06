using Microsoft.AspNetCore.Components.Forms;
namespace Fx.ControlKit.AI;
public sealed record PromptSubmission(string Text,IReadOnlyList<IBrowserFile> Attachments);
public sealed record ChatMessage(string Id,string Author,string Text,DateTimeOffset Timestamp,bool IsUser=false,IReadOnlyList<string>? Attachments=null);
public sealed record ChatRequest(PromptSubmission Submission,IReadOnlyList<ChatMessage> History);
public sealed record SmartPasteField(string Name,string Label,Type Type,object? CurrentValue);
public sealed record SmartPasteRequest(string Text,IReadOnlyList<SmartPasteField> Fields);
public sealed record SmartPasteApplied(IReadOnlyDictionary<string,object?> Values);
