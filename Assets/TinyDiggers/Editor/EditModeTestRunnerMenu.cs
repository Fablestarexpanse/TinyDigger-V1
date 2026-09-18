using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace TinyDiggers.EditorTools
{
    /// <summary>
    /// Runs the EditMode suite and writes the result to the console as a single summary line
    /// followed by any failures. The Test Runner window reports the same thing, but only to a
    /// human looking at it; this way the result can be read back from a log.
    /// </summary>
    public static class EditModeTestRunnerMenu
    {
        public const string ResultPrefix = "TESTS";

        [MenuItem("TinyDiggers/Run EditMode Tests")]
        public static void Run()
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            // The callbacks object is a ScriptableObject so that it survives the domain reload
            // that a test run can trigger.
            api.RegisterCallbacks(ScriptableObject.CreateInstance<ConsoleTestCallbacks>());
            api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode }));
        }
    }

    class ConsoleTestCallbacks : ScriptableObject, ICallbacks
    {
        public void RunStarted(ITestAdaptor testsToRun)
        {
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            var report = new StringBuilder();
            report.AppendLine(
                $"{EditModeTestRunnerMenu.ResultPrefix} {(result.FailCount == 0 ? "PASS" : "FAIL")} " +
                $"passed={result.PassCount} failed={result.FailCount} skipped={result.SkipCount} " +
                $"inconclusive={result.InconclusiveCount} duration={result.Duration:0.00}s");
            AppendFailures(result, report);

            if (result.FailCount == 0)
                Debug.Log(report.ToString());
            else
                Debug.LogError(report.ToString());
        }

        static void AppendFailures(ITestResultAdaptor result, StringBuilder report)
        {
            if (result.HasChildren)
            {
                foreach (var child in result.Children)
                    AppendFailures(child, report);
                return;
            }

            if (result.TestStatus == TestStatus.Failed)
                report.AppendLine($"FAILED {result.FullName}: {result.Message}");
        }

        public void TestStarted(ITestAdaptor test)
        {
        }

        public void TestFinished(ITestResultAdaptor result)
        {
        }
    }
}
