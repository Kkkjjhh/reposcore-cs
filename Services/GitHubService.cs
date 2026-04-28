using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Octokit;
using Octokit.GraphQL;
using Octokit.GraphQL.Model;

namespace RepoScore.Services
{
    public enum GitHubIssuePrLabel { None, Bug, Documentation, Duplicate, Enhancement, GoodFirstIssue, HelpWanted, Invalid, Pinned, Question, Typo, Wontfix }
    public enum IssueClosedStateReason { None, Completed, Duplicate, NotPlanned }

    public class ClaimRecord
    {
        public int Number { get; set; }
        public string Url { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool HasPr { get; set; }
        public IssueClosedStateReason ClosedReason { get; set; } = IssueClosedStateReason.None;
        public TimeSpan Remaining { get; set; }
        public List<GitHubIssuePrLabel> Labels { get; set; } = new();
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public class ClaimsData
    {
        public Dictionary<string, List<ClaimRecord>> ClaimedMap { get; set; } = new();
        public List<string> UnclaimedUrls { get; set; } = new();
    }

    public class PRRecord
    {
        public int Number { get; set; }
        public string Url { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool IsMerged { get; set; } = false;
        public List<GitHubIssuePrLabel> Labels { get; set; } = new();
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public class GitHubService
    {
        private readonly Octokit.GraphQL.Connection _graphQLConnection;
        private readonly Octokit.GitHubClient _restClient;
        private readonly string _owner;
        private readonly string _repo;
        private static readonly string[] s_defaultClaimKeywords = ["제가 하겠습니다", "진행하겠습니다", "할게요", "I'll take this"];
        private readonly string[] _claimKeywords;

        public GitHubService(string owner, string repo, string? token = null, string[]? keywords = null)
        {
            _owner = owner;
            _repo = repo;
            var actualToken = token ?? global::System.Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                              ?? throw new ArgumentNullException(nameof(token), "GitHub 토큰이 제공되지 않았습니다.");

            _claimKeywords = keywords ?? s_defaultClaimKeywords;
            _graphQLConnection = new Octokit.GraphQL.Connection(new Octokit.GraphQL.ProductHeaderValue("reposcore-cs"), actualToken);
            _restClient = new Octokit.GitHubClient(new Octokit.ProductHeaderValue("reposcore-cs")) { Credentials = new Octokit.Credentials(actualToken) };
        }

        public List<PRRecord> GetPullRequests(string authorLogin, DateTimeOffset? since = null)
        {
            string searchString = $"repo:{_owner}/{_repo} is:pr author:{authorLogin}";
            if (since.HasValue) searchString += $" updated:>={since.Value.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}";
            var prRecords = new List<PRRecord>();
            string? endCursor = null;
            bool hasNextPage = true;
            while (hasNextPage)
            {
                var query = new Octokit.GraphQL.Query()
                    .Search(query: searchString, type: SearchType.Issue, first: 100, after: endCursor)
                    .Select(search => new { search.PageInfo.HasNextPage, search.PageInfo.EndCursor, Nodes = search.Nodes.OfType<Octokit.GraphQL.Model.PullRequest>().Select(pr => new { pr.Number, pr.Title, pr.Url, pr.Merged, pr.UpdatedAt, Labels = pr.Labels(10, null, null, null, null).Nodes.Select(l => l.Name).ToList() }).ToList() });
                var result = _graphQLConnection.Run(query).Result;
                foreach (var pr in result.Nodes)
                {
                    prRecords.Add(new PRRecord { Number = pr.Number, Title = pr.Title, Url = pr.Url, IsMerged = pr.Merged, UpdatedAt = pr.UpdatedAt, Labels = pr.Labels.Select(ParseGitHubLabel).Where(l => l != GitHubIssuePrLabel.None).ToList() });
                }
                hasNextPage = result.HasNextPage; endCursor = result.EndCursor;
                if (result.Nodes.Count == 0) break;
            }
            return prRecords;
        }

        public List<ClaimRecord> GetClaims(string authorLogin, DateTimeOffset? since = null)
        {
            string searchString = $"repo:{_owner}/{_repo} is:issue author:{authorLogin}";
            if (since.HasValue) searchString += $" updated:>={since.Value.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}";
            var claimRecords = new List<ClaimRecord>();
            string? endCursor = null;
            bool hasNextPage = true;
            while (hasNextPage)
            {
                var query = new Octokit.GraphQL.Query()
                    .Search(query: searchString, type: SearchType.Issue, first: 100, after: endCursor)
                    .Select(search => new { search.PageInfo.HasNextPage, search.PageInfo.EndCursor, Nodes = search.Nodes.OfType<Octokit.GraphQL.Model.Issue>().Select(i => new { i.Number, i.Title, i.Url, i.UpdatedAt, Labels = i.Labels(10, null, null, null, null).Nodes.Select(l => l.Name).ToList() }).ToList() });
                var result = _graphQLConnection.Run(query).Result;
                foreach (var issue in result.Nodes)
                {
                    claimRecords.Add(new ClaimRecord { Number = issue.Number, Title = issue.Title, Url = issue.Url, UpdatedAt = issue.UpdatedAt, Labels = issue.Labels.Select(ParseGitHubLabel).Where(l => l != GitHubIssuePrLabel.None).ToList() });
                }
                hasNextPage = result.HasNextPage; endCursor = result.EndCursor;
                if (result.Nodes.Count == 0) break;
            }
            return claimRecords;
        }

        public List<string> GetAllContributors()
        {
            try { return _restClient.Repository.GetAllContributors(_owner, _repo).Result.Select(c => c.Login).ToList(); }
            catch { return new List<string>(); }
        }

        private bool HasLinkedPullRequest(int issueNumber)
        {
            try { var query = new Octokit.GraphQL.Query().Repository(_owner, _repo).Issue(issueNumber).TimelineItems(first: 50, null, null, null, null).Nodes.OfType<CrossReferencedEvent>().Select(e => e.Url); return _graphQLConnection.Run(query).Result.Any(url => url != null && url.Contains("/pull/")); }
            catch { return false; }
        }

        private static bool IsDocumentTask(List<GitHubIssuePrLabel> issueLabels) => issueLabels.Contains(GitHubIssuePrLabel.Documentation) || issueLabels.Contains(GitHubIssuePrLabel.Typo);

        private static GitHubIssuePrLabel ParseGitHubLabel(string labelName)
        {
            var n = labelName.ToLowerInvariant().Replace(" ", "").Replace("-", "");
            return n switch { "bug" => GitHubIssuePrLabel.Bug, "documentation" => GitHubIssuePrLabel.Documentation, "enhancement" => GitHubIssuePrLabel.Enhancement, "typo" => GitHubIssuePrLabel.Typo, _ => GitHubIssuePrLabel.None };
        }

        public ClaimsData GetRecentClaimsData()
        {
            var query = new Octokit.GraphQL.Query().Repository(_owner, _repo).Issues(first: 20, states: new[] { IssueState.Open }, orderBy: new IssueOrder { Field = IssueOrderField.CreatedAt, Direction = OrderDirection.Desc }).Nodes.Select(i => new { i.Number, i.Url, Labels = i.Labels(10, null, null, null, null).Nodes.Select(l => l.Name).ToList(), Comments = i.Comments(10, null, null, null, null).Nodes.Select(c => new { c.Body, c.CreatedAt, AuthorLogin = c.Author.Login }).ToList() });
            var result = _graphQLConnection.Run(query).Result;
            var now = DateTimeOffset.UtcNow; var claimsData = new ClaimsData();
            foreach (var issue in result)
            {
                var issueLabels = issue.Labels.Select(ParseGitHubLabel).Where(l => l != GitHubIssuePrLabel.None).ToList();
                bool isClaimed = false;
                foreach (var comment in issue.Comments)
                {
                    if ((now - comment.CreatedAt).TotalHours > 48) continue;
                    if (_claimKeywords.Any(k => comment.Body.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    {
                        claimsData.ClaimedMap.TryAdd(comment.AuthorLogin ?? "unknown", new List<ClaimRecord>());
                        claimsData.ClaimedMap[comment.AuthorLogin ?? "unknown"].Add(new ClaimRecord { Number = issue.Number, Url = issue.Url, Remaining = comment.CreatedAt.AddHours(IsDocumentTask(issueLabels) ? 24 : 48) - now, Labels = issueLabels, HasPr = HasLinkedPullRequest(issue.Number) });
                        isClaimed = true; break;
                    }
                }
                if (!isClaimed) claimsData.UnclaimedUrls.Add(issue.Url);
            }
            return claimsData;
        }
    }
}
