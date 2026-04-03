using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageCompression.Core;
using Microsoft.Win32;

namespace ImageCompression.Wpf;

/// <summary>
/// 메인 작업 화면.
/// 입력 로딩/미리보기/실행/로그/사용자 설정 저장까지 전체 UX 흐름을 조율합니다.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ZipImageCompressionService _service = new();
    private CompressionOptions _options = new();
    private OutputMode _outputMode = OutputMode.Zip;
    private readonly List<InputImageItem> _inputImageItems = [];
    private readonly List<string> _selectedInputPaths = [];
    private readonly List<string> _recentInputPaths = [];
    private readonly List<SavedPreset> _presets = [];
    private readonly HashSet<string> _failedDisplayPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _lastOutputPaths = [];
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _estimateCts;
    private CancellationTokenSource? _runCts;
    private bool _isWorking;
    private bool _isUpdatingQuickControls;
    private bool _suspendSettingsPersistence;
    private long _inputTotalBytes;
    private long _inputImageTotalBytes;
    private long _inputNonImageTotalBytes;
    private int _inputLoadVersion;
    private string _languageCode = LocalizationManager.GetSystemDefaultLanguageCode();
    private OutputConflictMode _outputConflictMode = OutputConflictMode.AutoRename;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly PropertyInfo? FailedEntryNamesProperty =
        typeof(CompressionSummary).GetProperty("FailedEntryNames", BindingFlags.Instance | BindingFlags.Public);
    private static readonly HashSet<string> SupportedInputFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff"
    };
    private static string T(string key) => LocalizationManager.GetString(key);
    private static string Tf(string key, params object[] args) => string.Format(CultureInfo.CurrentUICulture, T(key), args);

    /// <summary>
    /// 메인 화면 초기화.
    /// 사용자 설정 로드, 이벤트 연결, 기본 UI 상태(로그/진행바/요약) 구성까지 수행합니다.
    /// </summary>
    public MainWindow()
    {
        // 생성자에서는 "초기 상태 복원 + UI 연결"만 수행하고,
        // 실제 무거운 작업(입력 스캔 등)은 사용자 액션 시점에 실행합니다.
        InitializeComponent();
        InputImagesDataGrid.ItemsSource = _inputImageItems;
        LoadUserSettings();
        LocalizationManager.SetLanguage(_languageCode);
        _options.AutoQualityLevel = AutoQualityLevel.Off;
        _options.EnableAutoFormatSelection = false;
        ApplyUserSettingsToControls();
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        RefreshPresetCombo();
        UpdateInputFilesSummary();
        UpdateOutputPathPreview();
        ResetCompressionProgress(0, 0);
        EstimateSummaryTextBlock.Text = T("Ui.EstimateWait");
        AppendLog(T("Log.Ready"));
    }

    /// <summary>
    /// 메인 창 로드 완료 시 빠른 설정 컨트롤을 현재 옵션과 동기화합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">로드 이벤트 인자</param>
    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SyncQuickControlsFromOptions();
    }

    /// <summary>
    /// 입력 선택 버튼 클릭 시 파일/폴더 선택 컨텍스트 메뉴를 엽니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">클릭 이벤트 인자</param>
    private void BrowseInputButton_Click(object sender, RoutedEventArgs e)
    {
        if (FindName("InputBrowseContextMenu") is not ContextMenu menu ||
            sender is not Button button)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 파일 선택 메뉴 처리기입니다.
    /// 다중 선택을 허용해 ZIP/이미지 파일 입력을 설정합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">클릭 이벤트 인자</param>
    private async void BrowseInputFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var fileDialog = new OpenFileDialog
            {
                Title = T("Dlg.OpenFileTitle"),
                Filter = T("Dlg.OpenFileFilter"),
                CheckFileExists = true,
                Multiselect = true
            };

            if (fileDialog.ShowDialog() != true)
            {
                return;
            }

            await SetInputPathsAsync(fileDialog.FileNames);
        }
        catch (Exception ex)
        {
            AppendLog(Tf("Log.Error", ex.Message), LogSeverity.Error);
        }
    }

    /// <summary>
    /// 폴더 선택 메뉴 처리기입니다.
    /// 선택한 폴더를 입력으로 등록하고 내부 이미지를 스캔합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">클릭 이벤트 인자</param>
    private async void BrowseInputFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folderDialog = new OpenFolderDialog
            {
                Title = T("Dlg.OpenFolderTitle")
            };

            if (folderDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(folderDialog.FolderName))
            {
                return;
            }

            await SetInputPathAsync(folderDialog.FolderName);
        }
        catch (Exception ex)
        {
            AppendLog(Tf("Log.Error", ex.Message), LogSeverity.Error);
        }
    }

    /// <summary>
    /// 최근 입력 경로 목록 컨텍스트 메뉴를 표시합니다.
    /// 항목 클릭 시 해당 경로를 다시 입력으로 로드합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">클릭 이벤트 인자</param>
    private void RecentInputsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recentInputPaths.Count == 0)
        {
            AppendLog(T("Msg.NoRecentInputs"));
            return;
        }

        if (sender is not Button button)
        {
            return;
        }

        var menu = new ContextMenu();
        foreach (var path in _recentInputPaths)
        {
            var item = new MenuItem { Header = path };
            item.Click += async (_, _) =>
            {
                try
                {
                    await SetInputPathAsync(path);
                }
                catch (Exception ex)
                {
                    AppendLog(Tf("Log.Error", ex.Message), LogSeverity.Error);
                }
            };
            menu.Items.Add(item);
        }

        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 드래그 오버 중 입력 가능 여부를 판정해 커서 이펙트를 갱신합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">드래그 이벤트 인자</param>
    private void Window_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (TryGetDroppedInputPaths(e.Data, out _, out _))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    /// <summary>
    /// 드롭된 파일/폴더를 검증한 뒤 입력으로 설정합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">드롭 이벤트 인자</param>
    private async void Window_PreviewDrop(object sender, System.Windows.DragEventArgs e)
    {
        try
        {
            if (!TryGetDroppedInputPaths(e.Data, out var inputPaths, out var validationError))
            {
                MessageBox.Show(validationError ?? T("Msg.InvalidDrop"), T("Msg.InputValidationTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
                return;
            }

            await SetInputPathsAsync(inputPaths);
            AppendLog(T("Log.DragDropSet"));
            e.Handled = true;
        }
        catch (Exception ex)
        {
            AppendLog(Tf("Log.Error", ex.Message), LogSeverity.Error);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 출력 모드(ZIP/폴더) 변경을 반영하고 설정을 저장합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">선택 변경 이벤트 인자</param>
    private void OutputModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox &&
            comboBox.SelectedItem is ComboBoxItem selected &&
            selected.Tag is string tag)
        {
            _outputMode = tag == "folder" ? OutputMode.Folder : OutputMode.Zip;
            UpdateOutputPathPreview();
            if (!_suspendSettingsPersistence)
            {
                SaveUserSettings();
            }
        }
    }

    /// <summary>
    /// 출력 접미사 입력 변경 시 경로 미리보기를 갱신하고 설정을 저장합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">텍스트 변경 이벤트 인자</param>
    private void OutputSuffixTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateOutputPathPreview();
        if (!_suspendSettingsPersistence)
        {
            SaveUserSettings();
        }
    }

    /// <summary>
    /// 출력 충돌 처리 정책(자동 이름 변경/덮어쓰기/건너뛰기)을 변경합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">선택 변경 이벤트 인자</param>
    private void OutputConflictModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox ||
            comboBox.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        _outputConflictMode = tag switch
        {
            "overwrite" => OutputConflictMode.Overwrite,
            "skip" => OutputConflictMode.Skip,
            _ => OutputConflictMode.AutoRename
        };
        if (!_suspendSettingsPersistence)
        {
            SaveUserSettings();
        }
    }

    /// <summary>
    /// 고급 설정 창을 열고 적용 결과를 메인 상태에 반영합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">클릭 이벤트 인자</param>
    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new ImageSettingsWindow(CloneOptions(_options), _languageCode)
        {
            Owner = this
        };

        if (settingsWindow.ShowDialog() != true || settingsWindow.SelectedOptions is null)
        {
            return;
        }

        _options = settingsWindow.SelectedOptions;
        if (!string.Equals(settingsWindow.SelectedLanguageCode, _languageCode, StringComparison.OrdinalIgnoreCase))
        {
            _languageCode = settingsWindow.SelectedLanguageCode;
            LocalizationManager.SetLanguage(_languageCode);
            RefreshLocalizedRuntimeTexts();
        }
        SyncQuickControlsFromOptions();
        AppendLog(T("Log.SettingsUpdated"));
        _ = RefreshPreviewForCurrentSelectionAsync();
        _ = RecalculateEstimateAsync();
        SaveUserSettings();
    }

    /// <summary>
    /// 언어 변경 시 런타임에 구성되는 텍스트들을 즉시 재계산합니다.
    /// </summary>
    private void RefreshLocalizedRuntimeTexts()
    {
        if (_selectedInputPaths.Count > 1)
        {
            InputZipTextBox.Text = Tf("Ui.MultiZipSelected", _selectedInputPaths.Count);
        }

        if (_inputImageItems.Count == 0)
        {
            EstimateSummaryTextBlock.Text = T("Ui.EstimateWait");
        }

        UpdateInputFilesSummary();
        UpdateOutputPathPreview();
    }

    /// <summary>
    /// 압축 실행 버튼 처리기입니다.
    /// 입력 검증 후 다중 입력을 순차 처리하고 진행률/로그/결과 경로를 갱신합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">클릭 이벤트 인자</param>
    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        // 새 실행 시작 전 이전 실행 토큰 정리(중복 실행 방지).
        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        var runToken = _runCts.Token;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (_selectedInputPaths.Count == 0)
            {
                MessageBox.Show(T("Msg.InvalidInputPath"), T("Msg.InputValidationTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_inputImageItems.Count == 0)
            {
                MessageBox.Show(T("Msg.NoImagesToProcess"), T("Msg.InputValidationTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var suffix = OutputSuffixTextBox.Text.Trim();
            if (!IsValidSuffix(suffix))
            {
                MessageBox.Show(T("Msg.InvalidSuffix"), T("Msg.InputValidationTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ToggleUi(isWorking: true);
            ResetCompressionProgress(0, _inputImageItems.Count);
            AppendLog(T("Log.Start"));
            AppendLog(Tf("Log.Mode", _outputMode == OutputMode.Zip ? T("Mode.Zip") : T("Mode.Folder")));
            var totalImages = _inputImageItems.Count;
            var processedImageOffset = 0;
            var totalEntries = 0;
            var imageEntries = 0;
            var compressedEntries = 0;
            var failedEntries = 0;
            var keptOriginalEntries = 0;
            long totalInputBytes = 0;
            long totalOutputBytes = 0;
            _failedDisplayPaths.Clear();
            _lastOutputPaths.Clear();

            foreach (var inputPath in _selectedInputPaths)
            {
                try
                {
                    // Per-ZIP isolation: keep processing remaining ZIPs even if one fails.
                    runToken.ThrowIfCancellationRequested();
                    var requestedOutputPath = BuildOutputPath(inputPath, _outputMode, suffix);
                    var outputPath = ResolveOutputPathByConflictMode(requestedOutputPath);
                    if (string.IsNullOrWhiteSpace(outputPath))
                    {
                        AppendLog(Tf("Log.SkippedByConflict", requestedOutputPath));
                        continue;
                    }
                    AppendLog(Tf("Log.OutPath", outputPath));
                    _lastOutputPaths.Add(outputPath);

                    var prepared = await PrepareInputAsZipAsync(inputPath);
                    try
                    {
                        var currentOffset = processedImageOffset;
                        var progress = new Progress<CompressionProgress>(p =>
                        {
                            var processed = Math.Min(totalImages, currentOffset + p.ProcessedImages);
                            UpdateCompressionProgress(processed, totalImages);
                        });

                        var summary = await _service.ProcessZipAsync(prepared.ZipPath, outputPath, _options, _outputMode, progress, runToken);
                        processedImageOffset = Math.Min(totalImages, processedImageOffset + summary.ImageEntries);

                        totalEntries += summary.TotalEntries;
                        imageEntries += summary.ImageEntries;
                        compressedEntries += summary.CompressedImageEntries;
                        failedEntries += summary.FailedImageEntries;
                        keptOriginalEntries += summary.KeptOriginalBecauseLargerEntries;
                        totalInputBytes += summary.InputZipBytes;
                        totalOutputBytes += summary.OutputZipBytes;
                        var prefix = _selectedInputPaths.Count > 1 ? $"[{Path.GetFileName(inputPath)}] " : string.Empty;
                        foreach (var failed in GetFailedEntryNamesCompat(summary))
                        {
                            _failedDisplayPaths.Add(prefix + failed);
                        }
                    }
                    finally
                    {
                        prepared.Dispose();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppendLog(Tf("Log.ZipFailedContinue", Path.GetFileName(inputPath), ex.Message), LogSeverity.Error);
                }
            }

            var reductionPercent = totalInputBytes <= 0 ? 0 : (1 - (double)totalOutputBytes / totalInputBytes) * 100;
            AppendLog(Tf("Log.Done", totalEntries, imageEntries), LogSeverity.Success);
            AppendLog(Tf("Log.ResultCount", compressedEntries, failedEntries), LogSeverity.Success);
            AppendLog(Tf("Log.KeptOriginal", keptOriginalEntries), LogSeverity.Success);
            AppendLog(Tf("Log.SizeResult", FormatBytes(totalInputBytes), FormatBytes(totalOutputBytes), reductionPercent), LogSeverity.Success);
            UpdateCompressionProgress(imageEntries, totalImages);
            stopwatch.Stop();
            if (FindName("OpenOutputButton") is Button openOutputButton)
            {
                openOutputButton.IsEnabled = _lastOutputPaths.Count > 0;
            }
            ApplyInputFilter();
        }
        catch (OperationCanceledException)
        {
            AppendLog(T("Msg.WorkCancelled"));
        }
        catch (Exception ex)
        {
            AppendLog(Tf("Log.Error", ex.Message), LogSeverity.Error);
            MessageBox.Show(Tf("Msg.WorkError", ex.Message), T("Msg.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ToggleUi(isWorking: false);
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    /// <summary>
    /// 목록 선택 변경 시 우측 미리보기를 다시 생성합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">선택 변경 이벤트 인자</param>
    private async void InputImagesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            await RefreshPreviewForCurrentSelectionAsync();
        }
        catch (Exception ex)
        {
            AppendLog(Tf("Log.Error", ex.Message), LogSeverity.Error);
        }
    }

    /// <summary>
    /// 현재 선택된 항목의 압축 결과 미리보기를 비동기로 갱신합니다.
    /// </summary>
    /// <returns>비동기 작업</returns>
    private async Task RefreshPreviewForCurrentSelectionAsync()
    {
        if (InputImagesDataGrid.SelectedItem is not InputImageItem selectedItem)
        {
            ClearInlinePreview(T("Ui.PreviewSelect"));
            return;
        }

        try
        {
            _previewCts?.Cancel();
            _previewCts = new CancellationTokenSource();
            var token = _previewCts.Token;

            InlinePreviewSummaryTextBlock.Text = T("Ui.PreviewGenerating");

            var originalBytes = await ReadImageBytesAsync(selectedItem);
            token.ThrowIfCancellationRequested();

            var compressedBytes = await _service.CompressImagePreviewAsync(
                originalBytes,
                selectedItem.DisplayPath,
                BuildPreviewCompressionOptions(),
                token);

            var previewOutputBytes = PreviewCompareCheckBox.IsChecked == true ? originalBytes : compressedBytes;
            var outputDimensionsText = GetImageDimensionsTextFromBytes(previewOutputBytes);

            InlineCompressedImage.Source = CreateBitmapImage(previewOutputBytes);
            InlinePreviewSummaryTextBlock.Text = BuildInlinePreviewSummary(
                previewOutputBytes.LongLength,
                outputDimensionsText);
        }
        catch (OperationCanceledException)
        {
            // selection changed quickly; ignore
        }
        catch (Exception ex)
        {
            ClearInlinePreview(Tf("Ui.PreviewFailed", ex.Message));
        }
    }

    /// <summary>
    /// 옵션 값을 메인 화면의 빠른 설정 컨트롤로 동기화합니다.
    /// </summary>
    private void SyncQuickControlsFromOptions()
    {
        if (!TryGetQuickControls(
                out var jpegQualitySlider,
                out var jpegQualityValueTextBlock,
                out var enableResizeCheckBox,
                out var maxWidthTextBox,
                out var maxHeightTextBox))
        {
            return;
        }

        _isUpdatingQuickControls = true;
        try
        {
            jpegQualitySlider.Value = _options.JpegQuality;
            jpegQualityValueTextBlock.Text = _options.JpegQuality.ToString();
            enableResizeCheckBox.IsChecked = _options.EnableResize;
            maxWidthTextBox.Text = _options.MaxWidth.ToString();
            maxHeightTextBox.Text = _options.MaxHeight.ToString();
        }
        finally
        {
            _isUpdatingQuickControls = false;
        }
    }

    /// <summary>
    /// JPEG 품질 슬라이더 변경을 옵션/미리보기/예상치에 반영합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">슬라이더 값 변경 이벤트 인자</param>
    private async void MainJpegQualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        try
        {
            if (_isUpdatingQuickControls)
            {
                return;
            }

            if (!TryGetQuickControls(
                    out var jpegQualitySlider,
                    out var jpegQualityValueTextBlock,
                    out _,
                    out _,
                    out _))
            {
                return;
            }

            _options.JpegQuality = (int)Math.Round(jpegQualitySlider.Value);
            _options.AutoQualityLevel = AutoQualityLevel.Off;
            jpegQualityValueTextBlock.Text = _options.JpegQuality.ToString();
            await RefreshPreviewForCurrentSelectionAsync();
            await RecalculateEstimateAsync();
            SaveUserSettings();
        }
        catch (Exception ex)
        {
            AppendLog(Tf("Log.Error", ex.Message), LogSeverity.Error);
        }
    }

    /// <summary>
    /// 리사이즈 사용 여부 변경을 반영하고 미리보기/예상치를 갱신합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">변경 이벤트 인자</param>
    private async void MainResizeOptionChanged(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isUpdatingQuickControls)
            {
                return;
            }

            if (!TryApplyResizeQuickSettings())
            {
                return;
            }

            await RefreshPreviewForCurrentSelectionAsync();
            await RecalculateEstimateAsync();
            SaveUserSettings();
        }
        catch (Exception ex)
        {
            AppendLog(Tf("Log.Error", ex.Message), LogSeverity.Error);
        }
    }

    /// <summary>
    /// 리사이즈 최대 가로/세로 입력 변경을 반영합니다.
    /// 유효한 값일 때만 즉시 미리보기/예상치를 다시 계산합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">텍스트 변경 이벤트 인자</param>
    private async void MainResizeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            if (_isUpdatingQuickControls)
            {
                return;
            }

            if (!TryApplyResizeQuickSettings())
            {
                return;
            }

            await RefreshPreviewForCurrentSelectionAsync();
            await RecalculateEstimateAsync();
            SaveUserSettings();
        }
        catch (Exception ex)
        {
            AppendLog(Tf("Log.Error", ex.Message), LogSeverity.Error);
        }
    }

    /// <summary>
    /// 빠른 설정 컨트롤 값을 현재 옵션으로 적용합니다.
    /// </summary>
    /// <returns>적용 성공 여부</returns>
    private bool TryApplyResizeQuickSettings()
    {
        if (!TryGetQuickControls(
                out _,
                out _,
                out var enableResizeCheckBox,
                out var maxWidthTextBox,
                out var maxHeightTextBox))
        {
            return false;
        }

        _options.EnableResize = enableResizeCheckBox.IsChecked == true;
        if (!_options.EnableResize)
        {
            return true;
        }

        if (!int.TryParse(maxWidthTextBox.Text, out var maxWidth) || maxWidth <= 0)
        {
            return false;
        }

        if (!int.TryParse(maxHeightTextBox.Text, out var maxHeight) || maxHeight <= 0)
        {
            return false;
        }

        _options.MaxWidth = maxWidth;
        _options.MaxHeight = maxHeight;
        return true;
    }

    /// <summary>
    /// 창 종료 시 현재 사용자 설정을 저장합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">종료 이벤트 인자</param>
    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        SaveUserSettings();
    }

    /// <summary>
    /// 사용자 설정 파일을 로드해 런타임 상태를 복원합니다.
    /// </summary>
    private void LoadUserSettings()
    {
        try
        {
            // 설정 파일은 호환성을 위해 "없거나 일부 필드가 누락"되어도 안전하게 동작해야 합니다.
            var settingsPath = GetUserSettingsPath();
            if (!File.Exists(settingsPath))
            {
                return;
            }

            var json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions);
            if (settings is null)
            {
                return;
            }

            if (settings.CompressionOptions is not null)
            {
                _options = settings.CompressionOptions;
            }

            _outputMode = settings.OutputMode;
            _languageCode = ResolveLanguageCode(settings.Language);
            _outputConflictMode = settings.OutputConflictMode;
            _recentInputPaths.Clear();
            _recentInputPaths.AddRange((settings.RecentInputs ?? []).Where(x => !string.IsNullOrWhiteSpace(x)));
            _presets.Clear();
            foreach (var preset in settings.Presets ?? [])
            {
                if (string.IsNullOrWhiteSpace(preset.Name) || preset.Options is null)
                {
                    continue;
                }

                _presets.Add(new SavedPreset(
                    preset.Name,
                    CloneOptions(preset.Options),
                    preset.OutputMode,
                    preset.OutputSuffix ?? string.Empty,
                    preset.ConflictMode));
            }
        }
        catch
        {
            // ignore corrupted setting file and continue with defaults
        }
    }

    /// <summary>
    /// 로드된 설정 값을 UI 컨트롤 선택 상태에 적용합니다.
    /// </summary>
    private void ApplyUserSettingsToControls()
    {
        _suspendSettingsPersistence = true;
        try
        {
            if (FindName("OutputSuffixTextBox") is TextBox suffixTextBox)
            {
                suffixTextBox.Text = LoadStoredOutputSuffix();
            }

            if (FindName("OutputModeComboBox") is ComboBox outputModeComboBox)
            {
                foreach (var item in outputModeComboBox.Items.OfType<ComboBoxItem>())
                {
                    if (item.Tag is string tag &&
                        ((tag == "zip" && _outputMode == OutputMode.Zip) ||
                         (tag == "folder" && _outputMode == OutputMode.Folder)))
                    {
                        outputModeComboBox.SelectedItem = item;
                        break;
                    }
                }
            }
            if (FindName("OutputConflictModeComboBox") is ComboBox conflictComboBox)
            {
                var tag = _outputConflictMode switch
                {
                    OutputConflictMode.Overwrite => "overwrite",
                    OutputConflictMode.Skip => "skip",
                    _ => "autorename"
                };
                foreach (var item in conflictComboBox.Items.OfType<ComboBoxItem>())
                {
                    if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                    {
                        conflictComboBox.SelectedItem = item;
                        break;
                    }
                }
            }
        }
        finally
        {
            _suspendSettingsPersistence = false;
        }
    }

    /// <summary>
    /// 현재 옵션/출력/언어/프리셋 정보를 설정 파일에 저장합니다.
    /// </summary>
    private void SaveUserSettings()
    {
        try
        {
            // 실패해도 앱 동작은 지속되도록 best-effort 저장 정책을 유지합니다.
            var settingsPath = GetUserSettingsPath();
            var settingsDirectory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
            {
                Directory.CreateDirectory(settingsDirectory);
            }

            var settings = new UserSettings
            {
                CompressionOptions = CloneOptions(_options),
                OutputMode = _outputMode,
                OutputSuffix = GetCurrentOutputSuffix(),
                Language = _languageCode,
                OutputConflictMode = _outputConflictMode,
                RecentInputs = _recentInputPaths.ToList(),
                Presets = _presets.Select(p => p with { Options = CloneOptions(p.Options) }).ToList()
            };

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(settingsPath, json);
        }
        catch
        {
            // best effort persistence
        }
    }

    private static string GetUserSettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "ImageCompression", "settings.json");
    }

    /// <summary>
    /// 설정 파일에 저장된 출력 접미사를 읽어옵니다.
    /// </summary>
    /// <returns>저장된 접미사(없으면 빈 문자열)</returns>
    private string LoadStoredOutputSuffix()
    {
        try
        {
            var settingsPath = GetUserSettingsPath();
            if (!File.Exists(settingsPath))
            {
                return string.Empty;
            }

            var json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions);
            return settings?.OutputSuffix ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// UI의 출력 접미사 입력값을 읽어옵니다.
    /// </summary>
    /// <returns>현재 접미사 문자열</returns>
    private string GetCurrentOutputSuffix()
    {
        if (FindName("OutputSuffixTextBox") is TextBox suffixTextBox)
        {
            return suffixTextBox.Text ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>
    /// 메인 빠른 설정 컨트롤들을 null-safe하게 조회합니다.
    /// </summary>
    /// <param name="jpegQualitySlider">JPEG 품질 슬라이더</param>
    /// <param name="jpegQualityValueTextBlock">JPEG 품질 표시 텍스트</param>
    /// <param name="enableResizeCheckBox">리사이즈 사용 체크박스</param>
    /// <param name="maxWidthTextBox">최대 가로 입력</param>
    /// <param name="maxHeightTextBox">최대 세로 입력</param>
    /// <returns>모든 컨트롤 조회 성공 여부</returns>
    private bool TryGetQuickControls(
        out Slider jpegQualitySlider,
        out TextBlock jpegQualityValueTextBlock,
        out CheckBox enableResizeCheckBox,
        out TextBox maxWidthTextBox,
        out TextBox maxHeightTextBox)
    {
        jpegQualitySlider = FindName("MainJpegQualitySlider") as Slider ?? null!;
        jpegQualityValueTextBlock = FindName("MainJpegQualityValueTextBlock") as TextBlock ?? null!;
        enableResizeCheckBox = FindName("MainEnableResizeCheckBox") as CheckBox ?? null!;
        maxWidthTextBox = FindName("MainMaxWidthTextBox") as TextBox ?? null!;
        maxHeightTextBox = FindName("MainMaxHeightTextBox") as TextBox ?? null!;

        return jpegQualitySlider is not null &&
               jpegQualityValueTextBlock is not null &&
               enableResizeCheckBox is not null &&
               maxWidthTextBox is not null &&
               maxHeightTextBox is not null;
    }

    /// <summary>
    /// 현재 입력/모드/접미사 기준으로 출력 경로 미리보기를 업데이트합니다.
    /// </summary>
    private void UpdateOutputPathPreview()
    {
        if (FindName("OutputPathPreviewTextBlock") is not TextBlock outputPathPreviewTextBlock)
        {
            return;
        }

        if (_selectedInputPaths.Count == 0)
        {
            outputPathPreviewTextBlock.Text = T("Ui.OutputPathPreviewHint");
            return;
        }

        var suffix = OutputSuffixTextBox.Text.Trim();
        if (_selectedInputPaths.Count == 1)
        {
            outputPathPreviewTextBlock.Text = BuildOutputPath(_selectedInputPaths[0], _outputMode, suffix);
            return;
        }

        outputPathPreviewTextBlock.Text = Tf("Ui.MultiZipSelected", _selectedInputPaths.Count);
    }

    private static InputScanResult ScanInputImageItems(IReadOnlyList<string> inputPaths)
    {
        var items = new List<InputImageItem>();
        long totalBytes = 0;
        long imageTotalBytes = 0;

        foreach (var inputPath in inputPaths)
        {
            var pathResult = ScanSingleInputImageItems(inputPath, inputPaths.Count > 1);
            items.AddRange(pathResult.Items);
            totalBytes += pathResult.TotalBytes;
            imageTotalBytes += pathResult.ImageBytes;
        }

        var nonImageBytes = Math.Max(0, totalBytes - imageTotalBytes);
        return new InputScanResult(items, totalBytes, imageTotalBytes, nonImageBytes);
    }

    private static InputScanResult ScanSingleInputImageItems(string inputPath, bool includeSourcePrefix)
    {
        var items = new List<InputImageItem>();
        long totalBytes = 0;
        long imageTotalBytes = 0;
        var sourcePrefix = includeSourcePrefix ? $"[{Path.GetFileName(inputPath)}] " : string.Empty;

        if (Directory.Exists(inputPath))
        {
            foreach (var filePath in Directory.EnumerateFiles(inputPath, "*", SearchOption.AllDirectories))
            {
                var fileInfo = new FileInfo(filePath);
                totalBytes += fileInfo.Length;

                if (!IsImageExtension(Path.GetExtension(filePath)))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(inputPath, filePath);
                items.Add(new InputImageItem(
                    displayPath: sourcePrefix + relativePath,
                    dimensionsText: GetImageDimensionsTextFromFile(filePath),
                    sourceType: "Folder",
                    sizeBytes: fileInfo.Length,
                    location: InputImageLocation.FileSystem,
                    sourcePath: filePath,
                    zipEntryPath: string.Empty));

                imageTotalBytes += fileInfo.Length;
            }
        }
        else if (File.Exists(inputPath) && string.Equals(Path.GetExtension(inputPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zipArchive = ZipArchiveEncodingHelper.OpenReadBestEffort(inputPath);
            foreach (var entry in zipArchive.Entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                {
                    continue;
                }

                totalBytes += entry.Length;
                if (!IsImageExtension(Path.GetExtension(entry.FullName)))
                {
                    continue;
                }

                items.Add(new InputImageItem(
                    displayPath: sourcePrefix + entry.FullName,
                    dimensionsText: GetImageDimensionsTextFromZipEntry(inputPath, entry.FullName),
                    sourceType: "ZIP",
                    sizeBytes: entry.Length,
                    location: InputImageLocation.ZipEntry,
                    sourcePath: inputPath,
                    zipEntryPath: entry.FullName));

                imageTotalBytes += entry.Length;
            }
        }
        else if (File.Exists(inputPath) && IsImageExtension(Path.GetExtension(inputPath)))
        {
            var fileInfo = new FileInfo(inputPath);
            totalBytes = fileInfo.Length;
            imageTotalBytes = fileInfo.Length;
            items.Add(new InputImageItem(
                displayPath: sourcePrefix + Path.GetFileName(inputPath),
                dimensionsText: GetImageDimensionsTextFromFile(inputPath),
                sourceType: "File",
                sizeBytes: fileInfo.Length,
                location: InputImageLocation.FileSystem,
                sourcePath: inputPath,
                zipEntryPath: string.Empty));
        }

        var nonImageBytes = Math.Max(0, totalBytes - imageTotalBytes);
        return new InputScanResult(items, totalBytes, imageTotalBytes, nonImageBytes);
    }

    /// <summary>
    /// 입력 스캔 결과를 내부 상태와 화면에 반영합니다.
    /// </summary>
    /// <param name="scanResult">스캔 결과 집합</param>
    private void ApplyInputScanResult(InputScanResult scanResult)
    {
        _inputImageItems.Clear();
        _inputImageItems.AddRange(scanResult.Items);
        _inputTotalBytes = scanResult.TotalBytes;
        _inputImageTotalBytes = scanResult.ImageBytes;
        _inputNonImageTotalBytes = scanResult.NonImageBytes;
        ApplyInputFilter();
        UpdateInputFilesSummary();
        ResetCompressionProgress(0, _inputImageItems.Count);
        _ = RecalculateEstimateAsync();
    }

    /// <summary>
    /// 입력 이미지 개수/총 용량 요약을 갱신하고 실행 버튼 상태를 업데이트합니다.
    /// </summary>
    private void UpdateInputFilesSummary()
    {
        var totalBytes = _inputImageItems.Sum(x => x.SizeBytes);
        InputFilesCountTextBlock.Text = Tf("Ui.InputSummary", _inputImageItems.Count, FormatBytes(totalBytes));
        UpdateRunButtonAvailability();
        if (_inputImageItems.Count == 0)
        {
            ClearInlinePreview(T("Ui.PreviewSelect"));
        }
    }

    private static CompressionOptions CloneOptions(CompressionOptions source)
    {
        var cloned = new CompressionOptions
        {
            JpegQuality = source.JpegQuality,
            WebpQuality = source.WebpQuality,
            PngCompressionLevel = source.PngCompressionLevel,
            AutoQualityLevel = source.AutoQualityLevel,
            QualityMetric = source.QualityMetric,
            ParallelWorkers = source.ParallelWorkers,
            EnableAutoFormatSelection = source.EnableAutoFormatSelection,
            PreferWebpForPhotos = source.PreferWebpForPhotos,
            UseLosslessWebpForAlpha = source.UseLosslessWebpForAlpha,
            StripMetadata = source.StripMetadata,
            UseJpeg420Subsampling = source.UseJpeg420Subsampling,
            UseProgressiveJpeg = source.UseProgressiveJpeg,
            EnableResize = source.EnableResize,
            ResizeOnlyWhenOversized = source.ResizeOnlyWhenOversized,
            MaxWidth = source.MaxWidth,
            MaxHeight = source.MaxHeight,
            ConvertAllToJpeg = source.ConvertAllToJpeg,
            KeepOriginalWhenLarger = source.KeepOriginalWhenLarger
        };
        return cloned;
    }

    /// <summary>
    /// 실행 중 여부에 따라 주요 UI 컨트롤 활성 상태를 전환합니다.
    /// </summary>
    /// <param name="isWorking">실행 중 여부</param>
    private void ToggleUi(bool isWorking)
    {
        _isWorking = isWorking;
        if (FindName("RunButton") is Button runButton)
        {
            runButton.IsEnabled = !_isWorking && _inputImageItems.Count > 0;
        }

        if (FindName("CancelButton") is Button cancelButton)
        {
            cancelButton.IsEnabled = isWorking;
        }

        if (FindName("CompressProgressBar") is ProgressBar compressProgressBar)
        {
            compressProgressBar.IsEnabled = isWorking;
        }
    }

    /// <summary>
    /// 현재 입력 개수와 실행 상태를 기준으로 실행 버튼 활성화 여부를 갱신합니다.
    /// </summary>
    private void UpdateRunButtonAvailability()
    {
        if (FindName("RunButton") is Button runButton)
        {
            runButton.IsEnabled = !_isWorking && _inputImageItems.Count > 0;
        }
    }

    /// <summary>
    /// 압축 진행 바와 진행 텍스트를 초기 상태로 설정합니다.
    /// </summary>
    /// <param name="processed">처리 완료 수</param>
    /// <param name="total">총 대상 수</param>
    private void ResetCompressionProgress(int processed, int total)
    {
        var max = Math.Max(total, 1);
        CompressProgressBar.Minimum = 0;
        CompressProgressBar.Maximum = max;
        CompressProgressBar.Value = Math.Min(processed, max);
        CompressProgressTextBlock.Text = $"{processed} / {total}";
    }

    /// <summary>
    /// 압축 진행 상태를 현재 처리 수치로 갱신합니다.
    /// </summary>
    /// <param name="processed">처리 완료 수</param>
    /// <param name="total">총 대상 수</param>
    private void UpdateCompressionProgress(int processed, int total)
    {
        var max = Math.Max(total, 1);
        CompressProgressBar.Maximum = max;
        CompressProgressBar.Value = Math.Min(processed, max);
        CompressProgressTextBlock.Text = $"{processed} / {total}";
    }

    private static string BuildOutputPath(string inputZipPath, OutputMode outputMode, string suffix)
    {
        string directory;
        string baseName;

        if (Directory.Exists(inputZipPath))
        {
            directory = Path.GetDirectoryName(inputZipPath) ?? string.Empty;
            baseName = Path.GetFileName(inputZipPath);
        }
        else
        {
            directory = Path.GetDirectoryName(inputZipPath) ?? string.Empty;
            baseName = Path.GetFileNameWithoutExtension(inputZipPath);
        }

        var normalizedSuffix = suffix.Trim();
        var outputName = string.IsNullOrWhiteSpace(normalizedSuffix) ? baseName : baseName + normalizedSuffix;

        return outputMode == OutputMode.Zip
            ? Path.Combine(directory, outputName + ".zip")
            : Path.Combine(directory, outputName);
    }

    private static bool IsValidSuffix(string suffix)
    {
        return suffix.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static bool TryGetDroppedInputPaths(System.Windows.IDataObject dataObject, out IReadOnlyList<string> inputPaths, out string? validationError)
    {
        inputPaths = Array.Empty<string>();
        validationError = null;

        if (!dataObject.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (dataObject.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return false;
        }

        if (files.Length == 1 && Directory.Exists(files[0]))
        {
            inputPaths = [files[0]];
            return true;
        }

        if (files.Any(path => !File.Exists(path)))
        {
            return false;
        }

        if (files.Length > 1)
        {
            if (files.Any(path => !string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase)))
            {
                validationError = T("Msg.MultiInputOnlyZip");
                return false;
            }

            inputPaths = files;
            return true;
        }

        var singleExt = Path.GetExtension(files[0]);
        if (!SupportedInputFileExtensions.Contains(singleExt))
        {
            return false;
        }

        inputPaths = [files[0]];
        return true;
    }

    private static bool IsSupportedInputPath(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            return true;
        }

        if (!File.Exists(inputPath))
        {
            return false;
        }

        return SupportedInputFileExtensions.Contains(Path.GetExtension(inputPath));
    }

    /// <summary>
    /// 단일 입력 경로를 입력 소스로 설정합니다.
    /// </summary>
    /// <param name="inputPath">입력 경로</param>
    /// <returns>비동기 작업</returns>
    private async Task SetInputPathAsync(string inputPath)
    {
        await SetInputPathsAsync([inputPath]);
    }

    /// <summary>
    /// 입력 경로 목록을 검증/스캔하고 목록/요약/미리보기를 갱신합니다.
    /// </summary>
    /// <param name="inputPaths">입력 경로 목록</param>
    /// <returns>비동기 작업</returns>
    private async Task SetInputPathsAsync(IReadOnlyList<string> inputPaths)
    {
        // 입력 검증 단계: 지원 형식/다중 입력 규칙/ZIP 가용성 확인.
        if (inputPaths.Count == 0)
        {
            MessageBox.Show(T("Msg.InvalidInputPath"), T("Msg.InputValidationTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (inputPaths.Count > 1 &&
            inputPaths.Any(path => !File.Exists(path) || !string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(T("Msg.MultiInputOnlyZip"), T("Msg.InputValidationTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (inputPaths.Any(path => !IsSupportedInputPath(path)))
        {
            MessageBox.Show(T("Msg.UnsupportedInput"), T("Msg.InputValidationTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (inputPaths.Any(path =>
                File.Exists(path) &&
                string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase) &&
                !IsZipReadable(path)))
        {
            MessageBox.Show(T("Msg.ZipNotReadable"), T("Msg.InputValidationTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var requestVersion = Interlocked.Increment(ref _inputLoadVersion);
        SetInputLoadingState(true, T("Ui.InputLoading"));
        ResetCompressionProgress(0, 0);

        try
        {
            // 비동기 스캔 경쟁 상태 방지:
            // 가장 마지막 요청(requestVersion)만 UI에 반영합니다.
            _selectedInputPaths.Clear();
            _selectedInputPaths.AddRange(inputPaths);
            AddRecentInputs(inputPaths);
            InputZipTextBox.Text = _selectedInputPaths.Count == 1
                ? _selectedInputPaths[0]
                : Tf("Ui.MultiZipSelected", _selectedInputPaths.Count);
            UpdateOutputPathPreview();

            var inputPathSnapshot = _selectedInputPaths.ToArray();
            var scanResult = await Task.Run(() => ScanInputImageItems(inputPathSnapshot));
            if (requestVersion != _inputLoadVersion)
            {
                return;
            }

            ApplyInputScanResult(scanResult);
            AppendLog(Tf("Log.InputAnalyzed", _inputImageItems.Count));
        }
        catch (InvalidDataException)
        {
            _inputImageItems.Clear();
            _inputTotalBytes = 0;
            _inputImageTotalBytes = 0;
            _inputNonImageTotalBytes = 0;
            InputImagesDataGrid.Items.Refresh();
            UpdateInputFilesSummary();
            MessageBox.Show(T("Msg.ZipCorrupted"), T("Msg.InputValidationTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (requestVersion == _inputLoadVersion)
            {
                SetInputLoadingState(false);
            }
        }
    }

    /// <summary>
    /// 입력 스캔 중 오버레이 표시 상태를 변경합니다.
    /// </summary>
    /// <param name="isLoading">로딩 표시 여부</param>
    /// <param name="message">표시 메시지(기본값 사용 가능)</param>
    private void SetInputLoadingState(bool isLoading, string? message = null)
    {
        InputLoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        InputLoadingTextBlock.Text = message ?? T("Ui.InputLoading");
    }

    /// <summary>
    /// 입력이 폴더/단일 파일인 경우 임시 ZIP으로 변환해 공통 처리 경로를 맞춥니다.
    /// </summary>
    /// <param name="inputPath">원본 입력 경로</param>
    /// <returns>처리용 ZIP 정보</returns>
    private async Task<PreparedInputZip> PrepareInputAsZipAsync(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            var tempZip = CreateTempZipPath();
            await Task.Run(() => ZipFile.CreateFromDirectory(inputPath, tempZip, CompressionLevel.NoCompression, includeBaseDirectory: true));
            return new PreparedInputZip(tempZip, isTemporary: true);
        }

        if (string.Equals(Path.GetExtension(inputPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return new PreparedInputZip(inputPath, isTemporary: false);
        }

        if (File.Exists(inputPath))
        {
            var tempZip = CreateTempZipPath();
            using (var zipArchive = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                var entryName = Path.GetFileName(inputPath);
                zipArchive.CreateEntryFromFile(inputPath, entryName, CompressionLevel.NoCompression);
            }

            return new PreparedInputZip(tempZip, isTemporary: true);
        }

        throw new InvalidOperationException(T("Msg.UnsupportedInput"));
    }

    private static string CreateTempZipPath()
    {
        var tempFile = Path.GetTempFileName();
        return Path.ChangeExtension(tempFile, ".zip");
    }

    private static bool IsZipReadable(string zipPath)
    {
        try
        {
            using var archive = ZipArchiveEncodingHelper.OpenReadBestEffort(zipPath);
            _ = archive.Entries.Count;
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class PreparedInputZip(string zipPath, bool isTemporary) : IDisposable
    {
        public string ZipPath { get; } = zipPath;
        public bool IsTemporary { get; } = isTemporary;

        public void Dispose()
        {
            if (!IsTemporary)
            {
                return;
            }

            try
            {
                if (File.Exists(ZipPath))
                {
                    File.Delete(ZipPath);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    /// <summary>
    /// 파일 시스템 또는 ZIP 엔트리에서 원본 이미지 바이트를 읽어옵니다.
    /// </summary>
    /// <param name="item">입력 이미지 항목</param>
    /// <returns>원본 이미지 바이트</returns>
    private async Task<byte[]> ReadImageBytesAsync(InputImageItem item)
    {
        if (item.Location == InputImageLocation.FileSystem)
        {
            return await File.ReadAllBytesAsync(item.SourcePath);
        }

        using var zipArchive = ZipArchiveEncodingHelper.OpenReadBestEffort(item.SourcePath);
        var entry = zipArchive.GetEntry(item.ZipEntryPath);
        if (entry is null)
        {
            throw new InvalidOperationException(T("Msg.ZipEntryMissing"));
        }

        await using var entryStream = entry.Open();
        await using var memory = new MemoryStream();
        await entryStream.CopyToAsync(memory);
        return memory.ToArray();
    }

    private static bool IsImageExtension(string extension)
    {
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetImageDimensionsTextFromFile(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            return GetImageDimensionsTextFromStream(stream);
        }
        catch
        {
            return "-";
        }
    }

    private static string GetImageDimensionsTextFromZipEntry(string zipPath, string entryPath)
    {
        try
        {
            using var zipArchive = ZipArchiveEncodingHelper.OpenReadBestEffort(zipPath);
            var entry = zipArchive.GetEntry(entryPath);
            if (entry is null)
            {
                return "-";
            }

            using var stream = entry.Open();
            return GetImageDimensionsTextFromStream(stream);
        }
        catch
        {
            return "-";
        }
    }

    private static string GetImageDimensionsTextFromStream(Stream stream)
    {
        var info = SixLabors.ImageSharp.Image.Identify(stream);
        if (info is null)
        {
            return "-";
        }

        return $"{info.Width}x{info.Height}px";
    }

    private static string GetImageDimensionsTextFromBytes(byte[] imageBytes)
    {
        try
        {
            using var stream = new MemoryStream(imageBytes);
            return GetImageDimensionsTextFromStream(stream);
        }
        catch
        {
            return "-";
        }
    }

    /// <summary>
    /// 인라인 미리보기 이미지를 비우고 상태 메시지를 표시합니다.
    /// </summary>
    /// <param name="message">표시 메시지</param>
    private void ClearInlinePreview(string message)
    {
        InlineCompressedImage.Source = null;
        InlinePreviewSummaryTextBlock.Text = message;
    }

    /// <summary>
    /// 샘플 기반 추정으로 예상 출력 용량/절감률을 다시 계산합니다.
    /// </summary>
    /// <returns>비동기 작업</returns>
    private async Task RecalculateEstimateAsync()
    {
        _estimateCts?.Cancel();
        _estimateCts = new CancellationTokenSource();
        var token = _estimateCts.Token;

        if (_inputImageItems.Count == 0 || _inputImageTotalBytes <= 0)
        {
            EstimateSummaryTextBlock.Text = T("Ui.EstimateNoImages");
            return;
        }

        EstimateSummaryTextBlock.Text = T("Ui.EstimateCalculating");

        try
        {
            // Debounce rapid slider/resize edits.
            await Task.Delay(220, token);

            const int maxSampleCount = 4;
            const int maxSampleBytesPerFile = 8 * 1024 * 1024;
            var estimateOptions = BuildEstimateCompressionOptions();

            var sampleItems = PickSampleItems(_inputImageItems, maxSampleCount);
            long sampledOriginalBytes = 0;
            long sampledOutputBytes = 0;

            foreach (var item in sampleItems)
            {
                token.ThrowIfCancellationRequested();

                var originalBytes = await ReadImageBytesAsync(item);
                if (originalBytes.LongLength > maxSampleBytesPerFile)
                {
                    continue;
                }

                var compressed = await _service.CompressImagePreviewAsync(
                    originalBytes,
                    item.DisplayPath,
                    estimateOptions,
                    token);
                var finalBytes = (_options.KeepOriginalWhenLarger && compressed.LongLength >= originalBytes.LongLength)
                    ? originalBytes.LongLength
                    : compressed.LongLength;

                sampledOriginalBytes += originalBytes.LongLength;
                sampledOutputBytes += finalBytes;
            }

            if (sampledOriginalBytes <= 0)
            {
                EstimateSummaryTextBlock.Text = T("Ui.EstimateSampleUnavailable");
                return;
            }

            var weightedRatio = (double)sampledOutputBytes / sampledOriginalBytes;
            var predictedImageBytes = (long)Math.Round(_inputImageTotalBytes * weightedRatio);
            var predictedTotalBytes = _inputNonImageTotalBytes + predictedImageBytes;
            var reduction = _inputTotalBytes <= 0 ? 0 : (1 - (double)predictedTotalBytes / _inputTotalBytes) * 100;

            EstimateSummaryTextBlock.Text = Tf("Ui.EstimateResult", sampleItems.Count, FormatBytes(predictedTotalBytes), reduction);
        }
        catch (OperationCanceledException)
        {
            // ignore - newer estimate requested
        }
        catch
        {
            EstimateSummaryTextBlock.Text = T("Ui.EstimateFailed");
        }
    }

    private static List<InputImageItem> PickSampleItems(IReadOnlyList<InputImageItem> items, int maxCount)
    {
        if (items.Count <= maxCount)
        {
            return items.ToList();
        }

        var result = new List<InputImageItem>(maxCount);
        var step = (double)(items.Count - 1) / (maxCount - 1);
        for (var i = 0; i < maxCount; i++)
        {
            var index = (int)Math.Round(i * step);
            result.Add(items[index]);
        }

        return result;
    }

    private static BitmapImage CreateBitmapImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string BuildInlinePreviewSummary(
        long outputBytes,
        string outputDimensionsText)
    {
        return string.Format(
            CultureInfo.CurrentUICulture,
            LocalizationManager.GetString("Ui.PreviewSummary"),
            outputDimensionsText,
            FormatBytes(outputBytes));
    }

    /// <summary>
    /// 미리보기 전용 압축 옵션을 생성합니다.
    /// 속도 우선을 위해 고비용 자동 기능은 비활성화합니다.
    /// </summary>
    /// <returns>미리보기용 옵션</returns>
    private CompressionOptions BuildPreviewCompressionOptions()
    {
        var options = CloneOptions(_options);
        options.AutoQualityLevel = AutoQualityLevel.Off;
        options.EnableAutoFormatSelection = false;
        return options;
    }

    /// <summary>
    /// 예상 용량 계산 전용 옵션을 생성합니다.
    /// </summary>
    /// <returns>예상 계산용 옵션</returns>
    private CompressionOptions BuildEstimateCompressionOptions()
    {
        var options = BuildPreviewCompressionOptions();
        options.StripMetadata = false;
        return options;
    }

    /// <summary>
    /// 실행 중 작업 취소를 요청합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">클릭 이벤트 인자</param>
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _runCts?.Cancel();
    }

    /// <summary>
    /// 마지막 출력 경로를 탐색기에서 엽니다.
    /// 파일이면 선택 상태로, 폴더면 해당 폴더를 엽니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">클릭 이벤트 인자</param>
    private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutputPaths.Count == 0)
        {
            return;
        }

        var lastPath = _lastOutputPaths[^1];
        try
        {
            if (File.Exists(lastPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{lastPath}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(lastPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{lastPath}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            AppendLog(Tf("Log.Error", ex.Message), LogSeverity.Error);
        }
    }

    /// <summary>
    /// 비교 보기 토글 변경 시 미리보기를 즉시 재생성합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">변경 이벤트 인자</param>
    private async void PreviewCompareCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        await RefreshPreviewForCurrentSelectionAsync();
    }

    /// <summary>
    /// 실패 항목만 보기 토글 변경 시 목록 필터를 다시 적용합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">변경 이벤트 인자</param>
    private void ShowFailedOnlyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        ApplyInputFilter();
    }

    /// <summary>
    /// 실패 항목 보기 옵션에 따라 목록 데이터 소스를 필터링합니다.
    /// </summary>
    private void ApplyInputFilter()
    {
        // 실패 항목만 보기 체크 시, 마지막 실행에서 수집한 실패 경로 기준으로 필터링.
        var showFailedOnly = FindName("ShowFailedOnlyCheckBox") is CheckBox failedOnlyCheckBox &&
                             failedOnlyCheckBox.IsChecked == true;
        if (showFailedOnly)
        {
            var failedOnly = _inputImageItems.Where(i => _failedDisplayPaths.Contains(i.DisplayPath)).ToList();
            InputImagesDataGrid.ItemsSource = failedOnly;
        }
        else
        {
            InputImagesDataGrid.ItemsSource = _inputImageItems;
        }

        InputImagesDataGrid.Items.Refresh();
    }

    /// <summary>
    /// 현재 설정을 프리셋으로 저장(동명 존재 시 덮어쓰기)합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">클릭 이벤트 인자</param>
    private void SavePresetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = PresetComboBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"Preset {DateTime.Now:MMdd-HHmm}";
            }

            var preset = new SavedPreset(name, CloneOptions(_options), _outputMode, GetCurrentOutputSuffix(), _outputConflictMode);
            var existingIndex = _presets.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                _presets[existingIndex] = preset;
            }
            else
            {
                _presets.Add(preset);
            }

            RefreshPresetCombo(name);
            SaveUserSettings();
            AppendLog(Tf("Log.PresetSaved", name));
        }
        catch (Exception ex)
        {
            AppendLog(Tf("Log.Error", ex.Message), LogSeverity.Error);
        }
    }

    /// <summary>
    /// 선택한 프리셋을 로드해 옵션/출력 정책/미리보기에 반영합니다.
    /// </summary>
    /// <param name="sender">이벤트 발신자</param>
    /// <param name="e">클릭 이벤트 인자</param>
    private async void ApplyPresetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = PresetComboBox.Text?.Trim();
            var preset = _presets.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(preset.Name) || preset.Options is null)
            {
                return;
            }

            _options = CloneOptions(preset.Options);
            _outputMode = preset.OutputMode;
            _outputConflictMode = preset.ConflictMode;
            if (FindName("OutputSuffixTextBox") is TextBox suffixTextBox)
            {
                suffixTextBox.Text = preset.OutputSuffix;
            }
            ApplyUserSettingsToControls();
            SyncQuickControlsFromOptions();
            UpdateOutputPathPreview();
            await RefreshPreviewForCurrentSelectionAsync();
            await RecalculateEstimateAsync();
            SaveUserSettings();
            AppendLog(Tf("Log.PresetApplied", preset.Name));
        }
        catch (Exception ex)
        {
            AppendLog(Tf("Log.Error", ex.Message), LogSeverity.Error);
        }
    }

    /// <summary>
    /// 프리셋 콤보박스 항목을 새로고침하고 필요 시 특정 항목을 선택합니다.
    /// </summary>
    /// <param name="selectName">선택할 프리셋 이름(선택)</param>
    private void RefreshPresetCombo(string? selectName = null)
    {
        if (FindName("PresetComboBox") is not ComboBox comboBox)
        {
            return;
        }

        comboBox.ItemsSource = _presets.Select(x => x.Name).OrderBy(x => x).ToList();
        if (!string.IsNullOrWhiteSpace(selectName))
        {
            comboBox.Text = selectName;
        }
    }

    /// <summary>
    /// 출력 경로가 이미 존재할 때 충돌 정책에 따라 최종 경로를 결정합니다.
    /// </summary>
    /// <param name="requestedPath">요청 출력 경로</param>
    /// <returns>실제 사용할 경로, 또는 건너뛰기 시 null</returns>
    private string? ResolveOutputPathByConflictMode(string requestedPath)
    {
        // 출력 충돌 정책(자동 이름 변경/덮어쓰기/건너뛰기)을 중앙화.
        var exists = File.Exists(requestedPath) || Directory.Exists(requestedPath);
        if (!exists)
        {
            return requestedPath;
        }

        return _outputConflictMode switch
        {
            OutputConflictMode.Overwrite => DeleteExistingAndReuse(requestedPath) ? requestedPath : null,
            OutputConflictMode.Skip => null,
            _ => BuildAutoRenamePath(requestedPath)
        };
    }

    private static bool DeleteExistingAndReuse(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildAutoRenamePath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var extension = Path.GetExtension(path);
        var baseName = string.IsNullOrWhiteSpace(extension)
            ? Path.GetFileName(path)
            : Path.GetFileNameWithoutExtension(path);

        for (var i = 1; i < 10000; i++)
        {
            var candidateName = $"{baseName} ({i}){extension}";
            var candidate = Path.Combine(directory, candidateName);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return path;
    }

    /// <summary>
    /// 최근 입력 경로 목록을 최신순으로 갱신하고 최대 개수를 유지합니다.
    /// </summary>
    /// <param name="inputPaths">추가할 입력 경로 목록</param>
    private void AddRecentInputs(IEnumerable<string> inputPaths)
    {
        foreach (var path in inputPaths.Reverse())
        {
            _recentInputPaths.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
            _recentInputPaths.Insert(0, path);
        }

        if (_recentInputPaths.Count > 10)
        {
            _recentInputPaths.RemoveRange(10, _recentInputPaths.Count - 10);
        }

        SaveUserSettings();
    }

    /// <summary>
    /// Core 버전 차이를 흡수하기 위해 실패 엔트리 목록을 reflection으로 읽어옵니다.
    /// </summary>
    /// <param name="summary">압축 결과 요약</param>
    /// <returns>실패 엔트리명 목록</returns>
    private static IReadOnlyList<string> GetFailedEntryNamesCompat(CompressionSummary summary)
    {
        if (FailedEntryNamesProperty?.GetValue(summary) is IEnumerable<string> values)
        {
            return values.ToList();
        }

        return Array.Empty<string>();
    }

    private enum InputImageLocation
    {
        FileSystem,
        ZipEntry
    }

    private readonly record struct InputScanResult(
        List<InputImageItem> Items,
        long TotalBytes,
        long ImageBytes,
        long NonImageBytes);

    private sealed class InputImageItem(
        string displayPath,
        string dimensionsText,
        string sourceType,
        long sizeBytes,
        InputImageLocation location,
        string sourcePath,
        string zipEntryPath)
    {
        public string DisplayPath { get; } = displayPath;
        public string DimensionsText { get; } = dimensionsText;
        public string SourceType { get; } = sourceType;
        public long SizeBytes { get; } = sizeBytes;
        public string SizeText => FormatBytes(SizeBytes);
        public InputImageLocation Location { get; } = location;
        public string SourcePath { get; } = sourcePath;
        public string ZipEntryPath { get; } = zipEntryPath;
    }

    private sealed class UserSettings
    {
        public CompressionOptions? CompressionOptions { get; set; }
        public OutputMode OutputMode { get; set; } = OutputMode.Zip;
        public string OutputSuffix { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public OutputConflictMode OutputConflictMode { get; set; } = OutputConflictMode.AutoRename;
        public List<string>? RecentInputs { get; set; }
        public List<SavedPreset>? Presets { get; set; }
    }

    private readonly record struct SavedPreset(
        string Name,
        CompressionOptions Options,
        OutputMode OutputMode,
        string OutputSuffix,
        OutputConflictMode ConflictMode);

    /// <summary>
    /// 저장된 언어 코드가 유효한지 확인하고 기본값을 보정합니다.
    /// </summary>
    /// <param name="persistedLanguageCode">저장된 언어 코드</param>
    /// <returns>최종 적용 언어 코드(ko/en)</returns>
    private static string ResolveLanguageCode(string? persistedLanguageCode)
    {
        if (string.Equals(persistedLanguageCode, "ko", StringComparison.OrdinalIgnoreCase))
        {
            return "ko";
        }

        if (string.Equals(persistedLanguageCode, "en", StringComparison.OrdinalIgnoreCase))
        {
            return "en";
        }

        return LocalizationManager.GetSystemDefaultLanguageCode();
    }

    /// <summary>
    /// 로그 뷰에 타임스탬프/색상 강조를 포함한 메시지를 추가합니다.
    /// </summary>
    /// <param name="message">로그 메시지</param>
    /// <param name="severity">로그 심각도</param>
    private void AppendLog(string message, LogSeverity severity = LogSeverity.Info)
    {
        var timeRun = new Run($"[{DateTime.Now:HH:mm:ss}] ")
        {
            Foreground = Brushes.LightSteelBlue
        };
        var messageRun = new Run(message)
        {
            Foreground = severity switch
            {
                LogSeverity.Success => new SolidColorBrush(Color.FromRgb(110, 231, 183)),
                LogSeverity.Error => new SolidColorBrush(Color.FromRgb(252, 165, 165)),
                _ => new SolidColorBrush(Color.FromRgb(220, 230, 255))
            }
        };

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0)
        };
        paragraph.Inlines.Add(timeRun);
        paragraph.Inlines.Add(messageRun);
        LogTextBox.Document.Blocks.Add(paragraph);
        LogTextBox.ScrollToEnd();
    }

    private enum LogSeverity
    {
        Info,
        Success,
        Error
    }

    /// <summary>
    /// 바이트 수를 사람이 읽기 쉬운 단위 문자열로 변환합니다.
    /// </summary>
    /// <param name="bytes">원본 바이트 값</param>
    /// <returns>포맷된 문자열</returns>
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}