using System.Diagnostics;
using System.IO;
using WinJevlyn.Multimodal.Models;
using LLama;
using LLama.Batched;
using LLama.Common;
using LLama.Native;

namespace WinJevlyn.Multimodal.Services;

public sealed record MultimodalLoadResult(bool UsingGpu, long ElapsedMilliseconds);

public sealed record MultimodalClassificationOutcome(IReadOnlyList<ChoiceResult> Results, long ElapsedMilliseconds);

/// <summary>
/// Multimodal single-forward-pass inference: given an image plus a question and three answer
/// choices, run a single forward pass and read the probability of each choice letter (A/B/C)
/// directly off the next-token logits, restricted (softmax) to just those three candidates - the
/// same approach as InferenceEngine (the text-only main app), extended with an image via
/// LLamaSharp's Mtmd/BatchedExecutor API.
///
/// GPU is used when available and falls back to CPU automatically - see the two-tier fallback in
/// LoadModelAsync.
/// </summary>
public sealed class MultimodalEngine : IDisposable
{
    private static readonly string[] Labels = ["A", "B", "C"];

    private static readonly object NativeConfigGate = new();
    private static bool _nativeConfigured;

    private readonly object _gate = new();
    private LLamaWeights? _weights;
    private MtmdWeights? _clip;
    private BatchedExecutor? _executor;

    public bool IsModelLoaded
    {
        get { lock (_gate) return _executor is not null; }
    }

    public bool IsUsingGpu { get; private set; }

    public Task<MultimodalLoadResult> LoadModelAsync(string textModelPath, string mmprojPath, int threads = 8, uint contextSize = 4096)
    {
        if (string.IsNullOrWhiteSpace(textModelPath) || !File.Exists(textModelPath))
            throw new FileNotFoundException("GGUFモデルファイルが見つかりません。", textModelPath);
        if (string.IsNullOrWhiteSpace(mmprojPath) || !File.Exists(mmprojPath))
            throw new FileNotFoundException("mmprojファイルが見つかりません。", mmprojPath);

        return Task.Run(async () =>
        {
            EnsureNativeLibraryConfigured();

            var stopwatch = Stopwatch.StartNew();

            var (newWeights, modelParams, usingGpu) = await LoadTextWeightsWithFallback(textModelPath, threads, contextSize);

            var mtmdParams = MtmdContextParams.Default();
            mtmdParams.UseGpu = usingGpu;

            MtmdWeights newClip;
            try
            {
                newClip = await MtmdWeights.LoadFromFileAsync(mmprojPath, newWeights, mtmdParams);
            }
            catch
            {
                newWeights.Dispose();
                throw;
            }

            BatchedExecutor newExecutor;
            try
            {
                newExecutor = new BatchedExecutor(newWeights, modelParams, newClip);
            }
            catch
            {
                newClip.Dispose();
                newWeights.Dispose();
                throw;
            }

            stopwatch.Stop();

            lock (_gate)
            {
                _executor?.Dispose(); // also disposes its Context
                _clip?.Dispose();
                _weights?.Dispose();
                _weights = newWeights;
                _clip = newClip;
                _executor = newExecutor;
                IsUsingGpu = usingGpu;
            }

            return new MultimodalLoadResult(usingGpu, stopwatch.ElapsedMilliseconds);
        });
    }

    /// <summary>
    /// Ensures the CUDA-capable native library is selected if a compatible GPU/driver is present,
    /// falling back to the CPU native library otherwise. Must run before any model is loaded, and
    /// only once per process (a selected native library cannot be swapped afterwards).
    /// </summary>
    private static void EnsureNativeLibraryConfigured()
    {
        lock (NativeConfigGate)
        {
            if (_nativeConfigured)
                return;

            NativeLibraryConfig.All.WithCuda(true).WithAutoFallback(true);
            _nativeConfigured = true;
        }
    }

    /// <summary>
    /// Tries to load the text model fully offloaded to GPU first. If that fails (e.g. no CUDA-capable
    /// library was selected, or the GPU is present but doesn't have enough VRAM for this particular
    /// model), retries on CPU using the same already-selected native library.
    /// </summary>
    private static async Task<(LLamaWeights Weights, ModelParams Params, bool UsingGpu)> LoadTextWeightsWithFallback(
        string textModelPath, int threads, uint contextSize)
    {
        try
        {
            var gpuParams = new ModelParams(textModelPath)
            {
                ContextSize = contextSize,
                GpuLayerCount = 99, // offload as many layers as fit; harmless no-op if only a CPU-only native library was selected
                Threads = threads,
                BatchThreads = threads,
                // Restrict to a single GPU instead of letting llama.cpp auto-split layers across
                // multiple cards - on a 2-GPU machine this measured ~46x slower end-to-end (mostly
                // from PCIe sync overhead during vision encoding) than pinning to one GPU.
                SplitMode = GPUSplitMode.None,
                MainGpu = 0,
            };

            var weights = await LLamaWeights.LoadFromFileAsync(gpuParams);
            return (weights, gpuParams, UsingGpu: true);
        }
        catch
        {
            // No CUDA-capable GPU/library, or the GPU is present but this model didn't fit - fall through to CPU.
        }

        var cpuParams = new ModelParams(textModelPath)
        {
            ContextSize = contextSize,
            GpuLayerCount = 0,
            Threads = threads,
            BatchThreads = threads,
        };

        var cpuWeights = await LLamaWeights.LoadFromFileAsync(cpuParams);
        return (cpuWeights, cpuParams, UsingGpu: false);
    }

    public Task<MultimodalClassificationOutcome> ClassifyAsync(string imagePath, string question, string choiceA, string choiceB, string choiceC)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("画像ファイルが見つかりません。", imagePath);

        return Task.Run(() => Classify(imagePath, question, choiceA, choiceB, choiceC));
    }

    private MultimodalClassificationOutcome Classify(string imagePath, string question, string choiceA, string choiceB, string choiceC)
    {
        LLamaWeights weights;
        MtmdWeights clip;
        BatchedExecutor executor;
        lock (_gate)
        {
            if (_weights is null || _clip is null || _executor is null)
                throw new InvalidOperationException("モデルが読み込まれていません。先にGGUFモデルとmmprojを読み込んでください。");
            weights = _weights;
            clip = _clip;
            executor = _executor;
        }

        var stopwatch = Stopwatch.StartNew();

        var texts = new[] { choiceA, choiceB, choiceC };
        var mediaMarker = NativeApi.MtmdDefaultMarker() ?? "<media>";
        var prompt = BuildPrompt(weights, mediaMarker, question, choiceA, choiceB, choiceC);

        // Determine the continuation token for each candidate letter via diff-tokenization (same
        // approach as InferenceEngine, the text-only main app). Safe to do with plain text
        // tokenization even though the full multimodal prompt also contains the media marker: BPE
        // tokenization is local, so retokenizing near "Answer: " doesn't depend on content far
        // earlier in the string, and this avoids having to run the (embed-requiring) multimodal
        // tokenizer just to resolve token ids.
        var baseTokens = executor.Context.Tokenize(prompt, addBos: true, special: true);
        if (baseTokens.Length == 0)
            throw new InvalidOperationException("プロンプトのトークン化に失敗しました。");

        var candidateTokens = new LLamaToken[Labels.Length];
        for (var i = 0; i < Labels.Length; i++)
            candidateTokens[i] = ResolveContinuationToken(executor.Context, prompt, baseTokens, Labels[i]);

        using var embed = clip.LoadMedia(imagePath);
        var conversation = executor.Create();
        try
        {
            conversation.Prompt(prompt, new[] { embed }, addBos: true);

            var decodeResult = executor.Infer().GetAwaiter().GetResult();
            if (decodeResult != DecodeResult.Ok)
                throw new InvalidOperationException($"推論に失敗しました（デコード結果: {decodeResult}）。");

            var logits = conversation.Sample();

            var rawScores = new double[Labels.Length];
            for (var i = 0; i < Labels.Length; i++)
                rawScores[i] = logits[(int)candidateTokens[i]];

            var probabilities = Softmax(rawScores);

            stopwatch.Stop();

            var topIndex = 0;
            for (var i = 1; i < probabilities.Length; i++)
                if (probabilities[i] > probabilities[topIndex])
                    topIndex = i;

            var results = new List<ChoiceResult>(Labels.Length);
            for (var i = 0; i < Labels.Length; i++)
            {
                results.Add(new ChoiceResult
                {
                    Label = Labels[i],
                    Text = texts[i],
                    Probability = probabilities[i] * 100.0,
                    IsTop = i == topIndex,
                });
            }

            return new MultimodalClassificationOutcome(results, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            conversation.Dispose();
        }
    }

    private static string BuildPrompt(LLamaWeights weights, string mediaMarker, string question, string choiceA, string choiceB, string choiceC)
    {
        var body =
            $"{mediaMarker}\n" +
            $"Context: {question}\n" +
            "Question: 画像の内容を踏まえ、最も適切なものを以下の選択肢から1つ選んでください。\n" +
            $"A: {choiceA}\n" +
            $"B: {choiceB}\n" +
            $"C: {choiceC}";

        try
        {
            var template = new LLamaTemplate(weights.NativeHandle, strict: true);
            template.Add("system", "あなたは画像と文脈から最も適切な1つを選ぶ分類器です。回答は選択肢のアルファベット（A、B、Cのいずれか1文字）のみを出力してください。");
            template.Add("user", body);
            template.AddAssistant = true;
            var templated = System.Text.Encoding.UTF8.GetString(template.Apply());
            return templated + "Answer: ";
        }
        catch
        {
            return body + "\nAnswer: ";
        }
    }

    private static LLamaToken ResolveContinuationToken(LLamaContext context, string prompt, LLamaToken[] baseTokens, string letter)
    {
        var extendedTokens = context.Tokenize(prompt + letter, addBos: true, special: true);

        var commonPrefixLength = 0;
        var maxCommon = Math.Min(baseTokens.Length, extendedTokens.Length);
        while (commonPrefixLength < maxCommon && baseTokens[commonPrefixLength] == extendedTokens[commonPrefixLength])
            commonPrefixLength++;

        if (commonPrefixLength >= extendedTokens.Length)
            throw new InvalidOperationException($"選択肢 '{letter}' に対応するトークンを特定できませんでした。");

        return extendedTokens[commonPrefixLength];
    }

    private static double[] Softmax(double[] scores)
    {
        var max = scores.Max();
        var exps = scores.Select(s => Math.Exp(s - max)).ToArray();
        var sum = exps.Sum();
        return exps.Select(e => e / sum).ToArray();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _executor?.Dispose();
            _clip?.Dispose();
            _weights?.Dispose();
            _executor = null;
            _clip = null;
            _weights = null;
        }
    }
}
