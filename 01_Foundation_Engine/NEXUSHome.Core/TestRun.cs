using System;
using System.Threading;

namespace NEXUSHomeHub.Core
{
    /// <summary>
    /// TASK-12c (B1 pre-fix, data layer): is this process a test run? True for <c>HostService --selftest</c> (marked by
    /// Program.Main) and for any process started with a Gemini mock (NEXUS_GEMINI_MOCK_RESPONSE or
    /// NEXUS_GEMINI_QA_MOCK_VERDICT: the Verify scripts' HostService and McpTools). Dispatch rows written by a test run carry
    /// <c>is_test = 1</c> and stay out of the Cockpit "Recent Dispatches" feed of the user's real HostService.
    /// </summary>
    public static class TestRun
    {
        private static int _selfTest;

        public static bool MockEnvironment { get; } =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NEXUS_GEMINI_MOCK_RESPONSE")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NEXUS_GEMINI_QA_MOCK_VERDICT"));

        public static bool SelfTest => Volatile.Read(ref _selfTest) == 1;

        public static bool Active => SelfTest || MockEnvironment;

        public static void MarkSelfTest() => Interlocked.Exchange(ref _selfTest, 1);
    }
}
