using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

public class IssueService
{
    private readonly HttpClient _httpClient;

    public IssueService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("RepoScoreApp", "1.0"));
    }

    public async Task ShowRecentClaims(string repo, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        }

        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("GitHub 토큰이 필요합니다.");
            return;
        }

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var parts = repo.Split('/');
        if (parts.Length != 2)
        {
            Console.WriteLine("repo 형식은 owner/repo 입니다.");
            return;
        }

        var owner = parts[0];
        var name = parts[1];

        var query = @"
        query($owner: String!, $name: String!) {
          repository(owner: $owner, name: $name) {
            issues(first: 20, states: OPEN, orderBy: {field: CREATED_AT, direction: DESC}) {
              nodes {
                title
                url
                comments(last: 10) {
                  nodes {
                    body
                    createdAt
                    author {
                      login
                    }
                  }
                }
              }
            }
          }
        }";

        var requestObj = new
        {
            query = query,
            variables = new { owner = owner, name = name }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(requestObj),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.PostAsync("https://api.github.com/graphql", content);
        var json = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);

        var issues = doc.RootElement
            .GetProperty("data")
            .GetProperty("repository")
            .GetProperty("issues")
            .GetProperty("nodes");

        Console.WriteLine("📌 최근 이슈 선점 현황\n");

        var keywords = new[] { "제가 하겠습니다", "진행하겠습니다", "할게요", "I'll take this" };
        var now = DateTime.UtcNow;

        var result = new Dictionary<string, List<string>>();

        foreach (var issue in issues.EnumerateArray())
        {
            var url = issue.GetProperty("url").GetString();
            var comments = issue.GetProperty("comments").GetProperty("nodes");

            foreach (var comment in comments.EnumerateArray())
            {
                var body = comment.GetProperty("body").GetString();
                var createdAt = comment.GetProperty("createdAt").GetDateTime();
                var author = comment.GetProperty("author").GetProperty("login").GetString();

                if ((now - createdAt).TotalHours <= 48)
                {
                    foreach (var keyword in keywords)
                    {
                        if (body != null && body.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!result.ContainsKey(author))
                                result[author] = new List<string>();

                            result[author].Add(url);
                            break;
                        }
                    }
                }
            }
        }

        foreach (var user in result)
        {
            Console.WriteLine($"👤 {user.Key}");

            foreach (var issue in user.Value.Distinct())
            {
                Console.WriteLine($" - {issue}");
            }

            Console.WriteLine();
        }
    }
}
