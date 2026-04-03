using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ImageCompression.Core;

namespace ImageCompression.Wpf;

/// <summary>
/// 상세 압축 옵션을 편집하는 설정 창입니다.
/// 입력 검증 후 <see cref="CompressionOptions"/>를 생성해 MainWindow로 반환합니다.
/// </summary>
public partial class ImageSettingsWindow : Window
{
    /// <summary>
    /// 적용 버튼 클릭 후 반환되는 최종 옵션입니다.
    /// </summary>
    public CompressionOptions? SelectedOptions { get; private set; }
    /// <summary>
    /// 적용 버튼 클릭 후 반환되는 선택 언어 코드입니다.
    /// </summary>
    public string SelectedLanguageCode { get; private set; } = "ko";

    /// <summary>
    /// 기존 옵션을 컨트롤에 바인딩해 초기 상태를 구성합니다.
    /// </summary>
    /// <param name="currentOptions">현재 적용 중인 압축 옵션</param>
    /// <param name="currentLanguageCode">현재 적용 중인 언어 코드</param>
    public ImageSettingsWindow(CompressionOptions currentOptions, string currentLanguageCode)
    {
        InitializeComponent();

        SetComboByTag(LanguageComboBox, currentLanguageCode);
        SetComboByTag(AutoQualityLevelComboBox, currentOptions.AutoQualityLevel.ToString());
        SetComboByTag(QualityMetricComboBox, currentOptions.QualityMetric.ToString());
        SetComboByTag(ParallelWorkersComboBox, currentOptions.ParallelWorkers.ToString(CultureInfo.InvariantCulture));
        JpegQualityTextBox.Text = currentOptions.JpegQuality.ToString(CultureInfo.InvariantCulture);
        WebpQualityTextBox.Text = currentOptions.WebpQuality.ToString(CultureInfo.InvariantCulture);
        PngLevelTextBox.Text = currentOptions.PngCompressionLevel.ToString(CultureInfo.InvariantCulture);
        EnableResizeCheckBox.IsChecked = currentOptions.EnableResize;
        MaxWidthTextBox.Text = currentOptions.MaxWidth.ToString(CultureInfo.InvariantCulture);
        MaxHeightTextBox.Text = currentOptions.MaxHeight.ToString(CultureInfo.InvariantCulture);
        ResizeOnlyWhenOversizedCheckBox.IsChecked = currentOptions.ResizeOnlyWhenOversized;
        EnableAutoFormatSelectionCheckBox.IsChecked = currentOptions.EnableAutoFormatSelection;
        PreferWebpForPhotosCheckBox.IsChecked = currentOptions.PreferWebpForPhotos;
        UseLosslessWebpForAlphaCheckBox.IsChecked = currentOptions.UseLosslessWebpForAlpha;
        StripMetadataCheckBox.IsChecked = currentOptions.StripMetadata;
        UseJpeg420SubsamplingCheckBox.IsChecked = currentOptions.UseJpeg420Subsampling;
        UseProgressiveJpegCheckBox.IsChecked = currentOptions.UseProgressiveJpeg;
        ConvertToJpegCheckBox.IsChecked = currentOptions.ConvertAllToJpeg;
        KeepOriginalWhenLargerCheckBox.IsChecked = currentOptions.KeepOriginalWhenLarger;
        UpdateControlStates();
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildOptions(out var options, out var error))
        {
            MessageBox.Show(error, LocalizationManager.GetString("Msg.SettingsValidationTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedOptions = options;
        SelectedLanguageCode = GetSelectedLanguageCode();
        DialogResult = true;
    }

    private bool TryBuildOptions(out CompressionOptions options, out string error)
    {
        // 실패 시 즉시 사용자 친화 메시지를 반환하기 위해 항목별로 순차 검증합니다.
        options = new CompressionOptions();

        if (!int.TryParse(JpegQualityTextBox.Text, out var jpegQuality) || jpegQuality < 1 || jpegQuality > 100)
        {
            error = LocalizationManager.GetString("Msg.InvalidJpegQuality");
            return false;
        }

        if (!int.TryParse(WebpQualityTextBox.Text, out var webpQuality) || webpQuality < 1 || webpQuality > 100)
        {
            error = LocalizationManager.GetString("Msg.InvalidWebpQuality");
            return false;
        }

        if (!int.TryParse(PngLevelTextBox.Text, out var pngLevel) || pngLevel < 1 || pngLevel > 9)
        {
            error = LocalizationManager.GetString("Msg.InvalidPngLevel");
            return false;
        }

        options.JpegQuality = jpegQuality;
        options.WebpQuality = webpQuality;
        options.PngCompressionLevel = pngLevel;
        options.AutoQualityLevel = GetSelectedEnum<AutoQualityLevel>(AutoQualityLevelComboBox, AutoQualityLevel.Off);
        options.QualityMetric = GetSelectedEnum<QualityMetric>(QualityMetricComboBox, QualityMetric.Psnr);
        options.ParallelWorkers = GetSelectedInt(ParallelWorkersComboBox, 0);
        options.EnableResize = EnableResizeCheckBox.IsChecked == true;
        options.ResizeOnlyWhenOversized = ResizeOnlyWhenOversizedCheckBox.IsChecked != false;
        options.EnableAutoFormatSelection = EnableAutoFormatSelectionCheckBox.IsChecked != false;
        options.PreferWebpForPhotos = PreferWebpForPhotosCheckBox.IsChecked == true;
        options.UseLosslessWebpForAlpha = UseLosslessWebpForAlphaCheckBox.IsChecked != false;
        options.StripMetadata = StripMetadataCheckBox.IsChecked != false;
        options.UseJpeg420Subsampling = UseJpeg420SubsamplingCheckBox.IsChecked != false;
        options.UseProgressiveJpeg = UseProgressiveJpegCheckBox.IsChecked != false;
        options.ConvertAllToJpeg = ConvertToJpegCheckBox.IsChecked == true;
        options.KeepOriginalWhenLarger = KeepOriginalWhenLargerCheckBox.IsChecked == true;

        if (options.EnableResize)
        {
            if (!int.TryParse(MaxWidthTextBox.Text, out var maxWidth) || maxWidth <= 0)
            {
                error = LocalizationManager.GetString("Msg.InvalidMaxWidth");
                return false;
            }

            if (!int.TryParse(MaxHeightTextBox.Text, out var maxHeight) || maxHeight <= 0)
            {
                error = LocalizationManager.GetString("Msg.InvalidMaxHeight");
                return false;
            }

            options.MaxWidth = maxWidth;
            options.MaxHeight = maxHeight;
        }

        error = string.Empty;
        return true;
    }

    private static void SetComboByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static TEnum GetSelectedEnum<TEnum>(ComboBox comboBox, TEnum fallback)
        where TEnum : struct
    {
        if (comboBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<TEnum>(tag, ignoreCase: true, out var value))
        {
            return value;
        }

        return fallback;
    }

    private static int GetSelectedInt(ComboBox comboBox, int fallback)
    {
        if (comboBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return fallback;
    }

    private void AnyUxStateControl_Changed(object sender, RoutedEventArgs e)
    {
        UpdateControlStates();
    }

    private void UpdateControlStates()
    {
        // 자동 품질이 꺼져 있으면 품질 비교 지표는 의미가 없으므로 비활성화.
        var autoQualityEnabled = GetSelectedEnum<AutoQualityLevel>(AutoQualityLevelComboBox, AutoQualityLevel.Off) != AutoQualityLevel.Off;
        QualityMetricComboBox.IsEnabled = autoQualityEnabled;

        var resizeEnabled = EnableResizeCheckBox.IsChecked == true;
        MaxWidthTextBox.IsEnabled = resizeEnabled;
        MaxHeightTextBox.IsEnabled = resizeEnabled;
        ResizeOnlyWhenOversizedCheckBox.IsEnabled = resizeEnabled;

        // JPEG 강제 변환 시 WebP/PNG 및 자동 포맷 관련 옵션은 비활성화.
        var convertAllToJpeg = ConvertToJpegCheckBox.IsChecked == true;
        WebpQualityTextBox.IsEnabled = !convertAllToJpeg;
        PngLevelTextBox.IsEnabled = !convertAllToJpeg;

        EnableAutoFormatSelectionCheckBox.IsEnabled = !convertAllToJpeg;
        var autoFormatEnabled = !convertAllToJpeg && EnableAutoFormatSelectionCheckBox.IsChecked == true;
        PreferWebpForPhotosCheckBox.IsEnabled = autoFormatEnabled;
        UseLosslessWebpForAlphaCheckBox.IsEnabled = autoFormatEnabled;
    }

    private string GetSelectedLanguageCode()
    {
        return LanguageComboBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
               string.Equals(tag, "en", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : "ko";
    }
}
