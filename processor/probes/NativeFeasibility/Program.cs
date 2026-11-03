using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Mpa.Processor.Probes;

internal static class Program
{
    public static int Main(string[] args)
    {
        OrtEnv.Instance().DisableTelemetryEvents();
        if (args.Length == 2 && args[0] == "--ocr-fixture")
        {
            new TextFixtureProbe(args[1]).Write();
            return 0;
        }
        if (args.Length == 4 && args[0] == "--compare")
        {
            var reference = new VerifiedModelDirectory(args[1]);
            var candidate = new VerifiedModelDirectory(args[2]);
            var comparison = new ModelEquivalenceProbe(reference.Entries, candidate.Entries).Run();
            using var comparisonOutput = new FileStream(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(comparisonOutput, comparison, ReportJson.Options);
            Console.WriteLine("Native model equivalence passed.");
            return 0;
        }
        if (args.Length != 2)
            throw new ArgumentException("Usage: NativeFeasibility <verified-model-directory> <new-report-path>");

        var models = new VerifiedModelDirectory(args[0]);
        var results = new List<ModelProbeResult>();
        foreach (var model in models.Entries)
            results.Add(new CpuInferenceProbe(model).Run());

        var decoder = new DecoderProbe().Run();
        using var process = Process.GetCurrentProcess();
        var peakWorkingSet = process.PeakWorkingSet64;
        var report = new
        {
            scope = "Native loading/CPU inference/decoder feasibility only; not recognition quality or product acceptance",
            runtime = RuntimeInformation.FrameworkDescription,
            platform = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            models = results,
            decoder,
            peakWorkingSetBytes = peakWorkingSet > 0 ? (long?)peakWorkingSet : null,
            memoryMeasurement = peakWorkingSet > 0 ? "Process.PeakWorkingSet64" : "Unavailable from Process API; not a zero-memory result"
        };
        using var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, report, ReportJson.Options);
        Console.WriteLine("Native feasibility passed; report written.");
        return 0;
    }
}

internal sealed class ModelEquivalenceProbe(IReadOnlyList<VerifiedModel> references,
    IReadOnlyList<VerifiedModel> candidates)
{
    public object Run()
    {
        var records = new List<object>();
        foreach (var reference in references)
        {
            var candidate = candidates.Single(x => x.Name == reference.Name);
            using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
            using var original = new InferenceSession(reference.Path, options);
            using var normalized = new InferenceSession(candidate.Path, options);
            var input = original.InputMetadata.Single();
            foreach (var seed in new[] { 0, 1, 42 })
            {
                var random = new Random(seed);
                var values = new DenseTensor<float>(input.Value.Dimensions);
                for (var index = 0; index < values.Length; index++)
                    values.SetValue(index, seed == 0 ? 0 : (float)random.NextDouble() * 255);
                var inputs = new[] { NamedOnnxValue.CreateFromTensor(input.Key, values) };
                using var expected = original.Run(inputs);
                using var actual = normalized.Run(inputs);
                double maxError = 0;
                if (expected.Count != actual.Count) throw new InvalidDataException("Output count changed");
                foreach (var output in expected)
                {
                    var beforeTensor = output.AsTensor<float>();
                    var afterTensor = actual.Single(x => x.Name == output.Name).AsTensor<float>();
                    if (!beforeTensor.Dimensions.SequenceEqual(afterTensor.Dimensions))
                        throw new InvalidDataException("Output shape changed");
                    var before = beforeTensor.ToArray();
                    var after = afterTensor.ToArray();
                    for (var index = 0; index < before.Length; index++)
                    {
                        var error = Math.Abs((double)before[index] - after[index]);
                        if (!float.IsFinite(before[index]) || !float.IsFinite(after[index]) ||
                            error > 1e-5 + 1e-5 * Math.Abs(before[index]))
                            throw new InvalidDataException("Model output changed beyond numerical tolerance");
                        maxError = Math.Max(error, maxError);
                    }
                }
                records.Add(new { reference.Name, seed, maxAbsoluteError = maxError,
                    sourceSha256 = reference.Sha256, normalizedSha256 = candidate.Sha256 });
            }
        }
        return new { scope = "Controlled numerical equivalence only; recognition quality not measured", records };
    }
}

internal static class ReportJson
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}

internal sealed record VerifiedModel(string Name, string Path, string Sha256);
internal sealed record ModelProbeResult(string Name, string Sha256, int[] InputShape,
    string[] OutputNames, long OutputElements, double ElapsedMilliseconds);

internal sealed class VerifiedModelDirectory
{
    public IReadOnlyList<VerifiedModel> Entries { get; }

    public VerifiedModelDirectory(string directory)
    {
        var root = Path.GetFullPath(directory);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "models.json")));
        var entries = new List<VerifiedModel>();
        foreach (var item in manifest.RootElement.GetProperty("models").EnumerateArray())
        {
            var name = item.GetProperty("file").GetString() ?? throw new InvalidDataException("Missing model filename");
            if (Path.GetFileName(name) != name || name is "." or "..")
                throw new InvalidDataException("Model path must be a filename");
            var path = Path.Combine(root, name);
            var expected = item.GetProperty("sha256").GetString() ?? throw new InvalidDataException("Missing hash");
            using var stream = File.OpenRead(path);
            if (stream.Length is <= 0 or > 128 * 1024 * 1024)
                throw new InvalidDataException("Model exceeds probe size limit");
            var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!StringComparer.Ordinal.Equals(actual, expected))
                throw new InvalidDataException("Model hash mismatch: " + name);
            entries.Add(new VerifiedModel(name, path, expected));
        }
        var reviewed = new HashSet<string>(StringComparer.Ordinal)
        {
            "face_detection_yunet_2023mar.onnx", "face_recognition_sface_2021dec.onnx",
            "object_detection_yolox_2022nov.onnx"
        };
        if (entries.Count != reviewed.Count || !reviewed.SetEquals(entries.Select(x => x.Name)))
            throw new InvalidDataException("Probe needs exactly the reviewed face detector, embedding and object models");
        Entries = entries;
    }
}

internal sealed class CpuInferenceProbe(VerifiedModel model)
{
    public ModelProbeResult Run()
    {
        using var options = new SessionOptions
        {
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
        };
        var timer = Stopwatch.StartNew();
        using var session = new InferenceSession(model.Path, options);
        if (session.InputMetadata.Count != 1)
            throw new InvalidDataException("Unexpected inference input count");
        var input = session.InputMetadata.Single();
        var shape = input.Value.Dimensions;
        if (shape.Length != 4 || shape.Any(x => x <= 0) || shape.Aggregate(1L, (x, y) => checked(x * y)) > 3 * 1024 * 1024)
            throw new InvalidDataException("Probe requires a bounded static NCHW model");
        var tensor = new DenseTensor<float>(shape);
        using var outputs = session.Run([NamedOnnxValue.CreateFromTensor(input.Key, tensor)]);
        if (outputs.Count == 0)
            throw new InvalidDataException("Inference returned no outputs");
        long elements = 0;
        foreach (var output in outputs)
        {
            var values = output.AsTensor<float>();
            foreach (var value in values)
                if (!float.IsFinite(value)) throw new InvalidDataException("Non-finite model output");
            elements = checked(elements + values.Length);
        }
        if (elements == 0) throw new InvalidDataException("Inference returned empty tensors");
        using var original = File.OpenRead(model.Path);
        if (!StringComparer.Ordinal.Equals(Convert.ToHexStringLower(SHA256.HashData(original)), model.Sha256))
            throw new InvalidDataException("Model changed during inference");
        return new ModelProbeResult(model.Name, model.Sha256, shape,
            outputs.Select(x => x.Name).ToArray(), elements, timer.Elapsed.TotalMilliseconds);
    }
}

internal sealed class DecoderProbe
{
    private readonly SKImageInfo _fixture = new(64, 48, SKColorType.Rgba8888, SKAlphaType.Opaque);

    public object Run()
    {
        using var bitmap = new SKBitmap(_fixture);
        bitmap.Erase(new SKColor(32, 96, 160));
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        using var decoded = SKBitmap.Decode(encoded);
        if (decoded is null || decoded.Width != _fixture.Width || decoded.Height != _fixture.Height)
            throw new InvalidDataException("Native JPEG round trip failed");
        var sample = decoded.GetPixel(32, 24);
        if (Math.Abs(sample.Red - 32) > 4 || Math.Abs(sample.Green - 96) > 4 || Math.Abs(sample.Blue - 160) > 4)
            throw new InvalidDataException("Native JPEG round trip changed pixel values unexpectedly");
        return new { format = "JPEG", width = decoded.Width, height = decoded.Height, encodedBytes = encoded.Size };
    }
}
