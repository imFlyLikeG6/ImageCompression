namespace ImageCompression.Core;

/// <summary>
/// 단일 작업(한 번의 ProcessZipAsync 호출)에 대한 집계 결과입니다.
/// UI 로그/통계 표시 및 실패 항목 필터링에 사용됩니다.
/// </summary>
public sealed class CompressionSummary
{
    /// <summary>입력 엔트리 총 개수(이미지 + 비이미지 + 디렉터리).</summary>
    public int TotalEntries { get; set; }
    /// <summary>이미지로 판별된 엔트리 개수.</summary>
    public int ImageEntries { get; set; }
    /// <summary>압축본이 실제로 저장된 이미지 개수.</summary>
    public int CompressedImageEntries { get; set; }
    /// <summary>압축 결과가 더 커서 원본을 유지한 이미지 개수.</summary>
    public int KeptOriginalBecauseLargerEntries { get; set; }
    /// <summary>압축 처리 중 오류가 발생한 이미지 개수.</summary>
    public int FailedImageEntries { get; set; }
    /// <summary>입력 ZIP 바이트 크기.</summary>
    public long InputZipBytes { get; set; }
    /// <summary>출력 결과 바이트 크기(ZIP 또는 폴더 합산).</summary>
    public long OutputZipBytes { get; set; }
    /// <summary>실패한 엔트리의 이름 목록(가능한 경우).</summary>
    public List<string> FailedEntryNames { get; } = [];

    /// <summary>용량 변화율(감소 시 양수, 증가 시 음수).</summary>
    public double ReductionPercent =>
        InputZipBytes <= 0 ? 0 : (1 - (double)OutputZipBytes / InputZipBytes) * 100;
}
