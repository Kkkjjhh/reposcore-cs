using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cocona;
using RepoScore.Data;
using RepoScore.Services;

var app = CoconaApp.Create();

app.AddCommand((
    [Argument(Description = "대상 저장소 (예: owner/repo)")] string repo,
    [Option(Description = "GitHub Token (미입력시 GITHUB_TOKEN 사용)")] string? token = null,
    [Option(Description = "최근 이슈 선점 현황 조회 (issue|user)")] string? claims = null,
    [Option(Description = "출력 형식 (csv, txt)")] string format = "csv",
    [Option(Description = "출력 디렉토리 경로")] string output = "./results",
    [Option(Description = "정렬 기준 (score | id)")] string sortBy = "score",
    [Option(Description = "정렬 방법 (asc | desc)")] string sortOrder = "desc",
    [Option(Description = "이슈 선점 키워드 (쉼표 구분)")] string? keywords = null
) =>
{
    token ??= Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    if (string.IsNullOrEmpty(token)) { Console.Error.WriteLine("오류: GitHub 토큰이 필요합니다."); return; }

    var parts = repo.Split('/');
    if (parts.Length != 2) { Console.Error.WriteLine("오류: 저장소 이름은 'owner/repo' 형식이어야 합니다."); return; }

    string ownerName = parts[0];
    string repoName = parts[1];

    string[]? parsedKeywords = keywords != null
        ? keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : null;

    var service = new GitHubService(ownerName, repoName, token, parsedKeywords);

    try
    {
        if (claims != null)
        {
            Console.Error.WriteLine($"[{ownerName}/{repoName}] 최근 이슈 선점 현황을 조회합니다...");
            var mode = string.IsNullOrEmpty(claims) ? "issue" : claims;

            var claimsData = service.GetRecentClaimsData();
            var report = BuildClaimsReport(claimsData, mode);
            Console.Write(report);
            return;
        }

        Console.Error.WriteLine($"{repo} 기여자 데이터 수집 및 분석 중...");

        if (!Directory.Exists(output)) Directory.CreateDirectory(output);
        string cachePath = Path.Combine(output, "cache.json");
        var cache = CacheManager.LoadCache(cachePath, repo);

        DateTimeOffset? since = cache.LastAnalyzedAt > DateTimeOffset.MinValue ? cache.LastAnalyzedAt : null;

        if (since.HasValue) Console.Error.WriteLine($"기존 캐시 존재: {since.Value.ToLocalTime():yyyy-MM-dd HH:mm}");
        else Console.Error.WriteLine("기존 캐시 없음: 전체 데이터를 수집합니다.");

        List<string> contributors = service.GetAllContributors();
        if (contributors.Count == 0) { Console.Error.WriteLine("조회된 기여자가 없습니다."); return; }

        var reportData = new List<(string Id, int docIssues, int featBugIssues, int typoPrs, int docPrs, int featBugPrs, int Score)>();

        foreach (var user in contributors)
        {
            var newClaims = service.GetClaims(user, since);
            var newPrs = service.GetPullRequests(user, since);

            if (!cache.UserClaims.ContainsKey(user)) cache.UserClaims[user] = new List<ClaimRecord>();
            if (!cache.UserPullRequests.ContainsKey(user)) cache.UserPullRequests[user] = new List<PRRecord>();

            foreach (var nc in newClaims)
            {
                int index = cache.UserClaims[user].FindIndex(c => c.Number == nc.Number);
                if (index >= 0) cache.UserClaims[user][index] = nc;
                else cache.UserClaims[user].Add(nc);
            }

            foreach (var npr in newPrs)
            {
                int index = cache.UserPullRequests[user].FindIndex(p => p.Number == npr.Number);
                if (index >= 0) cache.UserPullRequests[user][index] = npr;
                else cache.UserPullRequests[user].Add(npr);
            }

            var userClaimsToCalc = cache.UserClaims[user]
                .Where(c => c.ClosedReason != IssueClosedStateReason.NotPlanned && c.ClosedReason != IssueClosedStateReason.Duplicate)
                .ToList();

            var prsToCalc = cache.UserPullRequests[user]
                .Where(p => p.IsMerged)
                .ToList();

            var featureBugPrs = prsToCalc.Where(p => p.Labels.Contains(GitHubIssuePrLabel.Bug) || p.Labels.Contains(GitHubIssuePrLabel.Enhancement)).ToList();
            var docPrs = prsToCalc.Where(p => p.Labels.Contains(GitHubIssuePrLabel.Documentation)).ToList();
            var typoPrs = prsToCalc.Where(p => p.Labels.Contains(GitHubIssuePrLabel.Typo)).ToList();
            var featureBugIssues = userClaimsToCalc.Where(c => c.Labels.Contains(GitHubIssuePrLabel.Bug) || c.Labels.Contains(GitHubIssuePrLabel.Enhancement)).ToList();
            var docIssues = userClaimsToCalc.Where(c => c.Labels.Contains(GitHubIssuePrLabel.Documentation)).ToList();

            int finalScore = ScoreCalculator.CalculateFinalScore(featureBugPrs.Count, docPrs.Count, typoPrs.Count, featureBugIssues.Count, docIssues.Count);

            reportData.Add((user, docIssues.Count, featureBugIssues.Count, typoPrs.Count, docPrs.Count, featureBugPrs.Count, finalScore));
        }

        CacheManager.SaveCache(cachePath, cache);
        Console.Error.WriteLine($"캐시 갱신 및 저장 완료: {cachePath}");

        reportData = SortReportData(reportData, sortBy, sortOrder);

        var csv = new StringBuilder();
        csv.AppendLine("아이디, 문서이슈, 버그/기능이슈, 오타PR, 문서PR, 버그/기능PR, 총점");
        foreach (var r in reportData) csv.AppendLine($"{r.Id}, {r.docIssues}, {r.featBugIssues}, {r.typoPrs}, {r.docPrs}, {r.featBugPrs}, {r.Score}");

        string csvPath = Path.Combine(output, "results.csv");
        File.WriteAllText(csvPath, csv.ToString(), Encoding.UTF8);
        Console.Error.WriteLine($"기본 데이터(CSV) 저장 완료: {csvPath}");

        if (format.ToLower() == "txt")
        {
            string txtPath = Path.Combine(output, "results.txt");
            string txtContent = BuildTextReport(repo, reportData);

            File.WriteAllText(txtPath, txtContent, Encoding.UTF8);
            Console.Error.WriteLine($"가독성 리포트(TXT) 추가 저장 완료: {txtPath}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"데이터 처리 중 오류 발생: {ex.Message}");
    }
});

app.Run();

static List<(string Id, int docIssues, int featBugIssues, int typoPrs, int docPrs, int featBugPrs, int Score)>
SortReportData(List<(string Id, int docIssues, int featBugIssues, int typoPrs, int docPrs, int featBugPrs, int Score)> data, string sortBy, string sortOrder)
{
    var sorted = sortBy.ToLower() switch
    {
        "score" => sortOrder.ToLower() == "asc" ? data.OrderBy(x => x.Score).ToList() : data.OrderByDescending(x => x.Score).ToList(),
        "id" => sortOrder.ToLower() == "asc" ? data.OrderBy(x => x.Id).ToList() : data.OrderByDescending(x => x.Id).ToList(),
        _ => sortOrder.ToLower() == "asc" ? data.OrderBy(x => x.Score).ToList() : data.OrderByDescending(x => x.Score).ToList()
    };
    return sorted;
}

static string BuildTextReport(string repo, List<(string Id, int docIssues, int featBugIssues, int typoPrs, int docPrs, int featBugPrs, int Score)> reportData)
{
    var rows = reportData.Select(r => new { Id = r.Id, IssuePr = $"{r.docIssues + r.featBugIssues}/{r.typoPrs + r.docPrs + r.featBugPrs}", Score = r.Score.ToString() }).ToList();
    string userHeader = "유저", issuePrHeader = "이슈/PR", scoreHeader = "점수";
    int userWidth = Math.Max(userHeader.Length, rows.Any() ? rows.Max(x => x.Id.Length) : 0);
    int issuePrWidth = Math.Max(issuePrHeader.Length, rows.Any() ? rows.Max(x => x.IssuePr.Length) : 0);
    int scoreWidth = Math.Max(scoreHeader.Length, rows.Any() ? rows.Max(x => x.Score.Length) : 0);
    string separator = new string('-', userWidth) + "-+-" + new string('-', issuePrWidth) + "-+-" + new string('-', scoreWidth);
    var sb = new StringBuilder();
    sb.AppendLine($"=== {repo} 오픈소스 기여도 분석 리포트 ===");
    sb.AppendLine($"분석 일시: {DateTime.Now:yyyy-MM-dd HH:mm}\n");
    sb.AppendLine(PadRightKorean(userHeader, userWidth) + " | " + PadLeft(issuePrHeader, issuePrWidth) + " | " + PadLeft(scoreHeader, scoreWidth));
    sb.AppendLine(separator);
    foreach (var row in rows) sb.AppendLine(PadRightKorean(row.Id, userWidth) + " | " + PadLeft(row.IssuePr, issuePrWidth) + " | " + PadLeft(row.Score, scoreWidth));
    return sb.ToString();
}

static string BuildClaimsReport(ClaimsData data, string mode)
{
    var sb = new StringBuilder();
    if (data.ClaimedMap.Count == 0) { sb.AppendLine("최근 48시간 내 선점된 이슈가 없습니다."); return sb.ToString(); }
    sb.AppendLine("미선점 이슈");
    foreach (var url in data.UnclaimedUrls) sb.AppendLine($" - {url}");
    sb.AppendLine("\n선점된 이슈");
    foreach (var (login, claims) in data.ClaimedMap)
    {
        sb.AppendLine($"{login}");
        foreach (var claim in claims)
        {
            sb.AppendLine($" - {claim.Url}");
            if (claim.Labels.Count > 0) sb.AppendLine($"    라벨: {string.Join(", ", claim.Labels)}");
            sb.AppendLine(claim.HasPr ? "    PR 생성됨" : FormatRemainingTime(claim.Remaining));
        }
    }
    return sb.ToString();
}

static string PadLeft(string text, int width) => text.PadLeft(width);
static string PadRightKorean(string text, int width) { int textWidth = GetDisplayWidth(text); return textWidth >= width ? text : text + new string(' ', width - textWidth); }
static int GetDisplayWidth(string text) { int width = 0; foreach (char c in text) width += c > 127 ? 2 : 1; return width; }
static string FormatRemainingTime(TimeSpan remaining) => remaining <= TimeSpan.Zero ? "    기한 초과" : $"    남은 시간: {(int)remaining.TotalHours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
