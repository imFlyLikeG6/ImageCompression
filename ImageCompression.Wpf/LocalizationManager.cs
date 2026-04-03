using System.Globalization;
using System.Windows;

namespace ImageCompression.Wpf;

/// <summary>
/// 런타임 다국어 리소스 전환을 담당하는 매니저입니다.
/// ResourceDictionary 교체와 현재 스레드 문화권 설정을 함께 수행합니다.
/// </summary>
public static class LocalizationManager
{
    private const string DictionaryPrefix = "Resources/Strings.";
    private static readonly Uri KoDictionaryUri = new("Resources/Strings.ko.xaml", UriKind.Relative);
    private static readonly Uri EnDictionaryUri = new("Resources/Strings.en.xaml", UriKind.Relative);

    /// <summary>
    /// 현재 애플리케이션에 적용된 언어 코드(ko/en)입니다.
    /// </summary>
    public static string CurrentLanguage { get; private set; } = "ko";

    /// <summary>
    /// 시스템 UI 언어를 확인해 앱 기본 언어 코드를 반환합니다.
    /// 현재는 한국어면 ko, 그 외는 en으로 매핑합니다.
    /// </summary>
    /// <returns>초기 언어 코드</returns>
    public static string GetSystemDefaultLanguageCode()
    {
        var ui = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return string.Equals(ui, "ko", StringComparison.OrdinalIgnoreCase) ? "ko" : "en";
    }

    /// <summary>
    /// 런타임 리소스 사전을 교체하고 현재 문화권을 변경합니다.
    /// </summary>
    /// <param name="languageCode">요청 언어 코드(ko/en)</param>
    public static void SetLanguage(string languageCode)
    {
        // 현재 지원 언어는 ko/en 2개로 고정.
        var normalized = string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ko";
        var culture = new CultureInfo(normalized == "en" ? "en-US" : "ko-KR");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        if (Application.Current is null)
        {
            CurrentLanguage = normalized;
            return;
        }

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        // 기존 문자열 사전(ko/en)을 제거한 뒤 신규 사전을 앞쪽에 삽입합니다.
        // 앞에 둘수록 키 충돌 시 우선 적용됩니다.
        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            var src = dictionaries[i].Source?.OriginalString;
            if (!string.IsNullOrWhiteSpace(src) &&
                src.Contains(DictionaryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                dictionaries.RemoveAt(i);
            }
        }

        dictionaries.Insert(0, new ResourceDictionary
        {
            Source = normalized == "en" ? EnDictionaryUri : KoDictionaryUri
        });

        CurrentLanguage = normalized;
    }

    /// <summary>
    /// 리소스 키를 현재 언어 사전에서 조회합니다.
    /// 키가 없으면 key 문자열 자체를 반환합니다.
    /// </summary>
    /// <param name="key">리소스 키</param>
    /// <returns>번역 문자열 또는 key</returns>
    public static string GetString(string key)
    {
        // 리소스 누락 시 key 자체를 반환해 디버깅이 쉽도록 유지.
        if (Application.Current?.TryFindResource(key) is string value)
        {
            return value;
        }

        return key;
    }
}
