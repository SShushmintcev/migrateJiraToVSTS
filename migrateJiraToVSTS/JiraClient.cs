using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Atlassian.Jira;

namespace migrateJiraToVSTS
{
    public class JiraClient
    {
        private readonly Jira _client;

        public JiraClient(string url, string login, string pass)
        {
            _client = Jira.CreateRestClient(url, login, pass);
        }

        public List<Issue> GetAllIssues(string projectKey, int skip = 0, int take = 100)
        {
            return GetIssues(projectKey, skip, take).ToList();
        }

        public List<Issue> GetEpicIssues(string projectKey, int skip = 0, int take = 100)
        {
            return GetIssues(projectKey, skip, take).Where(i => i.Type == "Epic").ToList();
        }

        public List<Issue> GetStoryIssues(string projectKey, int skip = 0, int take = 100)
        {
            return GetIssues(projectKey, skip, take).Where(i => i.Type == "Story").ToList();
        }

        public List<Issue> GetTaskIssues(string projectKey, int skip = 0, int take = 100)
        {
            return GetIssues(projectKey, skip, take).Where(i => i.Type == "Task").ToList();
        }

        public List<Issue> GetSubTaskIssues(string projectKey, int skip = 0, int take = 100)
        {
            return GetIssues(projectKey, skip, take).Where(i => i.Type == "Sub-task").ToList();
        }

        public List<Issue> GetBagIssues(string projectKey, int skip = 0, int take = 100)
        {
            return GetIssues(projectKey, skip, take).Where(i => i.Type == "Bug").ToList();
        }

        private IQueryable<Issue> GetIssues(string projectKey, int skip = 0, int take = 100)
        {
            return _client.Issues.Queryable
                .Where(p => p.Project == projectKey)
                .OrderBy(i => i.Created)
                .Skip(skip)
                .Take(take);
        }

        public Task<Issue> GetIssueAsync(string key)
        {
            return _client.Issues.GetIssueAsync(key);
        }

        public async Task<List<IssueType>> GetIssueTypesAsync(string projectKey)
        {
            var issueTypesAsync = await _client.IssueTypes.GetIssueTypesForProjectAsync(projectKey);
            return issueTypesAsync.ToList();
        }

        public async Task<List<Project>> GetProjectsAsync()
        {
            var projectsAsync = await _client.Projects.GetProjectsAsync();
            return projectsAsync.ToList();
        }

        public Task<Project> GetProjectAsync(string projectKey)
        {
            return _client.Projects.GetProjectAsync(projectKey);
        }
    }
}