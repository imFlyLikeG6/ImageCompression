using System.IO.Compression;
using System.Text;

namespace ImageCompression.Core;

/// <summary>
/// ZIP 엔트리 이름 인코딩을 최대한 복원해서 여는 헬퍼입니다.
/// UTF-8 플래그가 없거나 잘못 저장된 ZIP(다국어 파일명 깨짐) 대응용입니다.
/// </summary>
public static class ZipArchiveEncodingHelper
{
    private static readonly Encoding[] CandidateEncodings =
    [
        Encoding.UTF8,
        Encoding.GetEncoding(949),  // Korean
        Encoding.GetEncoding(932),  // Japanese Shift-JIS
        Encoding.GetEncoding(936),  // Simplified Chinese (GBK)
        Encoding.GetEncoding(950),  // Traditional Chinese (Big5)
        Encoding.GetEncoding(1252), // Western Latin
        Encoding.GetEncoding(1251), // Cyrillic
        Encoding.GetEncoding(866)   // OEM Cyrillic
    ];

    /// <summary>
    /// 가능한 인코딩 후보를 순차 시도해 엔트리명이 가장 자연스러운 ZIP 아카이브를 엽니다.
    /// </summary>
    /// <param name="zipPath">열 대상 ZIP 경로</param>
    /// <returns>읽기 모드로 열린 <see cref="ZipArchive"/></returns>
    /// <exception cref="InvalidDataException">지원 후보 인코딩으로 열 수 없는 경우</exception>
    public static ZipArchive OpenReadBestEffort(string zipPath)
    {
        // zipPath가 잘못된 경우의 예외는 호출 측에서 처리하도록 그대로 전파합니다.
        ZipArchive? bestArchive = null;
        var bestScore = int.MaxValue;

        // 1) 기본 동작(UTF-8 플래그 + 플랫폼 기본 처리)을 먼저 시도.
        TryOpenAndScore(zipPath, entryNameEncoding: null, ref bestArchive, ref bestScore);
        if (bestScore == 0 && bestArchive is not null)
        {
            return bestArchive;
        }

        // 2) 주요 레거시 코드페이지를 순차 시도 후 "덜 깨진" 결과를 채택.
        foreach (var encoding in CandidateEncodings)
        {
            TryOpenAndScore(zipPath, encoding, ref bestArchive, ref bestScore);
            if (bestScore == 0 && bestArchive is not null)
            {
                break;
            }
        }

        if (bestArchive is null)
        {
            throw new InvalidDataException("Unable to open ZIP archive with supported encodings.");
        }

        return bestArchive;
    }

    private static void TryOpenAndScore(string zipPath, Encoding? entryNameEncoding, ref ZipArchive? bestArchive, ref int bestScore)
    {
        try
        {
            var archive = new ZipArchive(
                File.OpenRead(zipPath),
                ZipArchiveMode.Read,
                leaveOpen: false,
                entryNameEncoding: entryNameEncoding);

            var score = CalculateNameQualityScore(archive);
            if (score < bestScore)
            {
                bestArchive?.Dispose();
                bestArchive = archive;
                bestScore = score;
                return;
            }

            archive.Dispose();
        }
        catch
        {
            // 해당 인코딩 후보는 실패로 간주하고 다음 후보를 시도.
        }
    }

    /// <summary>
    /// 엔트리명 품질 점수를 계산합니다. 점수가 낮을수록 사람이 읽기 좋은 이름입니다.
    /// </summary>
    private static int CalculateNameQualityScore(ZipArchive archive)
    {
        var score = 0;
        var inspected = 0;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName))
            {
                continue;
            }

            inspected++;
            var name = entry.FullName;
            var questionMarks = 0;

            foreach (var c in name)
            {
                if (c == '\uFFFD')
                {
                    score += 50;
                }
                else if (c == '?')
                {
                    questionMarks++;
                }
                else if (char.IsControl(c))
                {
                    score += 25;
                }
            }

            if (questionMarks > 0)
            {
                score += questionMarks * 4;
                if (questionMarks * 3 >= name.Length)
                {
                    score += 30;
                }
            }

            if (name.Contains("Ã", StringComparison.Ordinal) ||
                name.Contains("Â", StringComparison.Ordinal))
            {
                score += 10;
            }

            if (inspected >= 80)
            {
                break;
            }
        }

        return score;
    }
}
