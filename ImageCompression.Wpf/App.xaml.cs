using System.Text;
using System.Windows;

namespace ImageCompression.Wpf;

/// <summary>
/// WPF 애플리케이션 시작점.
/// 인코딩 공급자 등록 및 초기 언어 선택을 담당합니다.
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>
    /// 애플리케이션 도메인 시작 시 1회 실행됩니다.
    /// </summary>
    static App()
    {
        // ZIP 엔트리명 복원을 위해 레거시 코드페이지(CP949 등) 사용 가능하도록 등록.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// WPF 시작 직후 호출됩니다.
    /// 기본 언어를 먼저 적용한 뒤 베이스 로직을 실행합니다.
    /// </summary>
    /// <param name="e">시작 이벤트 인자</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        // 시스템 언어를 기준으로 초기 UI 언어를 설정합니다.
        LocalizationManager.SetLanguage(LocalizationManager.GetSystemDefaultLanguageCode());
        base.OnStartup(e);
    }
}

