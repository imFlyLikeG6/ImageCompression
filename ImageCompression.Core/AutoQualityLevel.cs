namespace ImageCompression.Core;

/// <summary>
/// 자동 품질 탐색 강도를 나타냅니다.
/// 값이 높을수록 원본 품질 유지 쪽으로 탐색하고, 값이 낮을수록 용량 절감 쪽으로 탐색합니다.
/// </summary>
public enum AutoQualityLevel
{
    /// <summary>자동 탐색을 사용하지 않고 고정 품질값을 사용합니다.</summary>
    Off,
    /// <summary>품질 우선 탐색.</summary>
    High,
    /// <summary>품질/용량 균형 탐색.</summary>
    Balanced,
    /// <summary>용량 절감 우선 탐색.</summary>
    Aggressive
}
