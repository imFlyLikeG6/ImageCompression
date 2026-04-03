using System.IO.Compression;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using PngCompressionLevel = SixLabors.ImageSharp.Formats.Png.PngCompressionLevel;

namespace ImageCompression.Core;

/// <summary>
/// ZIP/폴더 기반 이미지 압축의 핵심 처리 서비스입니다.
/// - 입력 ZIP을 읽어 이미지 엔트리만 압축
/// - 옵션에 따라 포맷/품질/리사이즈/메타데이터 정책 적용
/// - 진행률/요약 통계 반환
/// </summary>
public sealed class ZipImageCompressionService
{
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff"
    };

    /// <summary>
    /// 입력 ZIP을 압축하여 출력 ZIP으로 저장합니다.
    /// 내부적으로 <see cref="ProcessZipAsync"/>를 ZIP 모드로 호출하는 래퍼입니다.
    /// </summary>
    /// <param name="inputZipPath">입력 ZIP 파일 경로</param>
    /// <param name="outputZipPath">출력 ZIP 파일 경로</param>
    /// <param name="options">압축 옵션</param>
    /// <param name="progress">진행률 콜백(선택)</param>
    /// <param name="cancellationToken">취소 토큰</param>
    /// <returns>압축 처리 결과 요약</returns>
    public async Task<CompressionSummary> CompressZipAsync(
        string inputZipPath,
        string outputZipPath,
        CompressionOptions options,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await ProcessZipAsync(inputZipPath, outputZipPath, options, OutputMode.Zip, progress, cancellationToken);
    }

    /// <summary>
    /// 입력 ZIP을 처리하여 ZIP 또는 폴더 형태로 출력합니다.
    /// </summary>
    /// <param name="inputZipPath">입력 ZIP 파일 경로</param>
    /// <param name="outputPath">출력 경로(모드에 따라 ZIP 파일 또는 폴더)</param>
    /// <param name="options">압축 옵션</param>
    /// <param name="outputMode">출력 모드(ZIP/폴더)</param>
    /// <param name="progress">진행률 콜백(선택)</param>
    /// <param name="cancellationToken">취소 토큰</param>
    /// <returns>압축 처리 결과 요약</returns>
    public async Task<CompressionSummary> ProcessZipAsync(
        string inputZipPath,
        string outputPath,
        CompressionOptions options,
        OutputMode outputMode,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 입력/옵션 유효성 선검증: 예외 메시지를 명확하게 유지하기 위해 초기에 실패시킵니다.
        ArgumentException.ThrowIfNullOrWhiteSpace(inputZipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (!File.Exists(inputZipPath))
        {
            throw new FileNotFoundException("Input zip file was not found.", inputZipPath);
        }

        ValidateOptions(options);

        var summary = new CompressionSummary
        {
            InputZipBytes = new FileInfo(inputZipPath).Length
        };
        // 병렬 워커 수를 먼저 계산하여 전체 배치 처리 전략을 고정합니다.
        var parallelWorkers = ResolveParallelWorkers(options);
        var totalImageEntries = CountImageEntries(inputZipPath);
        progress?.Report(new CompressionProgress(0, totalImageEntries));

        if (outputMode == OutputMode.Zip)
        {
            await ProcessToZipAsync(inputZipPath, outputPath, options, parallelWorkers, summary, totalImageEntries, progress, cancellationToken);
        }
        else
        {
            await ProcessToFolderAsync(inputZipPath, outputPath, options, parallelWorkers, summary, totalImageEntries, progress, cancellationToken);
        }

        progress?.Report(new CompressionProgress(totalImageEntries, totalImageEntries));
        return summary;
    }

    /// <summary>
    /// 단일 이미지 바이트를 미리보기 용으로 압축합니다.
    /// UI 미리보기/예상 크기 계산에 사용됩니다.
    /// </summary>
    /// <param name="sourceBytes">원본 이미지 바이트</param>
    /// <param name="sourceName">원본 파일명(확장자 판별용)</param>
    /// <param name="options">압축 옵션</param>
    /// <param name="cancellationToken">취소 토큰</param>
    /// <returns>압축된 이미지 바이트</returns>
    public async Task<byte[]> CompressImagePreviewAsync(
        byte[] sourceBytes,
        string sourceName,
        CompressionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ValidateOptions(options);

        var extension = Path.GetExtension(sourceName);
        var result = await CompressImageBytesAsync(sourceBytes, extension, options, cancellationToken);
        return result.Bytes;
    }

    private async Task ProcessToZipAsync(
        string inputZipPath,
        string outputZipPath,
        CompressionOptions options,
        int parallelWorkers,
        CompressionSummary summary,
        int totalImageEntries,
        IProgress<CompressionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var outputDir = Path.GetDirectoryName(outputZipPath);
        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        await using var outputStream = File.Create(outputZipPath);
        using var inputArchive = ZipArchiveEncodingHelper.OpenReadBestEffort(inputZipPath);
        using var outputArchive = new ZipArchive(outputStream, ZipArchiveMode.Create);
        var usedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processedImageEntries = 0;
        var batch = new List<PendingEntryWorkItem>(parallelWorkers);
        var sequence = 0;

        foreach (var entry in inputArchive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            summary.TotalEntries++;

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                entry.FullName.EndsWith("\\", StringComparison.Ordinal))
            {
                var dirName = EnsureUniqueEntryName(entry.FullName, usedEntryNames);
                outputArchive.CreateEntry(dirName);
                continue;
            }

            var sourceBytes = await ReadEntryBytesAsync(entry, cancellationToken);
            batch.Add(new PendingEntryWorkItem(sequence++, entry.FullName, sourceBytes));
            if (batch.Count < parallelWorkers)
            {
                continue;
            }

            processedImageEntries = await FlushZipBatchAsync(
                batch,
                options,
                summary,
                outputArchive,
                usedEntryNames,
                processedImageEntries,
                totalImageEntries,
                progress,
                cancellationToken);
        }

        if (batch.Count > 0)
        {
            processedImageEntries = await FlushZipBatchAsync(
                batch,
                options,
                summary,
                outputArchive,
                usedEntryNames,
                processedImageEntries,
                totalImageEntries,
                progress,
                cancellationToken);
        }

        summary.OutputZipBytes = new FileInfo(outputZipPath).Length;
    }

    private async Task ProcessToFolderAsync(
        string inputZipPath,
        string outputFolderPath,
        CompressionOptions options,
        int parallelWorkers,
        CompressionSummary summary,
        int totalImageEntries,
        IProgress<CompressionProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputFolderPath);

        using var inputArchive = ZipArchiveEncodingHelper.OpenReadBestEffort(inputZipPath);
        var usedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long writtenBytes = 0;
        var processedImageEntries = 0;
        var batch = new List<PendingEntryWorkItem>(parallelWorkers);
        var sequence = 0;

        foreach (var entry in inputArchive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            summary.TotalEntries++;

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                entry.FullName.EndsWith("\\", StringComparison.Ordinal))
            {
                var dirName = EnsureUniqueEntryName(entry.FullName, usedEntryNames);
                var safeDirPath = GetSafePath(outputFolderPath, dirName);
                Directory.CreateDirectory(safeDirPath);
                continue;
            }

            var sourceBytes = await ReadEntryBytesAsync(entry, cancellationToken);
            batch.Add(new PendingEntryWorkItem(sequence++, entry.FullName, sourceBytes));
            if (batch.Count < parallelWorkers)
            {
                continue;
            }

            (processedImageEntries, var flushBytes) = await FlushFolderBatchAsync(
                batch,
                options,
                summary,
                outputFolderPath,
                usedEntryNames,
                processedImageEntries,
                totalImageEntries,
                progress,
                cancellationToken);
            writtenBytes += flushBytes;
        }

        if (batch.Count > 0)
        {
            (processedImageEntries, var flushBytes) = await FlushFolderBatchAsync(
                batch,
                options,
                summary,
                outputFolderPath,
                usedEntryNames,
                processedImageEntries,
                totalImageEntries,
                progress,
                cancellationToken);
            writtenBytes += flushBytes;
        }

        summary.OutputZipBytes = writtenBytes;
    }

    private static bool IsImageEntry(string entryName)
    {
        var ext = Path.GetExtension(entryName);
        return !string.IsNullOrWhiteSpace(ext) && SupportedImageExtensions.Contains(ext);
    }

    private async Task<int> FlushZipBatchAsync(
        List<PendingEntryWorkItem> batch,
        CompressionOptions options,
        CompressionSummary summary,
        ZipArchive outputArchive,
        ISet<string> usedEntryNames,
        int processedImageEntries,
        int totalImageEntries,
        IProgress<CompressionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var results = await ProcessBatchAsync(batch, options, cancellationToken);
        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyResultSummary(summary, result);

            var outputName = EnsureUniqueEntryName(result.OutputName, usedEntryNames);
            var outEntry = outputArchive.CreateEntry(outputName, CompressionLevel.Optimal);
            await using var outEntryStream = outEntry.Open();
            await outEntryStream.WriteAsync(result.WriteBytes, cancellationToken);

            if (!result.IsImage)
            {
                continue;
            }

            processedImageEntries++;
            progress?.Report(new CompressionProgress(processedImageEntries, totalImageEntries));
        }

        batch.Clear();
        return processedImageEntries;
    }

    private async Task<(int ProcessedImages, long WrittenBytes)> FlushFolderBatchAsync(
        List<PendingEntryWorkItem> batch,
        CompressionOptions options,
        CompressionSummary summary,
        string outputFolderPath,
        ISet<string> usedEntryNames,
        int processedImageEntries,
        int totalImageEntries,
        IProgress<CompressionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var results = await ProcessBatchAsync(batch, options, cancellationToken);
        long writtenBytes = 0;
        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyResultSummary(summary, result);

            var outputName = EnsureUniqueEntryName(result.OutputName, usedEntryNames);
            var safeFilePath = GetSafePath(outputFolderPath, outputName);
            var parent = Path.GetDirectoryName(safeFilePath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await File.WriteAllBytesAsync(safeFilePath, result.WriteBytes, cancellationToken);
            writtenBytes += result.WriteBytes.Length;

            if (!result.IsImage)
            {
                continue;
            }

            processedImageEntries++;
            progress?.Report(new CompressionProgress(processedImageEntries, totalImageEntries));
        }

        batch.Clear();
        return (processedImageEntries, writtenBytes);
    }

    private static void ApplyResultSummary(CompressionSummary summary, ProcessedEntryResult result)
    {
        if (!result.IsImage)
        {
            return;
        }

        summary.ImageEntries++;
        if (result.FailedImage)
        {
            summary.FailedImageEntries++;
            summary.FailedEntryNames.Add(result.OutputName);
            return;
        }

        if (result.KeptOriginalBecauseLarger)
        {
            summary.KeptOriginalBecauseLargerEntries++;
            return;
        }

        if (result.CompressedImage)
        {
            summary.CompressedImageEntries++;
        }
    }

    private async Task<ProcessedEntryResult[]> ProcessBatchAsync(
        List<PendingEntryWorkItem> batch,
        CompressionOptions options,
        CancellationToken cancellationToken)
    {
        // 배치 내부는 병렬 처리하고, 결과는 Sequence 기준으로 재정렬하여
        // 원본 엔트리 순서를 최대한 유지합니다.
        var tasks = new Task<ProcessedEntryResult>[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            var item = batch[i];
            tasks[i] = BuildEntryResultFromBytesAsync(item, options, cancellationToken);
        }

        var results = await Task.WhenAll(tasks);
        Array.Sort(results, (a, b) => a.Sequence.CompareTo(b.Sequence));
        return results;
    }

    private async Task<ProcessedEntryResult> BuildEntryResultFromBytesAsync(
        PendingEntryWorkItem item,
        CompressionOptions options,
        CancellationToken cancellationToken)
    {
        if (!IsImageEntry(item.EntryName))
        {
            return new ProcessedEntryResult(
                item.Sequence,
                item.EntryName,
                item.SourceBytes,
                IsImage: false,
                CompressedImage: false,
                KeptOriginalBecauseLarger: false,
                FailedImage: false);
        }

        try
        {
            var encodedResult = await CompressImageBytesAsync(
                item.SourceBytes,
                Path.GetExtension(item.EntryName),
                options,
                cancellationToken);

            var writeBytes = encodedResult.Bytes;
            var outputName = options.ConvertAllToJpeg
                ? ChangeExtensionToJpeg(item.EntryName)
                : ChangeExtension(item.EntryName, encodedResult.OutputExtension);
            var keptOriginalBecauseLarger = false;

            if (options.KeepOriginalWhenLarger &&
                encodedResult.Bytes.Length >= item.SourceBytes.Length)
            {
                writeBytes = item.SourceBytes;
                outputName = item.EntryName;
                keptOriginalBecauseLarger = true;
            }

            return new ProcessedEntryResult(
                item.Sequence,
                outputName,
                writeBytes,
                IsImage: true,
                CompressedImage: !keptOriginalBecauseLarger,
                KeptOriginalBecauseLarger: keptOriginalBecauseLarger,
                FailedImage: false);
        }
        catch
        {
            return new ProcessedEntryResult(
                item.Sequence,
                item.EntryName,
                item.SourceBytes,
                IsImage: true,
                CompressedImage: false,
                KeptOriginalBecauseLarger: false,
                FailedImage: true);
        }
    }

    private static async Task<byte[]> ReadEntryBytesAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var entryInputStream = entry.Open();
        await using var rawBuffer = new MemoryStream();
        await entryInputStream.CopyToAsync(rawBuffer, cancellationToken);
        return rawBuffer.ToArray();
    }

    private static int CountImageEntries(string zipPath)
    {
        using var archive = ZipArchiveEncodingHelper.OpenReadBestEffort(zipPath);
        var count = 0;
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.EndsWith("/", StringComparison.Ordinal) &&
                !entry.FullName.EndsWith("\\", StringComparison.Ordinal) &&
                IsImageEntry(entry.FullName))
            {
                count++;
            }
        }

        return count;
    }

    private static void ValidateOptions(CompressionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.JpegQuality < 1 || options.JpegQuality > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options.JpegQuality), "JpegQuality must be between 1 and 100.");
        }

        if (options.WebpQuality < 1 || options.WebpQuality > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options.WebpQuality), "WebpQuality must be between 1 and 100.");
        }

        if (options.PngCompressionLevel < 1 || options.PngCompressionLevel > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(options.PngCompressionLevel), "PngCompressionLevel must be between 1 and 9.");
        }

        if (options.EnableResize && (options.MaxWidth <= 0 || options.MaxHeight <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxWidth and MaxHeight must be positive when resize is enabled.");
        }

        if (options.ParallelWorkers < 0 || options.ParallelWorkers > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ParallelWorkers), "ParallelWorkers must be between 0 and 64.");
        }
    }

    private static int ResolveParallelWorkers(CompressionOptions options)
    {
        if (options.ParallelWorkers > 0)
        {
            return options.ParallelWorkers;
        }

        // 자동 모드: 최소 2, 최대 16 사이로 제한해 과도한 스레드 경쟁을 방지합니다.
        return Math.Clamp(Environment.ProcessorCount, 2, 16);
    }

    private static async Task<EncodedImageResult> CompressImageBytesAsync(
        byte[] sourceBytes,
        string extension,
        CompressionOptions options,
        CancellationToken cancellationToken)
    {
        await using var sourceStream = new MemoryStream(sourceBytes);
        using var image = await Image.LoadAsync(sourceStream, cancellationToken);

        var needContentClassification = options.EnableAutoFormatSelection && !options.ConvertAllToJpeg;
        var hasAlpha = false;
        var isGraphic = false;

        if (needContentClassification)
        {
            hasAlpha = HasAlpha(image);
            // Graphic detection is unnecessary when alpha branch already selected.
            if (!hasAlpha)
            {
                isGraphic = IsLikelyGraphic(image);
            }
        }

        image.Mutate(x =>
        {
            x.AutoOrient();
            if (options.EnableResize && ShouldResize(image.Width, image.Height, options))
            {
                x.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(options.MaxWidth, options.MaxHeight)
                });
            }
        });

        if (options.StripMetadata)
        {
            StripMetadata(image);
        }

        var targetExtension = DetermineTargetExtension(extension, hasAlpha, isGraphic, options);
        var useLosslessWebp = hasAlpha && options.UseLosslessWebpForAlpha && targetExtension == ".webp";
        var selectedQuality = DetermineQualityByMetric(image, targetExtension, options, useLosslessWebp, cancellationToken);
        var encodedBytes = await EncodeImageAsync(image, targetExtension, options, selectedQuality, useLosslessWebp, cancellationToken);
        return new EncodedImageResult(encodedBytes, targetExtension);
    }

    private static int? DetermineQualityByMetric(
        Image image,
        string extension,
        CompressionOptions options,
        bool useLosslessWebp,
        CancellationToken cancellationToken)
    {
        // 무손실 포맷은 품질 탐색 대상이 아니므로 null 반환(인코더 기본/고정 경로 사용).
        var isLossy = (extension is ".jpg" or ".jpeg") || (extension == ".webp" && !useLosslessWebp);
        if (!isLossy)
        {
            return null;
        }

        if (options.AutoQualityLevel == AutoQualityLevel.Off)
        {
            return extension is ".webp" ? options.WebpQuality : options.JpegQuality;
        }

        var target = options.AutoQualityLevel switch
        {
            AutoQualityLevel.High => options.QualityMetric == QualityMetric.Ssim ? 0.985 : 40.0,
            AutoQualityLevel.Balanced => options.QualityMetric == QualityMetric.Ssim ? 0.97 : 35.0,
            AutoQualityLevel.Aggressive => options.QualityMetric == QualityMetric.Ssim ? 0.95 : 31.0,
            _ => options.QualityMetric == QualityMetric.Ssim ? 0.97 : 35.0
        };

        return FindBestQuality(image, extension, options, target, options.QualityMetric, useLosslessWebp, cancellationToken);
    }

    private static int FindBestQuality(
        Image image,
        string extension,
        CompressionOptions options,
        double targetMetric,
        QualityMetric metric,
        bool useLosslessWebp,
        CancellationToken cancellationToken)
    {
        const int minQuality = 20;
        const int maxQuality = 95;
        var best = maxQuality;
        var low = minQuality;
        var high = maxQuality;
        using var baseline = image.CloneAs<Rgba32>();

        while (low <= high)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mid = (low + high) / 2;
            var encoded = EncodeImageAsync(image, extension, options, mid, useLosslessWebp, cancellationToken).GetAwaiter().GetResult();
            using var decoded = Image.Load<Rgba32>(encoded);
            var value = metric == QualityMetric.Psnr
                ? CalculatePsnr(baseline, decoded)
                : CalculateSsim(baseline, decoded);

            if (value >= targetMetric)
            {
                best = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return best;
    }

    private static async Task<byte[]> EncodeImageAsync(
        Image image,
        string extension,
        CompressionOptions options,
        int? quality,
        bool useLosslessWebp,
        CancellationToken cancellationToken)
    {
        await using var outputStream = new MemoryStream();
        var encoder = CreateEncoder(extension, options, quality, useLosslessWebp);
        await image.SaveAsync(outputStream, encoder, cancellationToken);
        return outputStream.ToArray();
    }

    private static SixLabors.ImageSharp.Formats.IImageEncoder CreateEncoder(
        string extension,
        CompressionOptions options,
        int? quality,
        bool useLosslessWebp)
    {
        if (options.ConvertAllToJpeg)
        {
            return CreateJpegEncoder(options, quality ?? options.JpegQuality);
        }

        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => CreateJpegEncoder(options, quality ?? options.JpegQuality),
            ".png" => new PngEncoder { CompressionLevel = (PngCompressionLevel)options.PngCompressionLevel },
            ".webp" => new WebpEncoder
            {
                Quality = quality ?? options.WebpQuality,
                FileFormat = useLosslessWebp ? WebpFileFormatType.Lossless : WebpFileFormatType.Lossy
            },
            ".bmp" => new BmpEncoder(),
            ".gif" => new GifEncoder(),
            ".tif" or ".tiff" => new TiffEncoder(),
            _ => CreateJpegEncoder(options, quality ?? options.JpegQuality)
        };
    }

    private static JpegEncoder CreateJpegEncoder(CompressionOptions options, int quality)
    {
        return new JpegEncoder
        {
            Quality = quality,
            Interleaved = !options.UseProgressiveJpeg,
            ColorType = options.UseJpeg420Subsampling ? JpegEncodingColor.YCbCrRatio420 : JpegEncodingColor.YCbCrRatio444
        };
    }

    private static string ChangeExtensionToJpeg(string entryName)
    {
        var ext = Path.GetExtension(entryName);
        return string.IsNullOrWhiteSpace(ext)
            ? entryName + ".jpg"
            : entryName[..^ext.Length] + ".jpg";
    }

    private static string ChangeExtension(string entryName, string targetExtension)
    {
        var ext = Path.GetExtension(entryName);
        return string.IsNullOrWhiteSpace(ext)
            ? entryName + targetExtension
            : entryName[..^ext.Length] + targetExtension;
    }

    private static bool ShouldResize(int width, int height, CompressionOptions options)
    {
        if (!options.ResizeOnlyWhenOversized)
        {
            return true;
        }

        return width > options.MaxWidth || height > options.MaxHeight;
    }

    private static void StripMetadata(Image image)
    {
        image.Metadata.ExifProfile = null;
        image.Metadata.IccProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.XmpProfile = null;
    }

    private static string DetermineTargetExtension(string sourceExtension, bool hasAlpha, bool isGraphic, CompressionOptions options)
    {
        if (options.ConvertAllToJpeg)
        {
            return ".jpg";
        }

        if (!options.EnableAutoFormatSelection)
        {
            return NormalizeExtension(sourceExtension);
        }

        if (hasAlpha)
        {
            return options.UseLosslessWebpForAlpha ? ".webp" : ".png";
        }

        if (isGraphic)
        {
            return ".png";
        }

        return options.PreferWebpForPhotos ? ".webp" : ".jpg";
    }

    private static string NormalizeExtension(string extension)
    {
        var ext = extension.ToLowerInvariant();
        return ext switch
        {
            ".jpeg" => ".jpg",
            ".jpg" or ".png" or ".webp" or ".bmp" or ".gif" or ".tif" or ".tiff" => ext,
            _ => ".jpg"
        };
    }

    private static bool HasAlpha(Image image)
    {
        using var rgba = image.CloneAs<Rgba32>();
        var pixels = new Rgba32[rgba.Width * rgba.Height];
        rgba.CopyPixelDataTo(pixels);
        var step = Math.Max(1, pixels.Length / 4096);
        for (var i = 0; i < pixels.Length; i += step)
        {
            if (pixels[i].A < 255)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLikelyGraphic(Image image)
    {
        using var rgba = image.CloneAs<Rgba32>();
        var pixels = new Rgba32[rgba.Width * rgba.Height];
        rgba.CopyPixelDataTo(pixels);
        var unique = new HashSet<uint>();
        var step = Math.Max(1, pixels.Length / 4096);
        for (var i = 0; i < pixels.Length; i += step)
        {
            var p = pixels[i];
            var key = ((uint)p.R << 24) | ((uint)p.G << 16) | ((uint)p.B << 8) | p.A;
            unique.Add(key);
            if (unique.Count > 256)
            {
                return false;
            }
        }

        return unique.Count <= 128;
    }

    private static double CalculatePsnr(Image<Rgba32> baseline, Image<Rgba32> test)
    {
        var width = Math.Min(baseline.Width, test.Width);
        var height = Math.Min(baseline.Height, test.Height);
        var countPixels = width * height;
        var bPixels = new Rgba32[baseline.Width * baseline.Height];
        var tPixels = new Rgba32[test.Width * test.Height];
        baseline.CopyPixelDataTo(bPixels);
        test.CopyPixelDataTo(tPixels);
        double mse = 0;
        long count = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var bi = y * baseline.Width + x;
                var ti = y * test.Width + x;
                var dr = bPixels[bi].R - tPixels[ti].R;
                var dg = bPixels[bi].G - tPixels[ti].G;
                var db = bPixels[bi].B - tPixels[ti].B;
                mse += dr * dr + dg * dg + db * db;
                count += 3;
            }
        }

        if (count == 0 || countPixels <= 0)
        {
            return 99;
        }

        mse /= count;
        if (mse <= 1e-10)
        {
            return 99;
        }

        return 10 * Math.Log10((255 * 255) / mse);
    }

    private static double CalculateSsim(Image<Rgba32> baseline, Image<Rgba32> test)
    {
        var width = Math.Min(baseline.Width, test.Width);
        var height = Math.Min(baseline.Height, test.Height);
        var bPixels = new Rgba32[baseline.Width * baseline.Height];
        var tPixels = new Rgba32[test.Width * test.Height];
        baseline.CopyPixelDataTo(bPixels);
        test.CopyPixelDataTo(tPixels);
        double meanX = 0;
        double meanY = 0;
        long n = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var bi = y * baseline.Width + x;
                var ti = y * test.Width + x;
                var lx = 0.299 * bPixels[bi].R + 0.587 * bPixels[bi].G + 0.114 * bPixels[bi].B;
                var ly = 0.299 * tPixels[ti].R + 0.587 * tPixels[ti].G + 0.114 * tPixels[ti].B;
                meanX += lx;
                meanY += ly;
                n++;
            }
        }

        if (n == 0)
        {
            return 1;
        }

        meanX /= n;
        meanY /= n;

        double varX = 0;
        double varY = 0;
        double cov = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var bi = y * baseline.Width + x;
                var ti = y * test.Width + x;
                var lx = 0.299 * bPixels[bi].R + 0.587 * bPixels[bi].G + 0.114 * bPixels[bi].B;
                var ly = 0.299 * tPixels[ti].R + 0.587 * tPixels[ti].G + 0.114 * tPixels[ti].B;
                var dx = lx - meanX;
                var dy = ly - meanY;
                varX += dx * dx;
                varY += dy * dy;
                cov += dx * dy;
            }
        }

        varX /= n;
        varY /= n;
        cov /= n;

        const double c1 = (0.01 * 255) * (0.01 * 255);
        const double c2 = (0.03 * 255) * (0.03 * 255);

        var numerator = (2 * meanX * meanY + c1) * (2 * cov + c2);
        var denominator = (meanX * meanX + meanY * meanY + c1) * (varX + varY + c2);

        if (Math.Abs(denominator) < 1e-9)
        {
            return 1;
        }

        return numerator / denominator;
    }

    private static string EnsureUniqueEntryName(string entryName, ISet<string> usedNames)
    {
        if (usedNames.Add(entryName))
        {
            return entryName;
        }

        var folder = Path.GetDirectoryName(entryName)?.Replace('\\', '/') ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(entryName);
        var ext = Path.GetExtension(entryName);
        var index = 1;

        while (true)
        {
            var candidateFile = $"{fileName} ({index}){ext}";
            var candidate = string.IsNullOrEmpty(folder) ? candidateFile : $"{folder}/{candidateFile}";

            if (usedNames.Add(candidate))
            {
                return candidate;
            }

            index++;
        }
    }

    private static string GetSafePath(string rootFolder, string entryName)
    {
        // Zip Slip 방지: 결과 경로가 반드시 rootFolder 내부에만 생성되도록 강제.
        var normalized = entryName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var rootFullPath = Path.GetFullPath(rootFolder);
        var rootWithSeparator = rootFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootFullPath
            : rootFullPath + Path.DirectorySeparatorChar;
        var combinedPath = Path.GetFullPath(Path.Combine(rootFullPath, normalized));

        if (!combinedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(combinedPath, rootFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid entry path detected in zip.");
        }

        return combinedPath;
    }

    private readonly record struct PendingEntryWorkItem(int Sequence, string EntryName, byte[] SourceBytes);
    private readonly record struct ProcessedEntryResult(
        int Sequence,
        string OutputName,
        byte[] WriteBytes,
        bool IsImage,
        bool CompressedImage,
        bool KeptOriginalBecauseLarger,
        bool FailedImage);
    private readonly record struct EncodedImageResult(byte[] Bytes, string OutputExtension);
}
