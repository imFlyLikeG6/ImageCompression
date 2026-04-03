namespace ImageCompression.Wpf;

/// <summary>
/// 출력 경로가 이미 존재할 때의 처리 정책입니다.
/// </summary>
public enum OutputConflictMode
{
    /// <summary>기존 파일/폴더를 유지하고 새 이름으로 자동 저장.</summary>
    AutoRename,
    /// <summary>기존 파일/폴더를 삭제 후 동일 경로에 저장.</summary>
    Overwrite,
    /// <summary>해당 출력은 건너뛰고 다음 작업을 진행.</summary>
    Skip
}
