using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;

// Single-shot multimodal benchmark: load a vision-capable GGUF model (e.g. Qwen3-VL-8B-Instruct)
// plus its mmproj file, show it one image, ask one question, and print a timing breakdown
// (model load / mmproj load / prompt+image processing / generation) in milliseconds.
//
// Usage:
//   JevMultimodalPlayground <textModel.gguf> <mmproj.gguf> <image.png> ["question text"] [--cpu|--gpu]
//
// --gpu (default): try CUDA, auto-fallback to CPU if no compatible GPU/driver is found.
// --cpu: force CPU-only (no GPU layers), for an apples-to-apples baseline measurement.

if (args.Length < 3)
{
    Console.WriteLine("Usage: JevMultimodalPlayground <textModel.gguf> <mmproj.gguf> <image.png> [\"question text\"] [--cpu|--gpu]");
    return 1;
}

var modelPath = args[0];
var mmProjPath = args[1];
var imagePath = args[2];
var flags = args.Skip(3).ToArray();
var useCpu = flags.Contains("--cpu");
var question = flags.FirstOrDefault(a => !a.StartsWith("--")) ?? "この画像には何が写っていますか。日本語で簡潔に説明してください。";

foreach (var (label, path) in new[] { ("モデル", modelPath), ("mmproj", mmProjPath), ("画像", imagePath) })
{
    if (!File.Exists(path))
    {
        Console.WriteLine($"{label}ファイルが見つかりません: {path}");
        return 1;
    }
}

// Must be configured before any native library / model load happens.
if (useCpu)
{
    NativeLibraryConfig.All.WithCuda(false);
}
else
{
    NativeLibraryConfig.All.WithCuda(true).WithAutoFallback(true);
}

Console.WriteLine($"Mode: {(useCpu ? "CPU (forced)" : "GPU (CUDA, auto-fallback to CPU)")}");

var modelParams = new ModelParams(modelPath)
{
    ContextSize = 4096,
    GpuLayerCount = useCpu ? 0 : 99, // 99 = offload as many layers as fit; ignored/no-op on a CPU-only load
    Threads = 8,
    BatchThreads = 8,
    // Restrict to a single GPU (device 0) instead of letting llama.cpp auto-split layers across
    // both cards - splitting a model this small across 2 GPUs adds PCIe sync overhead that hurt
    // vision-encoding latency badly in an earlier multi-GPU test run.
    SplitMode = useCpu ? null : GPUSplitMode.None,
    MainGpu = 0,
};

var swLoadModel = Stopwatch.StartNew();
using var model = await LLamaWeights.LoadFromFileAsync(modelParams);
using var context = model.CreateContext(modelParams);
swLoadModel.Stop();

var mtmdParams = MtmdContextParams.Default();
mtmdParams.UseGpu = !useCpu;

var swLoadMmproj = Stopwatch.StartNew();
using var clipModel = await MtmdWeights.LoadFromFileAsync(mmProjPath, model, mtmdParams);
swLoadMmproj.Stop();

Console.WriteLine($"Supports vision: {clipModel.SupportsVision}, audio: {clipModel.SupportsAudio}");
if (!clipModel.SupportsVision)
{
    Console.WriteLine("このモデルは画像入力に対応していません。");
    return 1;
}

var mediaMarker = mtmdParams.MediaMarker ?? NativeApi.MtmdDefaultMarker() ?? "<media>";

var executor = new InteractiveExecutor(context, clipModel);
var embed = clipModel.LoadMedia(imagePath);
executor.Embeds.Add(embed);

var history = new ChatHistory();
history.AddMessage(AuthorRole.System, "あなたは画像を見て日本語で答えるアシスタントです。");

var prompt = BuildInitialPrompt(model, history, $"{mediaMarker}\n{question}");

var inferenceParams = new InferenceParams
{
    SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline { Temperature = 0.1f },
    AntiPrompts = new List<string> { "User:" },
    MaxTokens = 256,
};

Console.WriteLine();
Console.WriteLine($"Q: {question}");
Console.WriteLine($"(image: {imagePath})");
Console.WriteLine();
Console.Write("A: ");

var swFirstToken = Stopwatch.StartNew();
var swGeneration = new Stopwatch();
var responseBuilder = new StringBuilder();
var chunkCount = 0;
long firstTokenMs = -1;

await foreach (var text in executor.InferAsync(prompt, inferenceParams))
{
    if (firstTokenMs < 0)
    {
        firstTokenMs = swFirstToken.ElapsedMilliseconds;
        swGeneration.Start();
    }

    Console.Write(text);
    responseBuilder.Append(text);
    chunkCount++;
}

swGeneration.Stop();

Console.WriteLine();
Console.WriteLine();
Console.WriteLine("--- Timing breakdown (ms) ---");
Console.WriteLine($"Text model load:        {swLoadModel.ElapsedMilliseconds,8} ms");
Console.WriteLine($"mmproj load:             {swLoadMmproj.ElapsedMilliseconds,8} ms");
Console.WriteLine($"Prompt+image processing: {firstTokenMs,8} ms  (time to first token; includes vision encoding)");
Console.WriteLine($"Generation:              {swGeneration.ElapsedMilliseconds,8} ms  for ~{chunkCount} tokens ({(chunkCount / Math.Max(swGeneration.Elapsed.TotalSeconds, 0.001)):F1} tok/s)");
Console.WriteLine($"Total (excl. model load):{firstTokenMs + swGeneration.ElapsedMilliseconds,8} ms");
return 0;

static string BuildInitialPrompt(LLamaWeights model, ChatHistory history, string userContent)
{
    history.AddMessage(AuthorRole.User, userContent);
    var template = new LLamaTemplate(model.NativeHandle) { AddAssistant = true };
    foreach (var message in history.Messages)
        template.Add(message.AuthorRole.ToString().ToLowerInvariant(), message.Content);
    return LLamaTemplate.Encoding.GetString(template.Apply());
}
