using System;
using System.Collections.Generic;
using System.Linq;

namespace Fx.ControlKit.Llm;

public class InstructionEntry
{
    public string Instruction { get; set; } = "";
    public string Input { get; set; } = "";
    public string Output { get; set; } = "";
}

public class PromptFormatter
{
    public static string FormatInput(InstructionEntry entry)
    {
        string instructionText = 
            "Below is an instruction that describes a task. " +
            "Write a response that appropriately completes the request.\n\n" +
            $"### Instruction:\n{entry.Instruction}";

        string inputText = !string.IsNullOrEmpty(entry.Input) 
            ? $"\n\n### Input:\n{entry.Input}" 
            : "";

        return instructionText + inputText;
    }
}

public class InstructionDataset
{
    public List<int[]> EncodedTexts { get; } = new();
    public int MaxLength { get; }

    public InstructionDataset(List<InstructionEntry> data, SimpleTokenizer tokenizer, int? allowedMaxLength = null, int padTokenId = 50256)
    {
        var rawEncoded = new List<int[]>();
        foreach (var entry in data)
        {
            string prompt = PromptFormatter.FormatInput(entry);
            string response = $"\n\n### Response:\n{entry.Output}";
            string fullText = prompt + response;
            
            var tokens = tokenizer.Encode(fullText);
            tokens.Add(padTokenId);
            
            rawEncoded.Add(tokens.ToArray());
        }

        MaxLength = rawEncoded.Count > 0 ? rawEncoded.Max(t => t.Length) : 0;
        if (allowedMaxLength.HasValue && MaxLength > allowedMaxLength.Value)
        {
            MaxLength = allowedMaxLength.Value;
        }

        foreach (var seq in rawEncoded)
        {
            var padded = new int[MaxLength];
            if (seq.Length >= MaxLength)
            {
                Array.Copy(seq, padded, MaxLength);
            }
            else
            {
                Array.Copy(seq, padded, seq.Length);
                for (int p = seq.Length; p < MaxLength; p++)
                {
                    padded[p] = padTokenId;
                }
            }
            EncodedTexts.Add(padded);
        }
    }
}
