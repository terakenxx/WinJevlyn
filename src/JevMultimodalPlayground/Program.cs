using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;

// Single-shot multimodal smoke test: load a vision-capable GGUF model (e.g. Gemma3-4B-it)
// plus its mmproj file, show it one image, and print its answer to one question.
//
// Usage:
//   JevMultimodalPlayground <textModel.gguf> <mmproj.gguf> <image.png> ["question text"]

if (args.Length < 3)
{
    Console.WriteLine("Usage: JevMultimodalPlayground <textModel.gguf> <mmproj.gguf> <image.png> [\"question text\"]");
    return 1;
}

var modelPath = args[0];
var mmProjPath = args[1];
var imagePath = args[2];
var question = args.Length > 3 ? args[3] : "この画像には何が写っていますか。日本語で簡潔に説明してください。";

foreach (var (label, path) in new[] { ("モデル", modelPath), ("mmproj", mmProjPath), ("画像", imagePath) })
{
    if (!File.Exists(path))
    {
        Console.WriteLine($"{label}ファイルが見つかりません: {path}");
        return 1;
    }
}

var modelParams = new ModelParams(modelPath)
{
    ContextSize = 4096,
    GpuLayerCount = 0, // CPU only
    Threads = 8,
    BatchThreads = 8,
};

Console.WriteLine($"Loading text model: {modelPath}");
using var model = await LLamaWeights.LoadFromFileAsync(modelParams);
using var context = model.CreateContext(modelParams);

var mtmdParams = MtmdContextParams.Default();
mtmdParams.UseGpu = false;

Console.WriteLine($"Loading mmproj: {mmProjPath}");
using var clipModel = await MtmdWeights.LoadFromFileAsync(mmProjPath, model, mtmdParams);

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
    MaxTokens = 512,
};

Console.WriteLine();
Console.WriteLine($"Q: {question}");
Console.WriteLine($"(image: {imagePath})");
Console.WriteLine();
Console.Write("A: ");

var sw = Stopwatch.StartNew();
var responseBuilder = new StringBuilder();
await foreach (var text in executor.InferAsync(prompt, inferenceParams))
{
    Console.Write(text);
    responseBuilder.Append(text);
}
sw.Stop();

Console.WriteLine();
Console.WriteLine();
Console.WriteLine($"Latency: {sw.ElapsedMilliseconds} ms");
return 0;

static string BuildInitialPrompt(LLamaWeights model, ChatHistory history, string userContent)
{
    history.AddMessage(AuthorRole.User, userContent);
    var template = new LLamaTemplate(model.NativeHandle) { AddAssistant = true };
    foreach (var message in history.Messages)
        template.Add(message.AuthorRole.ToString().ToLowerInvariant(), message.Content);
    return LLamaTemplate.Encoding.GetString(template.Apply());
}
