using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Atlassian.Jira;

namespace migrateJiraToVSTS
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var jiraClient = new JiraClient("URI", "login", "pass");

            var tfsClient = new TfsClient(Guid.Empty, "URI", "");

            Console.WriteLine("--------Epics---------");
            var epicIssues = jiraClient.GetEpicIssues("SCP", take: Int32.MaxValue);
            await tfsClient.Migration(epicIssues);

            Console.WriteLine("--------Stories---------");
            var storyIssues = jiraClient.GetStoryIssues("SCP", 0, Int32.MaxValue);

            await tfsClient.Migration(storyIssues);

            Console.WriteLine("--------Tasks---------");
            var taskIssues = jiraClient.GetTaskIssues("SCP", 0, Int32.MaxValue);
            await tfsClient.Migration(taskIssues);

            Console.WriteLine("--------SubTasks---------");
            var subTaskIssues = jiraClient.GetSubTaskIssues("SCP", 0, Int32.MaxValue);
            await tfsClient.Migration(subTaskIssues);

            Console.WriteLine("--------Bags---------");
            var bagIssues = jiraClient.GetBagIssues("SCP", 0, Int32.MaxValue);
            await tfsClient.Migration(bagIssues);
        }
    }
}
