namespace ImageCompression.Core;

/// <summary>
/// 이미지 압축 동작을 제어하는 사용자 옵션 모음입니다.
/// WPF 설정창에서 값을 편집하고, Core 서비스에서 실제 처리에 사용합니다.
/// </summary>
public sealed class CompressionOptions
{
    /// <summary>자동 품질 탐색 강도.</summary>
    public AutoQualityLevel AutoQualityLevel { get; set; } = AutoQualityLevel.Off;
    /// <summary>자동 품질 탐색 시 사용할 비교 지표.</summary>
    public QualityMetric QualityMetric { get; set; } = QualityMetric.Psnr;
    /// <summary>
    /// 병렬 워커 수. 0이면 CPU 코어 수 기반 자동.
    /// </summary>
    public int ParallelWorkers { get; set; }
    /// <summary>JPEG 고정 품질(1~100).</summary>
    public int JpegQuality { get; set; } = 75;
    /// <summary>WebP 고정 품질(1~100).</summary>
    public int WebpQuality { get; set; } = 75;
    /// <summary>PNG 압축 레벨(1~9).</summary>
    public int PngCompressionLevel { get; set; } = 6;
    /// <summary>이미지 특성 기반 포맷 자동 선택 사용 여부.</summary>
    public bool EnableAutoFormatSelection { get; set; } = false;
    /// <summary>사진류에 WebP를 우선 사용할지 여부.</summary>
    public bool PreferWebpForPhotos { get; set; } = true;
    /// <summary>투명 이미지에 무손실 WebP를 우선 사용할지 여부.</summary>
    public bool UseLosslessWebpForAlpha { get; set; } = true;
    /// <summary>EXIF/ICC/IPTC/XMP 메타데이터 제거 여부.</summary>
    public bool StripMetadata { get; set; } = true;
    /// <summary>JPEG 4:2:0 색차 서브샘플링 사용 여부.</summary>
    public bool UseJpeg420Subsampling { get; set; } = true;
    /// <summary>Progressive JPEG 사용 여부.</summary>
    public bool UseProgressiveJpeg { get; set; } = true;
    /// <summary>리사이즈 사용 여부.</summary>
    public bool EnableResize { get; set; } = true;
    /// <summary>원본이 제한 크기를 초과할 때만 리사이즈할지 여부.</summary>
    public bool ResizeOnlyWhenOversized { get; set; } = true;
    /// <summary>리사이즈 최대 가로 픽셀.</summary>
    public int MaxWidth { get; set; } = 1920;
    /// <summary>리사이즈 최대 세로 픽셀.</summary>
    public int MaxHeight { get; set; } = 1080;
    /// <summary>모든 이미지를 JPEG로 강제 변환할지 여부.</summary>
    public bool ConvertAllToJpeg { get; set; }
    /// <summary>압축 결과가 더 크면 원본을 유지할지 여부.</summary>
    public bool KeepOriginalWhenLarger { get; set; } = true;
}
