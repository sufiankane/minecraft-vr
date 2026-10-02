using System.Collections.Generic;
using NUnit.Framework;

namespace Cubeglass.Gameplay.Tests
{
    /// <summary>
    /// Drives the JSON interaction scenario scripts (S4 work item 4).
    /// </summary>
    /// <remarks>
    /// Each script is replayed at a fixed timestep through
    /// <see cref="ScenarioRunner"/>; the final world hash, the declared
    /// accepted-command model and the selected per-step observations must all
    /// agree.
    /// </remarks>
    [TestFixture]
    public sealed class InteractionScenarios
    {
        private static readonly string[] RequiredScenarios =
        {
            "walk",
            "break",
            "place",
            "place-inside-player",
            "tracking-loss-cancel",
            "hotbar-mid-break",
            "recentre",
        };

        public static IEnumerable<TestCaseData> ScenarioCases()
        {
            foreach (string name in ScenarioRunner.ScenarioNames())
            {
                yield return new TestCaseData(name).SetName("Scenario_" + name.Replace('-', '_'));
            }
        }

        [TestCaseSource(nameof(ScenarioCases))]
        public void ReplaysWithTheExpectedWorldHashAndObservations(string name)
        {
            ScenarioRunner.RunFile(ScenarioRunner.ScenarioPath(name));
        }

        [Test]
        public void EveryRequiredScenarioHasAScript()
        {
            IReadOnlyList<string> names = ScenarioRunner.ScenarioNames();
            foreach (string required in RequiredScenarios)
            {
                Assert.That(names, Does.Contain(required), $"missing scenario script '{required}.json'");
            }
        }
    }
}
