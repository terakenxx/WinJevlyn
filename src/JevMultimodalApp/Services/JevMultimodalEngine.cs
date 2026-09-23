using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Native;

namespace JevMultimodalApp.Services;

public sealed record MultimodalLoadResult(bool UsingGpu, long ElapsedMilliseconds);

/// <summary>
/// Loads a vision-capable GGUF model (e.g. Qwen3-VL-8B-Instruct) plus its mmproj file and answers
/// questions about a single image, streaming the response token-by-token. GPU is used when
/// available and falls back to CPU automatically - see the two-tier fallback in LoadModelAsync.
/// </summary>
public sealed class JevMultimodalEngine : IDisposable
{
    private static readonly object NativeConfigGate = new();
    private static bool _nativeConfigured;
    private static bool _cudaLibraryAvailable;

    private readonly object _gate = new();
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private MtmdWeights? _clip;

    public bool IsModelLoaded
    {
        get { lock (_gate) return _context is not null && _clip is not null; }
    }

    public bool IsUsingGpu { get; private set; }

    /// <summary>Milliseconds spent on prompt + image processing (time to first token) for the last InferStreamingAsync call.</summary>
    public long LastPromptProcessingMs { get; private set; }

    /// <summary>Milliseconds spent generating tokens after the first one, for the last InferStreamingAsync call.</summary>
    public long LastGenerationMs { get; private set; }

    /// <summary>Number of streamed chunks (approximately tokens) produced by the last InferStreamingAsync call.</summary>
    public int LastTokenCount { get; private set; }

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

            var (newWeights, newContext, usingGpu) = await LoadTextModelWithFallback(textModelPath, threads, contextSize);

            var mtmdParams = MtmdContextParams.Default();
            mtmdParams.UseGpu = usingGpu;

            MtmdWeights newClip;
            try
            {
                newClip = await MtmdWeights.LoadFromFileAsync(mmprojPath, newWeights, mtmdParams);
            }
            catch
            {
                newContext.Dispose();
                newWeights.Dispose();
                throw;
            }

            stopwatch.Stop();

            lock (_gate)
            {
                _clip?.Dispose();
                _context?.Dispose();
                _weights?.Dispose();
                _weights = newWeights;
                _context = newContext;
                _clip = newClip;
                IsUsingGpu = usingGpu;
            }

            return new MultimodalLoadResult(usingGpu, stopwatch.ElapsedMilliseconds);
        });
    }

    /// <summary>
    /// Ensures the CUDA-capable native library is selected if a compatible GPU/driver is present,
    /// falling back to the CPU native library otherwise. Must run before any model is loaded, and
    /// only once per process (a selected native library cannot be swapped afterwards).
    ///
    /// A dry run resolves which library will actually be used (CUDA vs CPU) so that
    /// LoadTextModelWithFallback knows whether attempting GPU layers is even meaningful - a CPU-only
    /// native library silently ignores GpuLayerCount rather than throwing, so that alone can't be
    /// used to detect whether GPU is really in play.
    /// </summary>
    private static void EnsureNativeLibraryConfigured()
    {
        lock (NativeConfigGate)
        {
            if (_nativeConfigured)
                return;

            NativeLibraryConfig.All.WithCuda(true).WithAutoFallback(true);

            INativeLibrary? loadedLLama = null;
            try
            {
                NativeLibraryConfig.All.DryRun(out loadedLLama, out _);
            }
            catch
            {
                // Leave _cudaLibraryAvailable false; the text-model load will just use CPU params.
            }

            _cudaLibraryAvailable = loadedLLama?.Metadata?.UseCuda == true;
            _nativeConfigured = true;
        }
    }

    /// <summary>
    /// Tries to load the text model fully offloaded to GPU first. If that fails (e.g. the GPU is
    /// present but doesn't have enough VRAM for this particular model), retries on CPU using the
    /// same already-selected native library.
    /// </summary>
    private static async Task<(LLamaWeights Weights, LLamaContext Context, bool UsingGpu)> LoadTextModelWithFallback(
        string textModelPath, int threads, uint contextSize)
    {
        if (_cudaLibraryAvailable)
        {
            try
            {
                var gpuParams = new ModelParams(textModelPath)
                {
                    ContextSize = contextSize,
                    GpuLayerCount = 99, // offload as many layers as fit
                    Threads = threads,
                    BatchThreads = threads,
                    // Restrict to a single GPU instead of letting llama.cpp auto-split layers across
                    // multiple cards - on a 2-GPU machine this measured ~46x slower end-to-end (mostly
                    // from PCIe sync overhead during vision encoding) than pinning to one GPU.
                    SplitMode = GPUSplitMode.None,
                    MainGpu = 0,
                };

                var weights = await LLamaWeights.LoadFromFileAsync(gpuParams);
                var context = weights.CreateContext(gpuParams);
                return (weights, context, UsingGpu: true);
            }
            catch
            {
                // GPU present but this model didn't fit (e.g. insufficient VRAM) - fall through to CPU.
            }
        }

        var cpuParams = new ModelParams(textModelPath)
        {
            ContextSize = contextSize,
            GpuLayerCount = 0,
            Threads = threads,
            BatchThreads = threads,
        };

        var cpuWeights = await LLamaWeights.LoadFromFileAsync(cpuParams);
        var cpuContext = cpuWeights.CreateContext(cpuParams);
        return (cpuWeights, cpuContext, UsingGpu: false);
    }

    public async IAsyncEnumerable<string> InferStreamingAsync(string imagePath, string question, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("画像ファイルが見つかりません。", imagePath);

        LLamaWeights weights;
        LLamaContext context;
        MtmdWeights clip;
        lock (_gate)
        {
            if (_weights is null || _context is null || _clip is null)
                throw new InvalidOperationException("モデルが読み込まれていません。先にGGUFモデルとmmprojを読み込んでください。");
            weights = _weights;
            context = _context;
            clip = _clip;
        }

        context.NativeHandle.MemoryClear(true);
        clip.ClearMedia();

        var mediaMarker = NativeApi.MtmdDefaultMarker() ?? "<media>";
        var executor = new InteractiveExecutor(context, clip);
        var embed = clip.LoadMedia(imagePath);
        executor.Embeds.Add(embed);

        var history = new ChatHistory();
        history.AddMessage(AuthorRole.System, "あなたは画像を見て日本語で答えるアシスタントです。");
        history.AddMessage(AuthorRole.User, $"{mediaMarker}\n{question}");
        var template = new LLamaTemplate(weights.NativeHandle) { AddAssistant = true };
        foreach (var message in history.Messages)
            template.Add(message.AuthorRole.ToString().ToLowerInvariant(), message.Content);
        var prompt = Encoding.UTF8.GetString(template.Apply());

        var inferenceParams = new InferenceParams
        {
            SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline { Temperature = 0.1f },
            AntiPrompts = new List<string> { "User:" },
            MaxTokens = 512,
        };

        var swFirstToken = Stopwatch.StartNew();
        var swGeneration = new Stopwatch();
        long firstTokenMs = -1;
        var tokenCount = 0;

        await foreach (var text in executor.InferAsync(prompt, inferenceParams, cancellationToken))
        {
            if (firstTokenMs < 0)
            {
                firstTokenMs = swFirstToken.ElapsedMilliseconds;
                swGeneration.Start();
            }

            tokenCount++;
            yield return text;
        }

        swGeneration.Stop();
        LastPromptProcessingMs = firstTokenMs < 0 ? swFirstToken.ElapsedMilliseconds : firstTokenMs;
        LastGenerationMs = swGeneration.ElapsedMilliseconds;
        LastTokenCount = tokenCount;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _clip?.Dispose();
            _context?.Dispose();
            _weights?.Dispose();
            _clip = null;
            _context = null;
            _weights = null;
        }
    }
}
