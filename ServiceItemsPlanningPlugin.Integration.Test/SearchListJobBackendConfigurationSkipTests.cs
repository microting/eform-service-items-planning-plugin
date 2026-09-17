/*
The MIT License (MIT)

Copyright (c) 2007 - 2021 Microting A/S

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

namespace ServiceItemsPlanningPlugin.Integration.Test
{
    using System.Collections.Generic;
    using System.Linq;
    using Microting.ItemsPlanningBase.Infrastructure.Data.Entities;
    using NUnit.Framework;
    using Scheduler.Jobs;

    /// <summary>
    /// Covers the targeted skip added for microting/eform-backendconfiguration-plugin#1273 and #1280:
    /// when the SDK setting skipCloudDeploy is on, SearchListJob must leave backend-configuration-owned
    /// plannings entirely alone, and must change nothing at all otherwise.
    /// </summary>
    [TestFixture]
    public class SearchListJobBackendConfigurationSkipTests
    {
        private static Planning BackendConfigurationOwned(int id = 1)
        {
            // What BackendConfigurationTaskWizardService and
            // BackendConfigurationAreaRulePlanningsServiceHelper stamp on every planning they create.
            return new Planning { Id = id, IsLocked = true, IsEditable = false };
        }

        private static Planning ItemsPlanningOwned(int id = 2)
        {
            // What ItemsPlanning's own PlanningService / PlanningImportService stamp.
            return new Planning { Id = id, IsLocked = false, IsEditable = true };
        }

        /// <summary>
        /// The skip is applied only to plannings that carry BOTH backend-configuration markers.
        /// This mirrors what ExecuteDeploy and ExecuteCleanUp each do to their own candidate list —
        /// both filter on SearchListJob.IsLeftToBackendConfiguration, which is why one helper can
        /// stand in for both here.
        /// </summary>
        private static List<Planning> ApplySkip(IEnumerable<Planning> plannings, bool skipCloudDeploy)
        {
            return plannings
                .Where(x => !SearchListJob.IsLeftToBackendConfiguration(x, skipCloudDeploy))
                .ToList();
        }

        [Test]
        public void IsBackendConfigurationOwned_IsTrue_OnlyWhenLockedAndNotEditable()
        {
            Assert.Multiple(() =>
            {
                Assert.That(SearchListJob.IsBackendConfigurationOwned(
                    new Planning { IsLocked = true, IsEditable = false }), Is.True,
                    "IsLocked=true + IsEditable=false is the backend-configuration marker");

                Assert.That(SearchListJob.IsBackendConfigurationOwned(
                    new Planning { IsLocked = false, IsEditable = true }), Is.False,
                    "an items-planning owned planning must never match");

                Assert.That(SearchListJob.IsBackendConfigurationOwned(
                    new Planning { IsLocked = true, IsEditable = true }), Is.False,
                    "locked but still editable is not the marker");

                Assert.That(SearchListJob.IsBackendConfigurationOwned(
                    new Planning { IsLocked = false, IsEditable = false }), Is.False,
                    "an unstamped planning (both bools default) must never match");
            });
        }

        [Test]
        public void Skip_RemovesBackendConfigurationPlannings_WhenSkipCloudDeployIsOn()
        {
            var bcPlanning = BackendConfigurationOwned();
            var ownPlanning = ItemsPlanningOwned();

            var result = ApplySkip(new[] { bcPlanning, ownPlanning }, skipCloudDeploy: true);

            Assert.That(result, Is.EqualTo(new[] { ownPlanning }));
        }

        [Test]
        public void Skip_NeverRemovesANonBackendConfigurationPlanning_EvenWhenSkipCloudDeployIsOn()
        {
            var plannings = new[]
            {
                ItemsPlanningOwned(1),
                new Planning { Id = 2, IsLocked = true, IsEditable = true },
                new Planning { Id = 3, IsLocked = false, IsEditable = false }
            };

            var result = ApplySkip(plannings, skipCloudDeploy: true);

            Assert.That(result, Is.EqualTo(plannings));
        }

        [Test]
        public void Skip_IsInert_WhenSkipCloudDeployIsOffOrUnset()
        {
            var plannings = new[] { BackendConfigurationOwned(1), ItemsPlanningOwned(2) };

            // "false" and "absent" both arrive here as false — see IsCloudDeploySkippedAsync.
            var result = ApplySkip(plannings, skipCloudDeploy: false);

            Assert.That(result, Is.EqualTo(plannings),
                "with cloud deploy enabled this job is the only thing that performs a real CaseCreate " +
                "for a recurring backend-configuration planning, so nothing may be skipped");
        }

        [Test]
        public void Skip_CountsOnlyBackendConfigurationPlannings()
        {
            var plannings = new[]
            {
                BackendConfigurationOwned(1),
                BackendConfigurationOwned(2),
                ItemsPlanningOwned(3)
            };

            var skippedCount = plannings.Count(x => SearchListJob.IsLeftToBackendConfiguration(x, true));

            Assert.That(skippedCount, Is.EqualTo(2));
        }

        // ------------------------------------------------------------------
        // ExecuteCleanUp gating.
        //
        // The cleanup branch these cover selects on NextExecutionTime != null && StartDate <= now &&
        // LastExecutedTime == null && Enabled, all of which are plain column reads; the only part that
        // is not a DB query is the IsLeftToBackendConfiguration filter applied to the result, so that
        // filter is what is asserted here. Whether the surrounding EF query returns the right rows is
        // unchanged by this branch and is not re-tested.
        // ------------------------------------------------------------------

        /// <summary>
        /// Stands in for a planning that ExecuteDeploy skipped: it has a NextExecutionTime, its
        /// StartDate is in the past, and — because it was skipped — LastExecutedTime is still null.
        /// That is exactly the shape ExecuteCleanUp's second branch selects.
        /// </summary>
        private static Planning NeverExecutedBackendConfigurationPlanning(int id = 1)
        {
            return new Planning
            {
                Id = id,
                IsLocked = true,
                IsEditable = false,
                Enabled = true,
                StartDate = System.DateTime.UtcNow.AddDays(-30),
                NextExecutionTime = System.DateTime.UtcNow.AddDays(1),
                LastExecutedTime = null
            };
        }

        [Test]
        public void CleanUp_LeavesSkippedBackendConfigurationPlanningsAlone_WhenSkipCloudDeployIsOn()
        {
            var skippedByDeploy = NeverExecutedBackendConfigurationPlanning(1);
            var genuinelyStale = new Planning
            {
                Id = 2,
                IsLocked = false,
                IsEditable = true,
                Enabled = true,
                StartDate = System.DateTime.UtcNow.AddDays(-30),
                NextExecutionTime = System.DateTime.UtcNow.AddDays(1),
                LastExecutedTime = null
            };

            var toCorrect = ApplySkip(new[] { skippedByDeploy, genuinelyStale }, skipCloudDeploy: true);

            Assert.That(toCorrect, Is.EqualTo(new[] { genuinelyStale }),
                "the planning ExecuteDeploy skipped must not have its NextExecutionTime nulled, or it " +
                "would be re-nulled and re-reported to Sentry on every cycle for ever");
        }

        [Test]
        public void CleanUp_IsUnchangedForEveryPlanning_WhenSkipCloudDeployIsOff()
        {
            var plannings = new[]
            {
                NeverExecutedBackendConfigurationPlanning(1),
                ItemsPlanningOwned(2)
            };

            var toCorrect = ApplySkip(plannings, skipCloudDeploy: false);

            Assert.That(toCorrect, Is.EqualTo(plannings),
                "with cloud deploy enabled ExecuteCleanUp must behave exactly as it did before this change");
        }

        /// <summary>
        /// The full truth table of the one predicate ExecuteDeploy and ExecuteCleanUp share. Pinning it
        /// here is what stops the two branches drifting apart: they agree by construction because they
        /// call this method, and this test is what that method is allowed to mean. Exactly one of the
        /// eight cells is true.
        /// </summary>
        [TestCase(true, true, false, ExpectedResult = true)]
        [TestCase(true, true, true, ExpectedResult = false)]
        [TestCase(true, false, false, ExpectedResult = false)]
        [TestCase(true, false, true, ExpectedResult = false)]
        [TestCase(false, true, false, ExpectedResult = false)]
        [TestCase(false, true, true, ExpectedResult = false)]
        [TestCase(false, false, false, ExpectedResult = false)]
        [TestCase(false, false, true, ExpectedResult = false)]
        public bool IsLeftToBackendConfiguration_TruthTable(bool skipCloudDeploy, bool isLocked, bool isEditable)
        {
            return SearchListJob.IsLeftToBackendConfiguration(
                new Planning { IsLocked = isLocked, IsEditable = isEditable }, skipCloudDeploy);
        }
    }
}
