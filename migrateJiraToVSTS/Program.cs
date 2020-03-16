using System;
using System.Threading.Tasks;

namespace migrateJiraToVSTS
{
    class Program
    {
        private const string TASK_NAME = "";

        static async Task Main(string[] args)
        {
            var jiraClient = new JiraClient("URI", "login", "pass");

            var tfsClient = new TfsClient(Guid.Empty, "URI", "AuthToken");

            Console.WriteLine("--------Epics---------");
            var epicIssues = jiraClient.GetEpicIssues(TASK_NAME, take: Int32.MaxValue);
            await tfsClient.Migration(epicIssues);

            Console.WriteLine("--------Stories---------");
            var storyIssues = jiraClient.GetStoryIssues(TASK_NAME, 0, Int32.MaxValue);

            await tfsClient.Migration(storyIssues);

            Console.WriteLine("--------Tasks---------");
            var taskIssues = jiraClient.GetTaskIssues(TASK_NAME, 0, Int32.MaxValue);
            await tfsClient.Migration(taskIssues);

            Console.WriteLine("--------SubTasks---------");
            var subTaskIssues = jiraClient.GetSubTaskIssues(TASK_NAME, 0, Int32.MaxValue);
            await tfsClient.Migration(subTaskIssues);

            Console.WriteLine("--------Bags---------");
            var bagIssues = jiraClient.GetBagIssues(TASK_NAME, 0, Int32.MaxValue);
            await tfsClient.Migration(bagIssues);
        }
    }
}
