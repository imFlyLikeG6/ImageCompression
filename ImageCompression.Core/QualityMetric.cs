namespace ImageCompression.Core;

/// <summary>
/// 자동 품질 탐색 시 사용할 품질 비교 지표입니다.
/// </summary>
public enum QualityMetric
{
    /// <summary>PSNR(수치 기반) 지표를 사용합니다.</summary>
    Psnr,
    /// <summary>SSIM(체감 품질 유사도) 지표를 사용합니다.</summary>
    Ssim
}
