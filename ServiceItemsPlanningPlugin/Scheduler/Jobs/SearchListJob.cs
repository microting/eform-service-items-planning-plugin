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

namespace ServiceItemsPlanningPlugin.Scheduler.Jobs;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Infrastructure.Helpers;
using Messages;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.ItemsPlanningBase.Infrastructure.Data;
using Microting.ItemsPlanningBase.Infrastructure.Data.Entities;
using Microting.ItemsPlanningBase.Infrastructure.Enums;
using Microting.eFormApi.BasePn.Infrastructure.Helpers;
using Rebus.Bus;

public static class DateTimeExtensions
{
    public static DateTime StartOfWeek(this DateTime dt, DayOfWeek startOfWeek)
    {
        int diff = (7 + (dt.DayOfWeek - startOfWeek)) % 7;
        return dt.AddDays(-1 * diff).Date;
    }
}

public class SearchListJob : IJob
{
    private readonly ItemsPlanningPnDbContext _dbContext;
    private readonly IBus _bus;
    private readonly eFormCore.Core _sdkCore;

    public SearchListJob(
        DbContextHelper dbContextHelper,
        IBus bus,
        eFormCore.Core sdkCore)
    {
        _dbContext = dbContextHelper.GetDbContext();
        _bus = bus;
        _sdkCore = sdkCore;
    }

    public async Task Execute()
    {
        await ExecuteDeploy();
        await ExecutePush();
    }

    private async Task ExecutePush()
    {
        if (DateTime.UtcNow.Hour == 6)
        {
            Log.LogEvent("SearchListJob.Task: SearchListJob.Execute got called");
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

            foreach (Planning planning in pushReadyPlannings)
            {
                if ((((DateTime)planning.NextExecutionTime!).Date - now.Date).Days ==
                    planning.DaysBeforeRedeploymentPushMessage)
                {
                    await _bus.SendLocal(new PushMessage(planning.Id));
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
                Log.LogEvent(
                    $"SearchListJob.Task: ExecuteDeploy The current hour is smaller than the start time of {startTime}, so ending processing");
                return;
            }

            if (DateTime.UtcNow.Hour > endTime)
            {
                Log.LogEvent(
                    $"SearchListJob.Task: ExecuteDeploy The current hour is bigger than the end time of {endTime}, so ending processing");
                return;
            }

            Log.LogEvent("SearchListJob.Task: SearchListJob.Execute got called");
            var now = DateTime.UtcNow;
            now = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0);
            var baseQuery = _dbContext.Plannings
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed);

            var planningForCorrectingNextExecutionTime = await baseQuery
                .Where(x => x.NextExecutionTime != null)
                .Where(x => x.RepeatType != RepeatType.Day)
                .Where(x => x.RepeatEvery != 0)
                .Where(x => x.StartDate > now)
                .Where(x => x.Enabled)
                .ToListAsync();

            foreach (var planning in planningForCorrectingNextExecutionTime)
            {
                planning.NextExecutionTime = null;
                await planning.Update(_dbContext);
            }

            var planningsForExecution = await baseQuery
                .Where(x => x.NextExecutionTime <= now || x.NextExecutionTime == null)
                .Where(x => x.StartDate <= now)
                .Where(x => x.RepeatEvery != 0 && x.RepeatType != RepeatType.Day)
                .Where(x => x.Enabled)
                .ToListAsync();

            Log.LogEvent($"SearchListJob.Task: Found {planningsForExecution.Count} plannings");

            var scheduledItemPlannings = new List<Planning>();
            scheduledItemPlannings.AddRange(planningsForExecution);

            await using var sdkDbContext = _sdkCore.DbContextHelper.GetDbContext();

            foreach (var planning in scheduledItemPlannings)
            {
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
                    if (planning.DayOfMonth != null)
                    {
                        if (planning.DayOfMonth == 0)
                        {
                            planning.DayOfMonth = 1;
                        }
                        planning.NextExecutionTime = ((DateTime)planning.NextExecutionTime!).AddMonths(planning.RepeatEvery);
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

                await _bus.SendLocal(new ScheduledItemExecuted(planning.Id));

                Log.LogEvent($"SearchListJob.Task: Planning {planning.Id} executed");
            }

            // new plannings
            // foreach (var newPlanningSite in newPlanningSites)
            // {
            //     if (scheduledItemPlannings.All(x => x.Id != newPlanningSite.Id))
            //     {
            //         foreach (var planningSite in newPlanningSite.PlanningSites)
            //         {
            //             if (planningSite.LastExecutedTime == null)
            //             {
            //                 planningSite.LastExecutedTime = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0);;
            //                 await planningSite.Update(_dbContext);
            //
            //                 await _bus.SendLocal(new ScheduledItemExecuted(newPlanningSite.Id,
            //                     planningSite.SiteId));
            //                 //var newPlanningSiteName = translatedName.SingleOrDefault(x => x.PlanningId == newPlanningSite.Id)?.Name;
            //                 Log.LogEvent(
            //                     $"SearchListJob.Task: Planning {planningSite.PlanningId} executed with PlanningSite {planningSite.SiteId}");
            //             }
            //         }
            //     }
            // }
        }
    }
}