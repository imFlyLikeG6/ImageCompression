<# 
    ImageCompression 배포 자동화 스크립트
    - Self-contained / Framework-dependent 배포 모두 지원
    - 단일 실행 파일(PublishSingleFile) 생성
    - small 모드로 용량 최적화 가능
    - 결과물 ZIP 압축 생성(옵션)
#>
param(
    # 대상 런타임 식별자(RID). 예: win-x64, win-arm64
    [string]$Runtime = "win-x64",
    # 빌드 구성. 일반적으로 Release 사용
    [string]$Configuration = "Release",
    # 지정 시 framework-dependent 배포(런타임 미포함, 용량 작음)
    [switch]$FrameworkDependent,
    # 지정 시 결과 폴더 ZIP 압축 생성을 생략
    [switch]$NoZip,
    # 최종 exe 파일명/zip 파일명에 사용할 앱 이름
    [string]$AppName = "Image Compression",
    # 버전 문자열(예: v1.0.0). 지정 시 출력 폴더/ZIP 이름에 반영
    [string]$Version = "",
    # 지정 시 용량 최적화 우선 모드(ReadyToRun 비활성, 단일파일 압축 활성)
    [switch]$Small
)

# 에러가 발생하면 즉시 중단하여 실패 원인을 빠르게 파악합니다.
$ErrorActionPreference = "Stop"

# 명시 경로의 dotnet CLI를 사용해 환경 차이로 인한 오류를 줄입니다.
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    throw "dotnet 실행 파일을 찾을 수 없습니다: $dotnet"
}

function Get-ProjectVersion([string]$projectPath) {
    try {
        [xml]$projectXml = Get-Content -Path $projectPath -Raw -Encoding UTF8
        $versionNode = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
        if ($null -ne $versionNode) {
            $versionText = $versionNode.InnerText
            if (-not [string]::IsNullOrWhiteSpace($versionText)) {
                return $versionText.Trim()
            }
        }
    }
    catch {
        # fallback below
    }

    # 프로젝트 버전이 비어있으면 기본 버전을 사용합니다.
    return "1.0.0"
}

# 배포 대상 프로젝트 및 출력 경로 계산
# 중요: 스크립트를 어떤 현재 경로(CWD)에서 실행하든 동작하도록 절대 경로를 사용합니다.
$project = Join-Path $PSScriptRoot "ImageCompression.Wpf/ImageCompression.Wpf.csproj"
$publishRoot = Join-Path $PSScriptRoot "publish"
$effectiveVersion = if ([string]::IsNullOrWhiteSpace($Version)) { Get-ProjectVersion $project } else { $Version }
$normalizedVersion = $effectiveVersion.Trim()
if (-not [string]::IsNullOrWhiteSpace($normalizedVersion) -and
    $normalizedVersion.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
    throw "Version에 파일명으로 사용할 수 없는 문자가 포함되어 있습니다: $normalizedVersion"
}
$versionSuffix = if ([string]::IsNullOrWhiteSpace($normalizedVersion)) { "" } else { "-$normalizedVersion" }
$smallSuffix = if ($Small) { "-small" } else { "" }
$outputTag = "{0}{1}{2}" -f $Runtime, $versionSuffix, $smallSuffix
$publishDir = Join-Path $publishRoot $outputTag
$zipPath = Join-Path $publishRoot ("{0}-{1}.zip" -f $AppName, $outputTag)
$icoIconPath = Join-Path $PSScriptRoot "ImageCompression.Wpf/Resources/app_icon.ico"
$selfContained = if ($FrameworkDependent) { "false" } else { "true" }
$publishReadyToRun = if ($Small) { "false" } else { "true" }
$enableSingleFileCompression = if ($Small) { "true" } else { "false" }
$defaultExeName = "ImageCompression.Wpf.exe"
$targetExeName = "$AppName.exe"

Write-Host "== ImageCompression publish ==" -ForegroundColor Cyan
Write-Host ("Runtime: {0}" -f $Runtime)
Write-Host ("Configuration: {0}" -f $Configuration)
Write-Host ("Version: {0}" -f $(if ([string]::IsNullOrWhiteSpace($normalizedVersion)) { "(none)" } else { $normalizedVersion }))
Write-Host ("Self-contained: {0}" -f $selfContained)
Write-Host ("Small mode: {0}" -f ($(if ($Small) { "ON" } else { "OFF" })))
Write-Host ("Output: {0}" -f $publishDir)

if (-not (Test-Path $icoIconPath)) {
    throw "아이콘 파일이 없습니다: $icoIconPath"
}

# 기존 출력 폴더가 잠겨 있으면 타임스탬프 폴더로 자동 우회합니다.
if (Test-Path $publishDir) {
    try {
        Remove-Item $publishDir -Recurse -Force
    }
    catch {
        $fallbackDir = Join-Path $publishRoot ("{0}-{1}" -f $outputTag, (Get-Date -Format "yyyyMMdd-HHmmss"))
        Write-Warning ("기존 출력 폴더 잠금 감지. 새 출력 폴더를 사용합니다: {0}" -f $fallbackDir)
        $publishDir = $fallbackDir
        $zipPath = Join-Path $publishRoot ("{0}-{1}.zip" -f $AppName, (Split-Path $publishDir -Leaf))
    }
}

# 실제 publish 실행
# - PublishSingleFile: 단일 실행 파일 생성
# - IncludeNativeLibrariesForSelfExtract: 네이티브 라이브러리 포함
# - PublishReadyToRun/EnableCompressionInSingleFile: 속도-용량 트레이드오프 제어
Push-Location $PSScriptRoot
try {
    # 중요: global.json(루트 고정 SDK)을 확실히 적용하기 위해 스크립트 루트에서 publish를 실행합니다.
    & $dotnet publish $project `
        -c $Configuration `
        -r $Runtime `
        --self-contained $selfContained `
        /p:PublishSingleFile=true `
        /p:IncludeNativeLibrariesForSelfExtract=true `
        /p:PublishReadyToRun=$publishReadyToRun `
        /p:EnableCompressionInSingleFile=$enableSingleFileCompression `
        /p:DebugType=None `
        /p:DebugSymbols=false `
        /p:ApplicationIcon="$icoIconPath" `
        -o $publishDir
}
finally {
    Pop-Location
}

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed. exit code: $LASTEXITCODE"
}

# 기본 exe 이름을 사용자 지정 앱 이름으로 변경합니다.
$defaultExePath = Join-Path $publishDir $defaultExeName
$targetExePath = Join-Path $publishDir $targetExeName
if ((Test-Path $defaultExePath) -and ($defaultExeName -ne $targetExeName)) {
    if (Test-Path $targetExePath) {
        Remove-Item $targetExePath -Force
    }
    Rename-Item $defaultExePath $targetExeName
}

# 필요 시 배포 폴더를 ZIP으로 압축해 배포 전달을 쉽게 만듭니다.
if (-not $NoZip) {
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath
    Write-Host ("ZIP 생성 완료: {0}" -f $zipPath) -ForegroundColor Green
}

Write-Host ("Publish 완료: {0}" -f $publishDir) -ForegroundColor Green
