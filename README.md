# ImageCompression

WPF 기반의 Windows 이미지 압축 도구입니다.  
ZIP/폴더/개별 이미지 입력을 지원하며, 다중 ZIP을 한 번에 넣어도 각 ZIP을 개별 출력으로 처리합니다.

## 주요 기능

- ZIP, 폴더, 이미지 파일(`jpg`, `png`, `webp`, `bmp`, `gif`, `tif`) 입력 지원
- 드래그 앤 드롭 + 파일/폴더 선택 UI
- JPEG/WebP/PNG 품질 및 리사이즈 옵션
- 자동 품질 탐색(SSIM/PSNR), 포맷 자동 선택
- 메타데이터 제거, JPEG 4:2:0, Progressive JPEG 옵션
- 충돌 처리(자동 이름 변경/덮어쓰기/건너뛰기)
- 프리셋 저장/적용, 최근 입력 경로
- 한국어/영어 런타임 전환(설정 저장)
- 진행률 표시(`n/total`) 및 실패 항목 필터

## 기술 스택

- .NET 8 (`global.json`으로 SDK 고정)
- C#
- WPF (`ImageCompression.Wpf`)
- ImageSharp (`SixLabors.ImageSharp`)

## 프로젝트 구조

- `ImageCompression.Core/` : 압축 엔진, ZIP 처리, 옵션/요약 모델
- `ImageCompression.Wpf/` : 데스크톱 UI, 설정/미리보기/로컬라이징
- `build_publish.ps1` : 배포 스크립트
- `build_publish.cmd` : 원클릭 배포(일반 + Small)

## 개발 환경에서 실행

```powershell
dotnet build ImageCompression.sln -c Release
dotnet run --project .\ImageCompression.Wpf\ImageCompression.Wpf.csproj
```

## 배포(원클릭)

### 가장 쉬운 방법 (권장)

아래 파일을 더블클릭하거나 터미널에서 실행:

```bat
build_publish.cmd
```

동작:
1. 일반(Self-contained) 버전 빌드 + ZIP
2. Small 버전 빌드 + ZIP

실패 시 콘솔이 바로 닫히지 않도록 `pause`가 포함되어 있습니다.

### PowerShell 직접 실행

일반 버전:

```powershell
.\build_publish.ps1
```

Small 버전:

```powershell
.\build_publish.ps1 -Small
```

버전 포함 배포:

```powershell
.\build_publish.ps1 -Version "v1.0.0"
.\build_publish.ps1 -Version "v1.0.0" -Small
```

주요 옵션:

- `-Runtime win-x64` : 대상 런타임 지정
- `-FrameworkDependent` : 런타임 미포함 배포
- `-NoZip` : ZIP 압축 생략
- `-AppName "Image Compression"` : 최종 exe/zip 이름 지정
- `-Version "v1.0.0"` : 출력 폴더/ZIP 이름에 버전 문자열 포함
- `-Small` : 용량 최적화 모드

## 배포 결과물 위치

- 일반 폴더: `publish/win-x64`
- Small 폴더: `publish/win-x64-small`
- 일반 ZIP: `publish/Image Compression-win-x64.zip`
- Small ZIP: `publish/Image Compression-win-x64-small.zip`
- 버전 지정 예시:
  - `publish/Image Compression-win-x64-v1.0.0.zip`
  - `publish/Image Compression-win-x64-v1.0.0-small.zip`

## 라이선스

`LICENSE` 파일을 참고하세요.
