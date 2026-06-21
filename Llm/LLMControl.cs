using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Fx.ControlKit.Llm;

public class LLMConfiguration
{
    public string TextCorpusPath { get; set; } = "";
    public int VocabSize { get; set; } = 50257;
    public int ContextLength { get; set; } = 256;
    public int EmbDim { get; set; } = 768;
    public int NHeads { get; set; } = 12;
    public int NLayers { get; set; } = 12;
    public float DropRate { get; set; } = 0.1f;
    public bool QkvBias { get; set; }
    public int DefaultMaxNewTokens { get; set; } = 50;
    public double DefaultTemperature { get; set; } = 1.0;
    public int DefaultTopK { get; set; } = 10;
    public int MaxInputCharacters { get; set; } = 32768;
    public int MaxGeneratedTokens { get; set; } = 512;
    public bool AllowFileSystemOperations { get; set; } = true;
}

public enum LlmOperation
{
    BuildFromJson,
    BuildFromConfiguration,
    GetStatus,
    TokenizeText,
    DecodeTokens,
    ComputeAttention,
    Forward,
    ForwardLastToken,
    GenerateText,
    GenerateChatResponse,
    ClassifyText,
    TrainBpeTokenizer,
    EncodeBpe,
    DecodeBpe,
    TrainSimpleBpeTokenizer,
    EncodeSimpleBpe,
    DecodeSimpleBpe,
    EncodeOpenAiGpt2,
    DecodeOpenAiGpt2,
    DetectSafetensorsConfig,
    LoadSafetensorsWeights,
    SaveSafetensorsWeights,
    RunVerification
}

public class LlmMessage
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
    public LlmOperation Operation { get; set; }
    public string Source { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, JsonElement> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static LlmMessage Create(LlmOperation operation, object? parameters = null)
    {
        var message = new LlmMessage { Operation = operation };
        if (parameters is null)
            return message;

        var element = JsonSerializer.SerializeToElement(parameters, LLMControl.JsonOptions);
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                message.Parameters[property.Name] = property.Value.Clone();
        }
        else
        {
            message.Parameters["value"] = element.Clone();
        }

        return message;
    }
}

public class LlmResponse
{
    public string MessageId { get; set; } = "";
    public LlmOperation Operation { get; set; }
    public bool Succeeded { get; set; }
    public object? Data { get; set; }
    public string Error { get; set; } = "";

    public static LlmResponse Success(LlmMessage message, object? data = null) => new()
    {
        MessageId = message.MessageId,
        Operation = message.Operation,
        Succeeded = true,
        Data = data
    };

    public static LlmResponse Failure(LlmMessage message, Exception ex) => new()
    {
        MessageId = message.MessageId,
        Operation = message.Operation,
        Succeeded = false,
        Error = ex.Message
    };
}

public class LlmStatusResult
{
    public bool IsBuilt { get; set; }
    public int VocabularySize { get; set; }
    public int ContextLength { get; set; }
    public int EmbDim { get; set; }
    public int NHeads { get; set; }
    public int NLayers { get; set; }
    public string[] Operations { get; set; } = Array.Empty<string>();
}

public class LlmTokenizationResult
{
    public List<TokenItem> Tokens { get; set; } = new();
    public List<int> TokenIds { get; set; } = new();
    public string Text { get; set; } = "";
}

public class LlmGenerationResult
{
    public string Prompt { get; set; } = "";
    public string Text { get; set; } = "";
    public List<string> Tokens { get; set; } = new();
    public List<int> TokenIds { get; set; } = new();
    public List<int> NewTokenIds { get; set; } = new();
}

public class LlmChatResult
{
    public string Instruction { get; set; } = "";
    public string Response { get; set; } = "";
}

public class LlmTokenizerTrainingResult
{
    public string Tokenizer { get; set; } = "";
    public int VocabularySize { get; set; }
    public int MergeCount { get; set; }
}

public class LlmTokenIdResult
{
    public List<int> TokenIds { get; set; } = new();
    public string Text { get; set; } = "";
}

public class LlmForwardResult
{
    public int BatchSize { get; set; }
    public int SequenceLength { get; set; }
    public int Width { get; set; }
    public float[][][]? Logits { get; set; }
    public float[][]? LastTokenLogits { get; set; }
}

public class LlmVerificationResult
{
    public string Suite { get; set; } = "";
    public string Output { get; set; } = "";
}

public class LLMControl
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly object InstanceLock = new();
    private static LLMControl? _instance;

    private readonly object _gateLock = new();
    private readonly Dictionary<int, string> _idToToken;
    private BPETokenizer? _bpeTokenizer;
    private BPETokenizerSimple? _simpleBpeTokenizer;

    public static bool IsBuilt
    {
        get
        {
            lock (InstanceLock)
                return _instance is not null;
        }
    }

    public static LLMControl Instance
    {
        get
        {
            lock (InstanceLock)
            {
                if (_instance == null)
                    throw new InvalidOperationException("LLMControl has not been built. Call LLMControl.Build(configJsonPath) first.");
                return _instance;
            }
        }
    }

    public LLMConfiguration Configuration { get; }
    public SimpleTokenizer Tokenizer { get; }
    public GPTModel Model { get; }
    public Dictionary<string, int> Vocabulary { get; }
    public GPTClassifier Classifier { get; }

    private LLMControl(LLMConfiguration config, Dictionary<string, int> vocab)
    {
        Configuration = config ?? throw new ArgumentNullException(nameof(config));
        Vocabulary = vocab ?? throw new ArgumentNullException(nameof(vocab));
        Tokenizer = new SimpleTokenizer(vocab);
        _idToToken = vocab.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

        var gptConfig = new GPTConfig
        {
            VocabSize = vocab.Count,
            ContextLength = config.ContextLength,
            EmbDim = config.EmbDim,
            NHeads = config.NHeads,
            NLayers = config.NLayers,
            DropRate = config.DropRate,
            QkvBias = config.QkvBias
        };

        Model = new GPTModel(gptConfig);
        Classifier = new GPTClassifier(Model, numClasses: 2);
    }

    public static bool TryGetInstance(out LLMControl? control)
    {
        lock (InstanceLock)
        {
            control = _instance;
            return control is not null;
        }
    }

    public static void Reset()
    {
        lock (InstanceLock)
            _instance = null;
    }

    public static LLMControl Build(string configJsonPath)
    {
        if (string.IsNullOrWhiteSpace(configJsonPath))
            throw new ArgumentNullException(nameof(configJsonPath));

        if (!File.Exists(configJsonPath))
            throw new FileNotFoundException($"Configuration file not found at: {configJsonPath}");

        string json = File.ReadAllText(configJsonPath);
        var config = JsonSerializer.Deserialize<LLMConfiguration>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize LLMConfiguration from JSON.");

        string corpusPath = ResolveCorpusPath(config, configJsonPath);
        return Build(config, File.ReadAllText(corpusPath));
    }

    public static LLMControl Build(LLMConfiguration config, string? corpusText = null)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        corpusText ??= ResolveCorpusText(config);
        var vocab = BuildVocabulary(corpusText);
        var control = new LLMControl(config, vocab);

        lock (InstanceLock)
            _instance = control;

        return control;
    }

    public static LlmResponse Send(LlmMessage message)
    {
        if (message is null)
            throw new ArgumentNullException(nameof(message));

        try
        {
            return message.Operation switch
            {
                LlmOperation.BuildFromJson => LlmResponse.Success(message, Build(RequireAnyString(message, "configJsonPath", "path")).GetStatus()),
                LlmOperation.BuildFromConfiguration => LlmResponse.Success(message, BuildFromMessage(message).GetStatus()),
                _ => Instance.Dispatch(message)
            };
        }
        catch (Exception ex)
        {
            return LlmResponse.Failure(message, ex);
        }
    }

    public static Task<LlmResponse> SendAsync(LlmMessage message, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Send(message);
        }, cancellationToken);
    }

    public LlmResponse Dispatch(LlmMessage message)
    {
        if (message is null)
            throw new ArgumentNullException(nameof(message));

        try
        {
            return LlmResponse.Success(message, message.Operation switch
            {
                LlmOperation.GetStatus => GetStatus(),
                LlmOperation.TokenizeText => Tokenize(RequireAnyString(message, "text", "prompt", "message", "value")),
                LlmOperation.DecodeTokens => DecodeTokens(ReadRequiredIntList(message, "ids", "tokenIds", "value")),
                LlmOperation.ComputeAttention => ComputeAttention(
                    ReadRequiredDoubleMatrix(message, "q", "Q"),
                    ReadRequiredDoubleMatrix(message, "k", "K"),
                    ReadRequiredDoubleMatrix(message, "v", "V")),
                LlmOperation.Forward => Forward(ReadRequiredIntMatrix(message, "tokens", "inputIds", "value")),
                LlmOperation.ForwardLastToken => ForwardLastToken(ReadRequiredIntMatrix(message, "tokens", "inputIds", "value")),
                LlmOperation.GenerateText => GenerateTextCompletion(
                    RequireAnyString(message, "prompt", "text", "message", "value"),
                    ReadParameter(message, "temperature", Configuration.DefaultTemperature),
                    ReadParameter(message, "topK", Configuration.DefaultTopK),
                    ReadParameter(message, "maxTokens", Configuration.DefaultMaxNewTokens)),
                LlmOperation.GenerateChatResponse => new LlmChatResult
                {
                    Instruction = RequireAnyString(message, "instruction", "prompt", "text", "message", "value"),
                    Response = GenerateChatResponse(RequireAnyString(message, "instruction", "prompt", "text", "message", "value"))
                },
                LlmOperation.ClassifyText => Classify(RequireAnyString(message, "text", "message", "value")),
                LlmOperation.TrainBpeTokenizer => TrainBpeTokenizer(
                    RequireAnyString(message, "text", "corpus", "value"),
                    ReadParameter(message, "vocabSize", Configuration.VocabSize),
                    ReadStringSet(message, "allowedSpecial")),
                LlmOperation.EncodeBpe => EncodeBpe(RequireAnyString(message, "text", "value"), ReadStringSet(message, "allowedSpecial")),
                LlmOperation.DecodeBpe => DecodeBpe(ReadRequiredIntList(message, "ids", "tokenIds", "value")),
                LlmOperation.TrainSimpleBpeTokenizer => TrainSimpleBpeTokenizer(
                    RequireAnyString(message, "text", "corpus", "value"),
                    ReadParameter(message, "vocabSize", Configuration.VocabSize),
                    ReadStringSet(message, "allowedSpecial")),
                LlmOperation.EncodeSimpleBpe => EncodeSimpleBpe(RequireAnyString(message, "text", "value")),
                LlmOperation.DecodeSimpleBpe => DecodeSimpleBpe(ReadRequiredIntList(message, "ids", "tokenIds", "value")),
                LlmOperation.EncodeOpenAiGpt2 => EncodeOpenAiGpt2(
                    RequireAnyString(message, "text", "value"),
                    RequireAnyString(message, "vocabJsonPath", "vocabPath"),
                    RequireAnyString(message, "bpeMergesPath", "mergesPath")),
                LlmOperation.DecodeOpenAiGpt2 => DecodeOpenAiGpt2(
                    ReadRequiredIntList(message, "ids", "tokenIds", "value"),
                    RequireAnyString(message, "vocabJsonPath", "vocabPath"),
                    RequireAnyString(message, "bpeMergesPath", "mergesPath")),
                LlmOperation.DetectSafetensorsConfig => DetectSafetensorsConfig(RequireAnyString(message, "filePath", "path")),
                LlmOperation.LoadSafetensorsWeights => LoadSafetensorsWeights(RequireAnyString(message, "filePath", "path")),
                LlmOperation.SaveSafetensorsWeights => SaveSafetensorsWeights(
                    RequireAnyString(message, "originalFilePath", "sourcePath"),
                    RequireAnyString(message, "newFilePath", "targetPath")),
                LlmOperation.RunVerification => RunVerification(ReadParameter(message, "suite", "all")),
                LlmOperation.BuildFromJson or LlmOperation.BuildFromConfiguration => throw new InvalidOperationException("Build operations must be sent through LLMControl.Send."),
                _ => throw new NotSupportedException($"Unsupported LLM operation: {message.Operation}.")
            });
        }
        catch (Exception ex)
        {
            return LlmResponse.Failure(message, ex);
        }
    }

    public LlmStatusResult GetStatus() => new()
    {
        IsBuilt = true,
        VocabularySize = Vocabulary.Count,
        ContextLength = Model.Config.ContextLength,
        EmbDim = Model.Config.EmbDim,
        NHeads = Model.Config.NHeads,
        NLayers = Model.Config.NLayers,
        Operations = Enum.GetNames<LlmOperation>()
    };

    public LlmTokenizationResult Tokenize(string text)
    {
        var tokens = TokenizeText(text);
        return new LlmTokenizationResult
        {
            Tokens = tokens,
            TokenIds = tokens.Select(t => t.TokenId).ToList(),
            Text = text ?? ""
        };
    }

    public List<TokenItem> TokenizeText(string text)
    {
        ValidateText(text, nameof(text));
        var result = new List<TokenItem>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var tokenIds = Tokenizer.Encode(text);
        int idx = 1;
        foreach (var id in tokenIds)
        {
            result.Add(new TokenItem
            {
                Index = idx++,
                Token = TokenName(id),
                TokenId = id
            });
        }

        return result;
    }

    public string DecodeTokens(List<int> ids)
    {
        if (ids is null)
            throw new ArgumentNullException(nameof(ids));
        return Tokenizer.Decode(ids);
    }

    public class AttentionResult
    {
        public double[][] Scores { get; set; } = Array.Empty<double[]>();
        public double[][] Weights { get; set; } = Array.Empty<double[]>();
        public double[][] Context { get; set; } = Array.Empty<double[]>();
    }

    public AttentionResult ComputeAttention(double[][] Q, double[][] K, double[][] V)
    {
        ValidateAttentionInputs(Q, K, V);

        int numTokens = Q.Length;
        int d = Q[0].Length;
        double[][] scores = new double[numTokens][];
        for (int i = 0; i < numTokens; i++)
        {
            scores[i] = new double[numTokens];
            for (int j = 0; j < numTokens; j++)
            {
                double sum = 0;
                for (int k = 0; k < d; k++)
                    sum += Q[i][k] * K[j][k];

                scores[i][j] = sum / Math.Sqrt(d);
            }
        }

        float[][] floatScores = new float[numTokens][];
        for (int i = 0; i < numTokens; i++)
        {
            floatScores[i] = new float[numTokens];
            for (int j = 0; j < numTokens; j++)
                floatScores[i][j] = (float)scores[i][j];
        }

        TensorOps.Softmax(floatScores);

        double[][] weights = new double[numTokens][];
        for (int i = 0; i < numTokens; i++)
        {
            weights[i] = new double[numTokens];
            for (int j = 0; j < numTokens; j++)
                weights[i][j] = floatScores[i][j];
        }

        int dv = V[0].Length;
        double[][] context = new double[numTokens][];
        for (int i = 0; i < numTokens; i++)
        {
            context[i] = new double[dv];
            for (int j = 0; j < dv; j++)
            {
                double sum = 0;
                for (int k = 0; k < numTokens; k++)
                    sum += weights[i][k] * V[k][j];

                context[i][j] = sum;
            }
        }

        return new AttentionResult
        {
            Scores = scores,
            Weights = weights,
            Context = context
        };
    }

    public LlmForwardResult Forward(int[][] inputIds)
    {
        ValidateTokenMatrix(inputIds);
        var logits = Model.Forward(inputIds);
        return new LlmForwardResult
        {
            BatchSize = logits.Length,
            SequenceLength = logits.Length == 0 ? 0 : logits[0].Length,
            Width = logits.Length == 0 || logits[0].Length == 0 ? 0 : logits[0][0].Length,
            Logits = logits
        };
    }

    public LlmForwardResult ForwardLastToken(int[][] inputIds)
    {
        ValidateTokenMatrix(inputIds);
        var logits = Model.ForwardLastToken(inputIds);
        return new LlmForwardResult
        {
            BatchSize = logits.Length,
            SequenceLength = 1,
            Width = logits.Length == 0 ? 0 : logits[0].Length,
            LastTokenLogits = logits
        };
    }

    public List<string> GenerateText(string prompt, double temperature, int topK, int maxTokens)
    {
        return GenerateTextCompletion(prompt, temperature, topK, maxTokens).Tokens;
    }

    public LlmGenerationResult GenerateTextCompletion(string prompt, double? temperature = null, int? topK = null, int? maxTokens = null)
    {
        ValidateText(prompt, nameof(prompt));
        var startTokens = EnsureStartTokens(prompt);
        int effectiveMaxTokens = ClampMaxTokens(maxTokens ?? Configuration.DefaultMaxNewTokens);
        var generatedIds = GptGenerator.GenerateText(
            Model,
            startTokens,
            maxNewTokens: effectiveMaxTokens,
            contextSize: Model.Config.ContextLength,
            temperature: temperature ?? Configuration.DefaultTemperature,
            topK: topK ?? Configuration.DefaultTopK,
            eosId: Vocabulary.TryGetValue(SimpleTokenizer.EndOfTextToken, out var eosId) ? eosId : null);

        var newTokens = generatedIds.Skip(startTokens.Count).ToList();
        return new LlmGenerationResult
        {
            Prompt = prompt,
            Text = Tokenizer.Decode(newTokens),
            Tokens = newTokens.Select(TokenName).ToList(),
            TokenIds = generatedIds,
            NewTokenIds = newTokens
        };
    }

    public class ClassifierResult
    {
        public bool IsSpam { get; set; }
        public double SpamProbability { get; set; }
        public List<string> TriggerWords { get; set; } = new();
    }

    public ClassifierResult Classify(string text)
    {
        ValidateText(text, nameof(text));
        if (string.IsNullOrWhiteSpace(text))
            return new ClassifierResult { IsSpam = false, SpamProbability = 0.0 };

        var spamKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "free", "money", "click", "here", "urgent", "prize", "winner", "cash",
            "offer", "claim", "limited", "guaranteed", "investment", "credit", "card"
        };
        var words = text.Split(new[] { ' ', '.', ',', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
        var triggers = words.Where(w => spamKeywords.Contains(w)).Select(w => w.ToLowerInvariant()).Distinct().ToList();

        var tokens = Tokenizer.Encode(text);
        if (tokens.Count == 0)
            tokens.Add(0);

        float[][] logits = Classifier.Forward(new[] { tokens.ToArray() });
        float maxVal = Math.Max(logits[0][0], logits[0][1]);
        float sum = (float)(Math.Exp(logits[0][0] - maxVal) + Math.Exp(logits[0][1] - maxVal));
        float spamProb = (float)Math.Exp(logits[0][1] - maxVal) / sum;

        double score = spamProb;
        if (triggers.Count > 0)
            score = Math.Min(0.99, score * 0.4 + triggers.Count * 0.25);

        return new ClassifierResult
        {
            IsSpam = score >= 0.5,
            SpamProbability = score,
            TriggerWords = triggers
        };
    }

    public string GenerateChatResponse(string instruction)
    {
        ValidateText(instruction, nameof(instruction));
        var entry = new InstructionEntry
        {
            Instruction = instruction,
            Input = ""
        };

        string prompt = PromptFormatter.FormatInput(entry) + "\n\n### Response:\n";
        var startTokens = EnsureStartTokens(prompt);

        var generatedIds = GptGenerator.GenerateText(
            Model,
            startTokens,
            maxNewTokens: ClampMaxTokens(30),
            contextSize: Model.Config.ContextLength,
            temperature: 0.7,
            topK: 5,
            eosId: Vocabulary.TryGetValue(SimpleTokenizer.EndOfTextToken, out var eosId) ? eosId : null);

        var newTokens = generatedIds.Skip(startTokens.Count).ToList();
        return Tokenizer.Decode(newTokens);
    }

    public LlmTokenizerTrainingResult TrainBpeTokenizer(string text, int vocabSize, IEnumerable<string>? allowedSpecial = null)
    {
        ValidateText(text, nameof(text));
        if (vocabSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(vocabSize), "Vocabulary size must be greater than zero.");

        var tokenizer = new BPETokenizer();
        tokenizer.Train(text, vocabSize, ToSpecialSet(allowedSpecial));
        lock (_gateLock)
            _bpeTokenizer = tokenizer;

        return new LlmTokenizerTrainingResult
        {
            Tokenizer = nameof(BPETokenizer),
            VocabularySize = tokenizer.vocab.Count,
            MergeCount = tokenizer.bpe_merges.Count
        };
    }

    public LlmTokenIdResult EncodeBpe(string text, IEnumerable<string>? allowedSpecial = null)
    {
        ValidateText(text, nameof(text));
        var tokenizer = RequireBpeTokenizer();
        return new LlmTokenIdResult
        {
            Text = text,
            TokenIds = tokenizer.Encode(text, ToSpecialSet(allowedSpecial))
        };
    }

    public string DecodeBpe(List<int> tokenIds)
    {
        if (tokenIds is null)
            throw new ArgumentNullException(nameof(tokenIds));
        return RequireBpeTokenizer().Decode(tokenIds);
    }

    public LlmTokenizerTrainingResult TrainSimpleBpeTokenizer(string text, int vocabSize, IEnumerable<string>? allowedSpecial = null)
    {
        ValidateText(text, nameof(text));
        if (vocabSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(vocabSize), "Vocabulary size must be greater than zero.");

        var tokenizer = new BPETokenizerSimple();
        tokenizer.Train(text, vocabSize, ToSpecialSet(allowedSpecial));
        lock (_gateLock)
            _simpleBpeTokenizer = tokenizer;

        return new LlmTokenizerTrainingResult
        {
            Tokenizer = nameof(BPETokenizerSimple),
            VocabularySize = tokenizer.vocab.Count,
            MergeCount = tokenizer.bpe_merges.Count
        };
    }

    public LlmTokenIdResult EncodeSimpleBpe(string text)
    {
        ValidateText(text, nameof(text));
        var tokenizer = RequireSimpleBpeTokenizer();
        return new LlmTokenIdResult
        {
            Text = text,
            TokenIds = tokenizer.Encode(text)
        };
    }

    public string DecodeSimpleBpe(List<int> tokenIds)
    {
        if (tokenIds is null)
            throw new ArgumentNullException(nameof(tokenIds));
        return RequireSimpleBpeTokenizer().Decode(tokenIds);
    }

    public LlmTokenIdResult EncodeOpenAiGpt2(string text, string vocabJsonPath, string bpeMergesPath)
    {
        EnsureFileOperationsAllowed();
        ValidateText(text, nameof(text));
        var encoder = new OpenAiGpt2Encoder(vocabJsonPath, bpeMergesPath);
        return new LlmTokenIdResult
        {
            Text = text,
            TokenIds = encoder.Encode(text)
        };
    }

    public string DecodeOpenAiGpt2(List<int> tokenIds, string vocabJsonPath, string bpeMergesPath)
    {
        EnsureFileOperationsAllowed();
        if (tokenIds is null)
            throw new ArgumentNullException(nameof(tokenIds));
        var encoder = new OpenAiGpt2Encoder(vocabJsonPath, bpeMergesPath);
        return encoder.Decode(tokenIds);
    }

    public GPTConfig DetectSafetensorsConfig(string filePath)
    {
        EnsureFileOperationsAllowed();
        return SafetensorsLoader.DetectGPTConfig(filePath);
    }

    public LlmStatusResult LoadSafetensorsWeights(string filePath)
    {
        EnsureFileOperationsAllowed();
        SafetensorsLoader.LoadModel(Model, filePath);
        return GetStatus();
    }

    public string SaveSafetensorsWeights(string originalFilePath, string newFilePath)
    {
        EnsureFileOperationsAllowed();
        SafetensorsLoader.SaveModel(Model, originalFilePath, newFilePath);
        return newFilePath;
    }

    public LlmVerificationResult RunVerification(string suite = "all")
    {
        suite = string.IsNullOrWhiteSpace(suite) ? "all" : suite.Trim();
        lock (_gateLock)
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();
            try
            {
                Console.SetOut(writer);
                switch (suite.ToLowerInvariant())
                {
                    case "all":
                        VerifyModels.RunVerification();
                        VerifySwa.RunVerification();
                        VerifySafetensors.RunVerification();
                        break;
                    case "models":
                    case "model":
                        VerifyModels.RunVerification();
                        break;
                    case "swa":
                        VerifySwa.RunVerification();
                        break;
                    case "safetensors":
                    case "safetensor":
                        VerifySafetensors.RunVerification();
                        break;
                    default:
                        throw new NotSupportedException($"Unknown verification suite: {suite}.");
                }
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            return new LlmVerificationResult
            {
                Suite = suite,
                Output = writer.ToString()
            };
        }
    }

    private static LLMControl BuildFromMessage(LlmMessage message)
    {
        var config = ReadParameter<LLMConfiguration?>(message, "configuration", null);
        if (config is null)
        {
            var json = JsonSerializer.Serialize(message.Parameters.ToDictionary(kvp => kvp.Key, kvp => kvp.Value), JsonOptions);
            config = JsonSerializer.Deserialize<LLMConfiguration>(json, JsonOptions)
                ?? throw new InvalidOperationException("BuildFromConfiguration requires an LLMConfiguration payload.");
        }

        string? corpusText = ReadParameter<string?>(message, "corpusText", null);
        return Build(config, corpusText);
    }

    private static string ResolveCorpusPath(LLMConfiguration config, string configJsonPath)
    {
        string corpusPath = config.TextCorpusPath;
        if (!Path.IsPathRooted(corpusPath))
        {
            string? configDir = Path.GetDirectoryName(configJsonPath);
            if (!string.IsNullOrWhiteSpace(configDir))
                corpusPath = Path.Combine(configDir, corpusPath);
        }

        if (File.Exists(corpusPath))
            return corpusPath;

        string searchPath = Path.Combine(Directory.GetCurrentDirectory(), "docs", "LLMPython", "ch02", "01_main-chapter-code", "the-verdict.txt");
        if (File.Exists(searchPath))
            return searchPath;

        string fallbackText = "Hello, world! This is a fallback text corpus for testing the C# LLM. C# and DotNet LLM works perfectly from scratch.";
        string tempFallbackPath = Path.Combine(Path.GetTempPath(), "the-verdict-fallback.txt");
        File.WriteAllText(tempFallbackPath, fallbackText);
        return tempFallbackPath;
    }

    private static string ResolveCorpusText(LLMConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.TextCorpusPath) && File.Exists(config.TextCorpusPath))
            return File.ReadAllText(config.TextCorpusPath);

        return "Hello, world! This is a fallback text corpus for testing the C# LLM. C# and DotNet LLM works perfectly from scratch.";
    }

    private static Dictionary<string, int> BuildVocabulary(string corpusText)
    {
        var splitPattern = @"([,.:;?_!""()']|--|\s)";
        var rawTokens = Regex.Split(corpusText ?? "", splitPattern);
        var preprocessed = rawTokens
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        var uniqueWords = preprocessed.Distinct().OrderBy(w => w, StringComparer.Ordinal).ToList();
        var vocab = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int idx = 0;
        foreach (var word in uniqueWords)
            vocab[word] = idx++;

        vocab[SimpleTokenizer.EndOfTextToken] = idx++;
        vocab[SimpleTokenizer.UnkToken] = idx++;

        return vocab;
    }

    private List<int> EnsureStartTokens(string prompt)
    {
        var startTokens = Tokenizer.Encode(prompt);
        if (startTokens.Count == 0)
            startTokens.Add(Vocabulary.TryGetValue(SimpleTokenizer.UnkToken, out var unkId) ? unkId : 0);
        return startTokens;
    }

    private string TokenName(int id)
    {
        return _idToToken.TryGetValue(id, out var token)
            ? token
            : SimpleTokenizer.UnkToken;
    }

    private int ClampMaxTokens(int requested)
    {
        if (requested <= 0)
            return Configuration.DefaultMaxNewTokens;
        return Math.Min(requested, Math.Max(1, Configuration.MaxGeneratedTokens));
    }

    private void ValidateText(string? text, string parameterName)
    {
        if (text is null)
            throw new ArgumentNullException(parameterName);
        if (text.Length > Configuration.MaxInputCharacters)
            throw new ArgumentOutOfRangeException(parameterName, $"Input text exceeds the configured maximum of {Configuration.MaxInputCharacters} characters.");
    }

    private static void ValidateAttentionInputs(double[][] q, double[][] k, double[][] v)
    {
        if (q.Length == 0 || k.Length == 0 || v.Length == 0)
            throw new ArgumentException("Q, K, and V must be non-empty.");
        if (q.Length != k.Length || q.Length != v.Length)
            throw new ArgumentException("Q, K, and V must have the same token count.");
        if (q[0].Length == 0 || k[0].Length == 0 || v[0].Length == 0)
            throw new ArgumentException("Q, K, and V rows must be non-empty.");
        if (q[0].Length != k[0].Length)
            throw new ArgumentException("Q and K must have the same width.");

        EnsureRectangular(q, nameof(q));
        EnsureRectangular(k, nameof(k));
        EnsureRectangular(v, nameof(v));
    }

    private static void EnsureRectangular<T>(T[][] matrix, string name)
    {
        if (matrix.Length == 0)
            throw new ArgumentException($"{name} must be non-empty.", name);
        int width = matrix[0].Length;
        if (width == 0)
            throw new ArgumentException($"{name} rows must be non-empty.", name);
        if (matrix.Any(row => row.Length != width))
            throw new ArgumentException($"{name} must be rectangular.", name);
    }

    private static void ValidateTokenMatrix(int[][] inputIds)
    {
        if (inputIds.Length == 0)
            throw new ArgumentException("Token matrix must be non-empty.", nameof(inputIds));
        EnsureRectangular(inputIds, nameof(inputIds));
    }

    private void EnsureFileOperationsAllowed()
    {
        if (!Configuration.AllowFileSystemOperations)
            throw new InvalidOperationException("File-system LLM operations are disabled by LLMConfiguration.AllowFileSystemOperations.");
    }

    private BPETokenizer RequireBpeTokenizer()
    {
        lock (_gateLock)
            return _bpeTokenizer ?? throw new InvalidOperationException("TrainBpeTokenizer must be called before EncodeBpe or DecodeBpe.");
    }

    private BPETokenizerSimple RequireSimpleBpeTokenizer()
    {
        lock (_gateLock)
            return _simpleBpeTokenizer ?? throw new InvalidOperationException("TrainSimpleBpeTokenizer must be called before EncodeSimpleBpe or DecodeSimpleBpe.");
    }

    private static HashSet<string>? ToSpecialSet(IEnumerable<string>? allowedSpecial)
    {
        return allowedSpecial is null ? null : new HashSet<string>(allowedSpecial, StringComparer.Ordinal);
    }

    private static HashSet<string>? ReadStringSet(LlmMessage message, string name)
    {
        var list = ReadParameter<List<string>?>(message, name, null);
        return list is null ? null : new HashSet<string>(list, StringComparer.Ordinal);
    }

    private static string RequireAnyString(LlmMessage message, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadParameter<string?>(message, name, null);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        throw new ArgumentException($"Message operation {message.Operation} requires one of these string parameters: {string.Join(", ", names)}.");
    }

    private static List<int> ReadRequiredIntList(LlmMessage message, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadParameter<List<int>?>(message, name, null);
            if (value is not null)
                return value;
        }

        throw new ArgumentException($"Message operation {message.Operation} requires an integer list parameter: {string.Join(", ", names)}.");
    }

    private static int[][] ReadRequiredIntMatrix(LlmMessage message, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadParameter<int[][]?>(message, name, null);
            if (value is not null)
                return value;
        }

        throw new ArgumentException($"Message operation {message.Operation} requires an integer matrix parameter: {string.Join(", ", names)}.");
    }

    private static double[][] ReadRequiredDoubleMatrix(LlmMessage message, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadParameter<double[][]?>(message, name, null);
            if (value is not null)
                return value;
        }

        throw new ArgumentException($"Message operation {message.Operation} requires a double matrix parameter: {string.Join(", ", names)}.");
    }

    private static T ReadParameter<T>(LlmMessage message, string name, T defaultValue)
    {
        if (!TryGetParameter(message, name, out var element))
            return defaultValue;

        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return defaultValue;

        return element.Deserialize<T>(JsonOptions) ?? defaultValue;
    }

    private static bool TryGetParameter(LlmMessage message, string name, out JsonElement value)
    {
        if (message.Parameters.TryGetValue(name, out value))
            return true;

        foreach (var pair in message.Parameters)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
