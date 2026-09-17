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
using Sentry;

namespace ServiceItemsPlanningPlugin.Scheduler.Jobs;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Infrastructure.Helpers;
using Messages;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Dto;
using Microting.eForm.Infrastructure.Constants;
using Microting.ItemsPlanningBase.Infrastructure.Data;
using Microting.ItemsPlanningBase.Infrastructure.Data.Entities;
using Microting.ItemsPlanningBase.Infrastructure.Enums;
using Rebus.Bus;

public static class DateTimeExtensions
{
    public static DateTime StartOfWeek(this DateTime dt, DayOfWeek startOfWeek)
    {
        int diff = (7 + (dt.DayOfWeek - startOfWeek)) % 7;
        return dt.AddDays(-1 * diff).Date;
    }

    public static DateTime? NthWeekdayOfMonth(int year, int month, int ordinal, int targetDow)
    {
        var firstOfMonth = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        int dowOffset = (targetDow - (int)firstOfMonth.DayOfWeek + 7) % 7;
        var candidate = firstOfMonth.AddDays(dowOffset + (ordinal - 1) * 7);
        return candidate.Month != month ? null : candidate;
    }
}

public class SearchListJob(
    DbContextHelper dbContextHelper,
    IBus bus,
    eFormCore.Core sdkCore)
    : IJob
{
    private readonly ItemsPlanningPnDbContext _dbContext = dbContextHelper.GetDbContext();

    /// <summary>
    /// Reads the SDK's <c>skipCloudDeploy</c> setting through the SDK's own public API, so this stays
    /// correct if the storage representation ever changes.
    /// <para>
    /// The SDK's own <c>Core.SkipCloudDeployAsync()</c> — the check it uses to short-circuit
    /// <c>SendXml</c> — is <c>private</c>, so it cannot be called from here;
    /// <see cref="eFormCore.Core.GetSdkSetting"/> is the public read of the same
    /// <see cref="Settings.skipCloudDeploy"/> row, and the <c>== "true"</c> comparison below is
    /// deliberately the identical ordinal, case-sensitive test the SDK applies, so this job skips
    /// exactly when the SDK skips and never when it would still deploy.
    /// </para>
    /// <para>
    /// Fail-safe in one direction: a missing row (installations that pre-date the setting — the SDK
    /// returns <c>"N/A"</c> for those) and any read failure both come back as <c>false</c>, i.e.
    /// normal cloud deployment, so the behaviour of this job is unchanged wherever the setting is not
    /// explicitly turned on.
    /// </para>
    /// </summary>
    public static async Task<bool> IsCloudDeploySkippedAsync(eFormCore.Core sdkCore)
    {
        try
        {
            // GetSdkSetting already swallows a missing row into "N/A"; the catch below is for the
            // case where it cannot be reached at all.
            return await sdkCore.GetSdkSetting(Settings.skipCloudDeploy) == "true";
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"fail: SearchListJob.Task: could not read the {nameof(Settings.skipCloudDeploy)} SDK setting, assuming cloud deploy is enabled: {ex.Message}");
            SentrySdk.CaptureException(ex);
            return false;
        }
    }

    /// <summary>
    /// True when the planning is owned by the backend-configuration plugin rather than by items-planning
    /// itself. Backend-configuration stamps every planning it creates with
    /// <c>IsLocked = true, IsEditable = false</c>; plannings created through the items-planning UI or
    /// import are always <c>IsLocked = false, IsEditable = true</c>. The items-planning plugin already
    /// treats the same pair as an "owned elsewhere" predicate in PairingService.
    /// </summary>
    public static bool IsBackendConfigurationOwned(Planning planning)
    {
        return planning.IsLocked && !planning.IsEditable;
    }

    /// <summary>
    /// The single "this planning is none of our business this cycle" test, shared by ExecuteDeploy and
    /// ExecuteCleanUp so the two can never drift apart. ExecuteDeploy leaves such a planning
    /// undeployed, which means its <c>LastExecutedTime</c> stays null forever — so ExecuteCleanUp has
    /// to honour the same predicate, otherwise its "NextExecutionTime set but never executed" branch
    /// would match every one of them on every cycle, for ever, nulling NextExecutionTime and raising a
    /// Sentry message each time.
    /// </summary>
    public static bool IsLeftToBackendConfiguration(Planning planning, bool skipCloudDeploy)
    {
        return skipCloudDeploy && IsBackendConfigurationOwned(planning);
    }

    public async Task Execute()
    {
        await ExecuteDeploy();
        await ExecutePush();
        await ExecuteCleanUp();
    }

    private async Task ExecutePush()
    {
        if (DateTime.UtcNow.Hour == 6)
        {
            Console.WriteLine("info: SearchListJob.Task: SearchListJob.ExecutePush got called");
            var now = DateTime.UtcNow;

            var baseQuery = _dbContext.Plannings
                .Where(x =>
                    (x.RepeatUntil == null || DateTime.UtcNow <= x.RepeatUntil)
                    &&
                    (DateTime.UtcNow >= x.StartDate)
                    &&
                    x.DaysBeforeRedeploymentPushMessageRepeat == true
                    &&
                    x.WorkflowState != Constants.WorkflowStates.Removed);

            var pushReady = baseQuery.Where(x => !x.DoneInPeriod).Where(x => x.NextExecutionTime > now)
                .Where(x => x.RepeatType != RepeatType.Day).Where(x => !x.PushMessageSent);

            var pushReadyPlannings = await pushReady.ToListAsync();

            Console.WriteLine($"info: SearchListJob.Task: Found {pushReadyPlannings.Count} pushReadyPlannings");

            foreach (Planning planning in pushReadyPlannings)
            {
                if ((((DateTime)planning.NextExecutionTime!).AddDays(-1).Date - now.Date).Days ==
                    planning.DaysBeforeRedeploymentPushMessage)
                {
                    await bus.SendLocal(new PushMessage(planning.Id));
                }
            }
        }
    }

    private async Task ExecuteDeploy()
    {
        var startTimeDb = await _dbContext.PluginConfigurationValues
            .SingleOrDefaultAsync(x => x.Name == "ItemsPlanningBaseSettings:StartTime");
        if (startTimeDb != null)
        {
            int startTime = int.Parse(startTimeDb.Value);

            int endTime = int.Parse(_dbContext.PluginConfigurationValues
                .Single(x => x.Name == "ItemsPlanningBaseSettings:EndTime").Value);
            if (DateTime.UtcNow.Hour < startTime)
            {
                Console.WriteLine(
                    $"info: SearchListJob.Task: ExecuteDeploy The current hour is smaller than the start time of {startTime}, so ending processing");
                return;
            }

            if (DateTime.UtcNow.Hour > endTime)
            {
                Console.WriteLine(
                    $"info: SearchListJob.Task: ExecuteDeploy The current hour is bigger than the end time of {endTime}, so ending processing");
                return;
            }

            Console.WriteLine("info: SearchListJob.Task: SearchListJob.ExecuteDeploy got called");
            var now = DateTime.UtcNow;
            now = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0);

            // Read once per cycle, not per planning.
            var skipCloudDeploy = await IsCloudDeploySkippedAsync(sdkCore);

            var baseQuery = _dbContext.Plannings
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed);

            var planningsForExecution = await baseQuery
                .Where(x => x.NextExecutionTime <= now || x.NextExecutionTime == null)
                .Where(x => x.StartDate <= now)
                .Where(x => x.Enabled)
                .ToListAsync();

            Console.WriteLine($"info: SearchListJob.Task: Found {planningsForExecution.Count} plannings");

            if (skipCloudDeploy)
            {
                // With skipCloudDeploy the SDK short-circuits SendXml and hands back a synthetic
                // MicrotingUid, so nothing ever leaves for the cloud and no notification comes back.
                // For backend-configuration-owned plannings that makes this job pure harm: it retracts
                // and redeploys against uids the cloud never saw, and destroys one-off and yearly
                // deployments on every pass. Backend-configuration derives its own occurrences and
                // creates its own Compliance rows, so it needs nothing this job writes.
                // Ownership alone is deliberately NOT enough to skip: on an installation that still
                // does cloud deploys, this job is the only thing performing a real CaseCreate for a
                // recurring backend-configuration planning.
                var skippedCount = planningsForExecution.Count(x => IsLeftToBackendConfiguration(x, skipCloudDeploy));
                planningsForExecution = planningsForExecution
                    .Where(x => !IsLeftToBackendConfiguration(x, skipCloudDeploy))
                    .ToList();

                Console.WriteLine(
                    $"info: SearchListJob.Task: ExecuteDeploy skipCloudDeploy=true, skipped {skippedCount} backend-configuration-owned planning(s) (IsLocked=true, IsEditable=false) because cloud deploy is disabled and backend-configuration deploys them itself; {planningsForExecution.Count} planning(s) left for execution");
            }

            var scheduledItemPlannings = new List<Planning>();
            scheduledItemPlannings.AddRange(planningsForExecution);

            await using var sdkDbContext = sdkCore.DbContextHelper.GetDbContext();

            foreach (var planning in scheduledItemPlannings)
            {
                if (planning.RepeatType == RepeatType.Day && planning.RepeatEvery == 0)
                {
                    continue;
                }

                planning.LastExecutedTime = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0);
                planning.DoneInPeriod = false;
                planning.PushMessageSent = false;

                planning.NextExecutionTime ??= new DateTime(planning.StartDate.Year, planning.StartDate.Month, planning.StartDate.Day, 0, 0, 0);

                if (planning.RepeatType == RepeatType.Day)
                {
                    if (planning.RepeatEvery != 0)
                    {
                        planning.NextExecutionTime = ((DateTime)planning.NextExecutionTime!).AddDays(planning.RepeatEvery);
                    }
                }
                if (planning.RepeatType == RepeatType.Week)
                {
                    if (planning.DayOfWeek != null)
                    {
                        planning.NextExecutionTime = ((DateTime)planning.NextExecutionTime!).AddDays(planning.RepeatEvery * 7);
                    }
                }

                if (planning.RepeatType == RepeatType.Month)
                {
                    var current = (DateTime)planning.NextExecutionTime!;
                    if (planning.RepeatOrdinalWeek is > 0 && planning.DayOfWeek != null)
                    {
                        // Nth-weekday-of-month rule (e.g. "3rd Thursday"): advance whole months,
                        // then snap to the Nth occurrence of the target weekday in that month.
                        var target = current.AddMonths(planning.RepeatEvery);
                        var snapped = DateTimeExtensions.NthWeekdayOfMonth(
                            target.Year, target.Month, planning.RepeatOrdinalWeek.Value, (int)planning.DayOfWeek.Value);
                        // If the ordinal spills past the month (e.g. no 5th Thursday), fall back to the
                        // last occurrence of that weekday in the month.
                        snapped ??= DateTimeExtensions.NthWeekdayOfMonth(
                            target.Year, target.Month, 4, (int)planning.DayOfWeek.Value)
                            ?? DateTimeExtensions.NthWeekdayOfMonth(target.Year, target.Month, 3, (int)planning.DayOfWeek.Value);
                        planning.NextExecutionTime = new DateTime(snapped!.Value.Year, snapped.Value.Month, snapped.Value.Day, 0, 0, 0);
                    }
                    else if (planning.DayOfMonth != null)
                    {
                        if (planning.DayOfMonth == 0)
                        {
                            planning.DayOfMonth = 1;
                        }
                        planning.NextExecutionTime = current.AddMonths(planning.RepeatEvery);
                    }
                }

                await planning.Update(_dbContext);

                foreach (var planningSite in _dbContext.PlanningSites
                             .Where(y => y.WorkflowState != Constants.WorkflowStates.Removed
                                         && y.PlanningId == planning.Id).ToList())
                {
                    planningSite.LastExecutedTime = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0);
                    await planningSite.Update(_dbContext);
                }

                await bus.SendLocal(new ScheduledItemExecuted(planning.Id));

                Console.WriteLine($"info: SearchListJob.Task: Planning {planning.Id} executed");
            }
        }
    }

    private async Task ExecuteCleanUp()
    {
        try
        {
            var now = DateTime.UtcNow;
            now = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0);

            // Read once per cycle, not per planning — same as ExecuteDeploy.
            var skipCloudDeploy = await IsCloudDeploySkippedAsync(sdkCore);

            var baseQuery = _dbContext.Plannings
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed);

            var planningForCorrectingNextExecutionTime = await baseQuery
                .Where(x => x.NextExecutionTime != null)
                .Where(x => x.StartDate > now)
                .Where(x => x.Enabled)
                .ToListAsync();

            foreach (var planning in planningForCorrectingNextExecutionTime)
            {
                SentrySdk.CaptureMessage($"Setting NextExecutionTime to null for planning.Id {planning.Id}");
                Console.WriteLine($"fail: Setting NextExecutionTime to null for planning.Id {planning.Id}");
                planning.NextExecutionTime = null;
                await planning.Update(_dbContext);
            }

            planningForCorrectingNextExecutionTime = await baseQuery
                .Where(x => x.NextExecutionTime != null)
                .Where(x => x.StartDate <= now)
                .Where(x => x.LastExecutedTime == null)
                .Where(x => x.Enabled)
                .ToListAsync();

            if (skipCloudDeploy)
            {
                // A planning ExecuteDeploy deliberately skipped never gets a LastExecutedTime, so
                // without this gate it would match this branch on every cycle for ever — nulling
                // NextExecutionTime and raising a Sentry message each time. Leave it alone, exactly as
                // ExecuteDeploy does.
                var leftAloneCount =
                    planningForCorrectingNextExecutionTime.Count(x => IsLeftToBackendConfiguration(x, skipCloudDeploy));
                planningForCorrectingNextExecutionTime = planningForCorrectingNextExecutionTime
                    .Where(x => !IsLeftToBackendConfiguration(x, skipCloudDeploy))
                    .ToList();

                if (leftAloneCount > 0)
                {
                    Console.WriteLine(
                        $"info: SearchListJob.Task: ExecuteCleanUp skipCloudDeploy=true, left {leftAloneCount} backend-configuration-owned planning(s) with a null LastExecutedTime alone because ExecuteDeploy deliberately skipped them");
                }
            }

            foreach (var planning in planningForCorrectingNextExecutionTime)
            {
                SentrySdk.CaptureMessage(
                    $"Setting NextExecutionTime to null for planning.Id {planning.Id} since LastExecutedTime is null");
                Console.WriteLine(
                    $"fail: Setting NextExecutionTime to null for planning.Id {planning.Id} since LastExecutedTime is null");
                planning.NextExecutionTime = null;
                await planning.Update(_dbContext);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            Console.WriteLine(ex.StackTrace);
            SentrySdk.CaptureException(ex);
        }
    }
}