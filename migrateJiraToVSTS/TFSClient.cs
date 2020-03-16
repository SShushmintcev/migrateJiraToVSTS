using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Atlassian.Jira;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;

namespace migrateJiraToVSTS
{
    public class TfsClient
    {
        private readonly Guid _projectId;

        private readonly VssConnection _connection;

        private readonly Dictionary<string, int> _parents = new Dictionary<string, int>();

        /// <summary>
        /// Implement tfs client.
        /// </summary>
        /// <param name="projectId">Id project in namespace</param>
        /// <param name="url">https://****.visualstudio.com</param>
        /// <param name="key">AuthToken</param>
        public TfsClient(Guid projectId, string url, string key)
        {
            _projectId = projectId;
            _connection = new VssConnection(new Uri(url), new VssBasicCredential(string.Empty, key));
        }

        public async Task<List<TeamMember>> GetMembersAsync(Guid teamId, int? top = null, int? skip = null)
        {
            var client = await _connection.GetClientAsync<TeamHttpClient>();
            return await client.GetTeamMembersWithExtendedPropertiesAsync(_projectId.ToString(), teamId.ToString(), top, skip);
        }

        public async Task<WorkItemQueryResult> GetWorkItemsByTypeAsync(string type, string projectName)
        {
            var client = await _connection.GetClientAsync<WorkItemTrackingHttpClient>();

            var wiql = new Wiql
            {
                Query = $"SELECT [System.Id], [System.Title] FROM WorkItems WHERE [System.WorkItemType] = '{type}' and [System.TeamProject] = '{projectName}'"
            };

            return await client.QueryByWiqlAsync(wiql, _projectId);
        }

        public async Task<WorkItemClassificationNode> GetClassificationNodeAsync(TreeStructureGroup group)
        {
            var client = await _connection.GetClientAsync<WorkItemTrackingHttpClient>();
            return await client.GetClassificationNodeAsync(_projectId, group, null, 10);
        }

        public async Task<WorkItemClassificationNode> CreateSprintAsync(string name, string parentPath,
            TreeNodeStructureType type = TreeNodeStructureType.Iteration, IDictionary<string, object> attributes = null)
        {
            var node = new WorkItemClassificationNode
            {
                Name = name,
                StructureType = type
            };

            if (!string.IsNullOrEmpty(parentPath))
            {
                node.Path = parentPath;
            }

            if (attributes != null)
            {
                node.Attributes = attributes;
            }

            var group = type == TreeNodeStructureType.Iteration
                ? TreeStructureGroup.Iterations
                : TreeStructureGroup.Areas;

            var workItemClient = await _connection.GetClientAsync<WorkItemTrackingHttpClient>();
            return await workItemClient.CreateOrUpdateClassificationNodeAsync(node, _projectId, group);
        }

        public async Task<WorkItem> GetItemAsync(int id)
        {
            var workItemClient = await _connection.GetClientAsync<WorkItemTrackingHttpClient>();
            return await workItemClient.GetWorkItemAsync(_projectId, id);
        }

        public async Task<TeamProject> GetTeamProjectAsync(Guid projectId)
        {
            var projectClient = await _connection.GetClientAsync<ProjectHttpClient>();
            return await projectClient.GetProject(projectId.ToString());
        }

        public Task<TeamProject> GetCurrentTeamProjectAsync()
        {
            return GetTeamProjectAsync(_projectId);
        }

        public async Task<WorkItem> UpdateAsync(JsonPatchDocument document, int id)
        {
            var client = await _connection.GetClientAsync<WorkItemTrackingHttpClient>();
            return await client.UpdateWorkItemAsync(document, id, bypassRules: true);
        }

        public async Task<CommentList> GetItemCommentsAsync(int id)
        {
            var workItemClient = await _connection.GetClientAsync<WorkItemTrackingHttpClient>();
            return await workItemClient.GetCommentsAsync(_projectId, id);
        }

        public async Task DeleteCommentAsync(int itemId, int commentId)
        {
            var workItemClient = await _connection.GetClientAsync<WorkItemTrackingHttpClient>();
            await workItemClient.DeleteCommentAsync(_projectId, itemId, commentId);
        }

        public async Task DeleteCommentsAsync(int itemId, IEnumerable<int> commentIds)
        {
            var workItemClient = await _connection.GetClientAsync<WorkItemTrackingHttpClient>();

            foreach (var i in commentIds)
            {
                await workItemClient.DeleteCommentAsync(_projectId, itemId, i);
            }
        }

        public async Task Migration(List<Issue> issues)
        {
            var workItemClient = await _connection.GetClientAsync<WorkItemTrackingHttpClient>();

            var project = await GetCurrentTeamProjectAsync();

            var countMigrate = issues.Count;

            Console.WriteLine($"Count for migration: {countMigrate}");

            foreach (var issue in issues)
            {
                var document = await CreateJsonAsync(issue, project);

                var links = await ResolveLinkAsync(issue);

                var parentPath = GetParent(issue);

                if (parentPath != null)
                {
                    document.Add(parentPath);
                }

                var attachments = await CreateAttachmentPathAsync(workItemClient, issue);

                if (attachments.Count > 0)
                {
                    document.AddRange(attachments);
                }

                WorkItem workItem = null;

                try
                {
                    workItem = await workItemClient.CreateWorkItemAsync(document, _projectId, ResolveType(issue.Type));

                    if (issue.Type == "Task" || issue.Type == "Bug" || issue.Type == "Sub-task")
                    {
                        await InnerUpdateStateAsync(workItemClient, workItem.Id.Value, issue);
                    }

                    countMigrate--;
                    Console.WriteLine($"Issue was migrate: Jira:{issue.Key.Value} TFS Id {workItem.Id}. Count {countMigrate}");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error: Item[{issue.Key.Value}] - {e.Message}");
                }

                if (workItem?.Id == null) continue;

                if (string.IsNullOrEmpty(issue.ParentIssueKey))
                {
                    _parents[issue.Key.Value] = workItem.Id.Value;
                }

                await CreateCommentsAsync(workItemClient, workItem.Id.Value, issue);
            }
        }

        public async Task UpdateRepoStepsForBugIssuesAsync(string importTaskPattern, string repoText)
        {
            var project = await GetCurrentTeamProjectAsync();
            var list = await GetWorkItemsByTypeAsync("Bug", project.Name);

            foreach (var reference in list.WorkItems)
            {
                var item = await GetItemAsync(reference.Id);

                Console.WriteLine($"Team project: {item.Fields["System.TeamProject"]} Item Id: {item.Id}. Title: {item.Fields["System.Title"]}");

                if (Regex.IsMatch(item.Fields["System.Title"].ToString(), importTaskPattern))
                {
                    try
                    {
                        var document = new JsonPatchDocument
                        {
                            new JsonPatchOperation
                            {
                                Operation = Operation.Add,
                                Path = "/fields/Microsoft.VSTS.TCM.ReproSteps",
                                Value = repoText
                            }
                        };

                        await UpdateAsync(document, item.Id.Value);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
            }
        }

        private JsonPatchOperation GetParent(Issue issue)
        {
            if (string.IsNullOrEmpty(issue.ParentIssueKey))
            {
                if (!_parents.ContainsKey(issue.Key.Value))
                {
                    _parents.Add(issue.Key.Value, -1);
                }
            }

            if (string.IsNullOrEmpty(issue.ParentIssueKey) || !_parents.ContainsKey(issue.ParentIssueKey)) return null;

            var parent = _parents[issue.ParentIssueKey];

            if (parent != -1)
            {
                return new JsonPatchOperation
                {
                    Path = "/relations/-",
                    Value = new WorkItemRelation
                    {
                        Rel = "System.LinkTypes.Hierarchy-Reverse",
                        Url = $"https://ciappdev.visualstudio.com/_apis/wit/workItems/{parent}"
                    }
                };
            }

            return null;
        }

        private async Task<JsonPatchDocument> CreateJsonAsync(Issue issue, TeamProject project)
        {
            var sprint = issue.CustomFields["Sprint"];

            var document = new JsonPatchDocument
            {
                new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/System.TeamProject",
                    Value = project.Name
                },
                new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/System.AreaPath",
                    Value = project.Name
                },
                new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/System.WorkItemType",
                    Value = ResolveType(issue.Type)
                },
                new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/System.CreatedDate",
                    Value = issue.Created ?? DateTime.Today
                },
                new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/System.CreatedBy",
                    Value = ResolveUser(issue.Reporter)
                },
                new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/System.ChangedBy",
                    Value = ResolveUser(issue.Reporter)
                },
                new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/System.Title",
                    Value = $"{issue.Key.Value}: {issue.Summary}"
                },
                new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/System.Description",
                    Value = issue.Description ?? String.Empty
                },
                new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/Microsoft.VSTS.Common.Priority",
                    Value = ResolvePriority(issue.Priority)
                }
            };

            if (issue.Type == "Task" || issue.Type == "Bug" || issue.Type == "Sub-task")
            {
                document.Add(new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/System.State",
                    Value = "New"
                });
            }

            if (issue.Type == "Bug")
            {
                document.Add(new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/Microsoft.VSTS.TCM.ReproSteps",
                    Value = issue.Description ?? String.Empty
                });
            }

            if (sprint != null)
            {
                var parentNode = await GetClassificationNodeAsync(TreeStructureGroup.Iterations);
                document.Add(new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/System.IterationPath",
                    Value = $"{parentNode.Name}\\{sprint.Values.Last()}"
                });

                if (sprint.Values.Length > 1)
                {
                    document.Add(new JsonPatchOperation
                    {
                        Operation = Operation.Add,
                        Path = "/fields/Microsoft.VSTS.Common.AcceptanceCriteria",
                        Value = $" Migration: {string.Join("; ", sprint.Values)}"
                    });
                }
            }

            if (issue.Updated.HasValue)
            {
                document.Add(new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/System.ChangedDate",
                    Value = issue.Updated
                });
            }

            if (!string.IsNullOrEmpty(issue.Assignee))
            {
                document.Add(new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/System.AssignedTo",
                    Value = ResolveUser(issue.Assignee)
                });
            }

            return document;
        }

        private async Task CreateCommentsAsync(WorkItemTrackingHttpClient client, int id, Issue item)
        {
            var comments = (await item.GetCommentsAsync())
                .Select(p => CreateComment(p.Body, p.Author, p.CreatedDate?.ToUniversalTime()));

            foreach (var comment in comments)
            {
                try
                {
                    await client.UpdateWorkItemAsync(comment, id, bypassRules: true);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error: Add Comments item in jira - {item.Key.Value} in TFS - {id}. Message: {e.Message}");
                }
            }
        }

        private async Task<List<string>> ResolveLinkAsync(Issue item)
        {
            var links = await item.GetIssueLinksAsync();
            var issueLinks = links as IssueLink[] ?? links.ToArray();

            return issueLinks.Any()
                ? issueLinks.Select(i => i.OutwardIssue.Key.Value).ToList()
                : new List<string>(Array.Empty<string>());
        }

        private async Task<JsonPatchDocument> CreateAttachmentPathAsync(WorkItemTrackingHttpClientBase client, Issue item)
        {
            var attachments = await item.GetAttachmentsAsync();
            var document = new JsonPatchDocument();

            foreach (var attachment in attachments)
            {
                var bytes = await attachment.DownloadDataAsync();
                using (var stream = new MemoryStream(bytes))
                {
                    var uploaded = await client.CreateAttachmentAsync(stream, _projectId, attachment.FileName);
                    document.Add(new JsonPatchOperation
                    {
                        Path = "/relations/-",
                        Value = new WorkItemRelation { Rel = "AttachedFile", Url = uploaded.Url }
                    });
                }
            }

            return document;
        }

        private JsonPatchDocument CreateComment(string comment, string username = null, DateTime? date = null)
        {
            var patch = new JsonPatchDocument
            {
                new JsonPatchOperation
                {
                    Path = "/fields/System.History",
                    Value = $"<b>[Migration (Author: {username}; Date: {date})]:</b> {comment}"
                }
            };

            if (username != null)
                patch.Add(new JsonPatchOperation { Path = "/fields/System.CreatedBy", Value = ResolveUser(username) });

            if (date.HasValue)
                patch.Add(new JsonPatchOperation { Path = "/fields/System.CreatedDate", Value = date.Value.ToUniversalTime() });

            return patch;
        }

        private async Task InnerUpdateStateAsync(WorkItemTrackingHttpClientBase client, int id, Issue item)
        {
            var document = new JsonPatchDocument
            {
                new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = "/fields/System.State",
                    Value = ResolveState(item)
                }
            };

            try
            {
                await client.UpdateWorkItemAsync(document, _projectId, id);
                Console.WriteLine($"Update state TFS Id: {id}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error: Update state TFS Id: {id}. Message: {e.Message}");
            }
        }

        private string ResolveState(Issue issue)
        {
            switch (issue.Status.Id)
            {
                // Open
                case "1": return "New";
                // In Progress
                case "3": return "Active";
                // Reopened
                case "4": return "Reopened";
                // Resolved
                case "5": return "Resolved";
                // Closed
                case "6": return "Closed";
                // TO DO
                case "10000": return "New";
                // Done
                case "10001":
                    return (issue.Type == "Task" || issue.Type == "Sub-task") ? "Closed" : "Resolved";
                // In Testing
                case "10101": return "In Testing";
                default: throw new ArgumentException("Could not find state", nameof(issue.Status.Id));
            }
        }

        private string ResolveUser(string user)
        {
            switch (user)
            {
                case "": return "";
                default: throw new ArgumentException("Could not find user", nameof(user));
            }
        }

        private int ResolvePriority(IssuePriority priority)
        {
            switch (priority.Name)
            {
                case "Highest": return 1;
                case "High": return 2;
                case "Medium": return 3;
                case "Low":
                case "Lowest":
                default: return 4;
            }
        }

        private string ResolveType(IssueType type)
        {
            switch (type.Name)
            {
                case "Epic": return "Epic";
                case "Story": return "User Story";
                case "Task":
                case "Sub-task": return "Task";
                case "Bug": return "Bug";
                default: throw new ArgumentException("Could not find type", nameof(type));
            }
        }
    }
}