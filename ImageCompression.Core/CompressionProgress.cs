namespace ImageCompression.Core;

/// <summary>
/// 압축 진행 상황(처리 이미지 수 / 전체 이미지 수)을 UI에 전달하기 위한 값 타입입니다.
/// </summary>
public readonly record struct CompressionProgress(int ProcessedImages, int TotalImages);
