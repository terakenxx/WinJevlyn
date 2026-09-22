using System.Diagnostics;
using System.IO;
using JevInferenceApp.Models;
using LLama;
using LLama.Common;
using LLama.Native;

namespace JevInferenceApp.Services;

public sealed record InferenceOutcome(IReadOnlyList<ChoiceResult> Results, long ElapsedMilliseconds);

/// <summary>
/// Jev-style inference: instead of autoregressively generating text, run a single forward pass
/// and read the probability of each candidate answer letter (A/B/C) directly off the next-token
/// logits, restricted (softmax) to just those three candidates.
/// </summary>
public sealed class JevInferenceEngine : IDisposable
{
    private static readonly string[] Labels = ["A", "B", "C"];

    private readonly object _gate = new();
    private LLamaWeights? _weights;
    private LLamaContext? _context;

    public bool IsModelLoaded
    {
        get { lock (_gate) return _context is not null; }
    }

    public Task LoadModelAsync(string ggufPath, int threads = 8, uint contextSize = 4096)
    {
        if (string.IsNullOrWhiteSpace(ggufPath) || !File.Exists(ggufPath))
            throw new FileNotFoundException("GGUFモデルファイルが見つかりません。", ggufPath);

        return Task.Run(() =>
        {
            var modelParams = new ModelParams(ggufPath)
            {
                ContextSize = contextSize,
                GpuLayerCount = 0, // CPU only
                Threads = threads,
                BatchThreads = threads,
            };

            var newWeights = LLamaWeights.LoadFromFile(modelParams);
            LLamaContext newContext;
            try
            {
                newContext = newWeights.CreateContext(modelParams);
            }
            catch
            {
                newWeights.Dispose();
                throw;
            }

            lock (_gate)
            {
                _context?.Dispose();
                _weights?.Dispose();
                _weights = newWeights;
                _context = newContext;
            }
        });
    }

    public Task<InferenceOutcome> InferAsync(string contextText, string choiceA, string choiceB, string choiceC)
    {
        return Task.Run(() => Infer(contextText, choiceA, choiceB, choiceC));
    }

    private InferenceOutcome Infer(string contextText, string choiceA, string choiceB, string choiceC)
    {
        lock (_gate)
        {
            if (_weights is null || _context is null)
                throw new InvalidOperationException("モデルが読み込まれていません。先にGGUFモデルを読み込んでください。");

            var stopwatch = Stopwatch.StartNew();

            // Clear KV cache so each inference starts from a clean context.
            _context.NativeHandle.MemoryClear(true);

            var texts = new[] { choiceA, choiceB, choiceC };
            var prompt = BuildPrompt(contextText, choiceA, choiceB, choiceC);

            var baseTokens = _context.Tokenize(prompt, addBos: true, special: true);
            if (baseTokens.Length == 0)
                throw new InvalidOperationException("プロンプトのトークン化に失敗しました。");

            var candidateTokens = new LLamaToken[Labels.Length];
            for (var i = 0; i < Labels.Length; i++)
                candidateTokens[i] = ResolveContinuationToken(prompt, baseTokens, Labels[i]);

            var batch = new LLamaBatch();
            var logitIndex = batch.AddRange(baseTokens, 0, LLamaSeqId.Zero, true);

            var decodeResult = _context.Decode(batch);
            if (decodeResult != DecodeResult.Ok)
                throw new InvalidOperationException($"推論に失敗しました（デコード結果: {decodeResult}）。");

            var logits = _context.NativeHandle.GetLogitsIth(logitIndex);

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

            return new InferenceOutcome(results, stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Builds the chat-templated prompt. Falls back to a plain-text prompt if the GGUF has no
    /// usable embedded chat template (e.g. a base, non-instruct conversion).
    /// </summary>
    private string BuildPrompt(string contextText, string choiceA, string choiceB, string choiceC)
    {
        var body =
            $"Context: {contextText}\n" +
            "Question: 最も適切なものを以下の選択肢から1つ選んでください。\n" +
            $"A: {choiceA}\n" +
            $"B: {choiceB}\n" +
            $"C: {choiceC}";

        try
        {
            var template = new LLamaTemplate(_weights!, strict: true);
            template.Add("system", "あなたは与えられた文脈と選択肢から最も適切な1つを選ぶ分類器です。回答は選択肢のアルファベット（A、B、Cのいずれか1文字）のみを出力してください。");
            template.Add("user", body);
            template.AddAssistant = true;
            var templated = System.Text.Encoding.UTF8.GetString(template.Apply());
            return templated + "Answer: ";
        }
        catch
        {
            // No embedded/known chat template - fall back to plain-text completion prompt.
            return body + "\nAnswer: ";
        }
    }

    /// <summary>
    /// Determines the token id that represents <paramref name="letter"/> as a continuation of
    /// <paramref name="prompt"/>, by diffing the tokenization of prompt vs. prompt+letter. This
    /// is robust to tokenizers that would encode "A" differently depending on context.
    /// </summary>
    private LLamaToken ResolveContinuationToken(string prompt, LLamaToken[] baseTokens, string letter)
    {
        var extendedTokens = _context!.Tokenize(prompt + letter, addBos: true, special: true);

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
            _context?.Dispose();
            _weights?.Dispose();
            _context = null;
            _weights = null;
        }
    }
}
