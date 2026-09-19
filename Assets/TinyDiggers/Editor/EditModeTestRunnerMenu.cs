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

        static ConsoleTestCallbacks s_callbacks;

        [MenuItem("TinyDiggers/Run EditMode Tests")]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError($"{ResultPrefix} NOT RUN: leave play mode first.");
                return;
            }

            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            // Callbacks are registered globally, so a run that never finished would otherwise
            // leave its listener behind and every later run would report twice.
            if (s_callbacks != null)
                api.UnregisterCallbacks(s_callbacks);
            // A ScriptableObject so that it survives the domain reload a test run can trigger.
            s_callbacks = ScriptableObject.CreateInstance<ConsoleTestCallbacks>();
            api.RegisterCallbacks(s_callbacks);
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
