using Cocona;
using RepoScore.Data;

var app = CoconaApp.Create();

app.AddCommand(async (
    [Argument] string repo,
    [Option('t', Description = "GitHub Personal Access Token")] string? token = null,
    [Option("show-claims", Description = "이슈 선점 출력 방식 (user | issue)")] string? showClaims = null
) =>
{
    if (showClaims != null)
    {
        var service = new IssueService();

        var mode = showClaims.ToLower();
        if (string.IsNullOrEmpty(mode))
            mode = "user";

        await service.ShowRecentClaims(repo, token, mode);
        return;
    }

    Console.WriteLine($"저장소: {repo}");

    if (!string.IsNullOrEmpty(token))
    {
        Console.WriteLine($"토큰 인증 사용 중 (토큰: {token[..Math.Min(4, token.Length)]}***)");
    }
    else
    {
        Console.WriteLine("토큰 미입력 - 비인증 모드로 실행");
    }

    Console.WriteLine();
    Console.WriteLine("아이디, 문서이슈, 버그/기능이슈, 오타PR, 문서PR, 버그/기능PR, 총점");

    int user1Score = ScoreCalculator.CalculateFinalScore(1, 3, 1, 2, 1);
    Console.WriteLine($"user1, 1, 2, 1, 3, 1, {user1Score}");

    int user2Score = ScoreCalculator.CalculateFinalScore(2, 3, 5, 2, 1);
    Console.WriteLine($"user2, 1, 2, 5, 3, 2, {user2Score}");

    int user3Score = ScoreCalculator.CalculateFinalScore(5, 6, 5, 2, 3);
    Console.WriteLine($"user3, 3, 2, 5, 6, 5, {user3Score}");
});

app.Run();
