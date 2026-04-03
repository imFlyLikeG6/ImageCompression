namespace ImageCompression.Core;

/// <summary>
/// 압축 결과 저장 형태를 정의합니다.
/// </summary>
public enum OutputMode
{
    /// <summary>결과를 ZIP 파일 하나로 저장합니다.</summary>
    Zip,
    /// <summary>결과를 폴더 구조로 저장합니다.</summary>
    Folder
}
