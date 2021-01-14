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

namespace ServiceItemsPlanningPlugin.Scheduler.Jobs
{
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
            int startTime = int.Parse(_dbContext.PluginConfigurationValues
                .Single(x => x.Name == "ItemsPlanningBaseSettings:StartTime").Value);
            int endTime = int.Parse(_dbContext.PluginConfigurationValues
                .Single(x => x.Name == "ItemsPlanningBaseSettings:EndTime").Value);
            if (DateTime.UtcNow.Hour < startTime)
            {
                Log.LogEvent($"SearchListJob.Task: The current hour is smaller than the start time of {startTime}, so ending processing");
                return;
            }

            if (DateTime.UtcNow.Hour > endTime)
            {
                Log.LogEvent($"SearchListJob.Task: The current hour is bigger than the end time of {endTime}, so ending processing");
                return;
            }

            Log.LogEvent("SearchListJob.Task: SearchListJob.Execute got called");
            var now = DateTime.UtcNow;
            var lastDayOfMonth = new DateTime(now.Year, now.Month, 1).AddMonths(1).AddDays(-1).Day;
            var firstDayOfMonth = new DateTime(now.Year, now.Month, 1).Day;


            var baseQuery = _dbContext.Plannings
                .Where(x =>
                    (x.RepeatUntil == null || DateTime.UtcNow <= x.RepeatUntil)
                    &&
                    (DateTime.UtcNow >= x.StartDate)
                    &&
                    x.WorkflowState != Constants.WorkflowStates.Removed);

            var dailyListsQuery = baseQuery
                .Where(x => x.RepeatType == RepeatType.Day
                            && (x.LastExecutedTime == null ||
                                now.AddDays(-x.RepeatEvery) >= x.LastExecutedTime));

            var weeklyListsQuery = baseQuery
                .Where(x => x.RepeatType == RepeatType.Week
                            && (x.LastExecutedTime == null ||
                                (now.AddDays(-x.RepeatEvery * 7) >= x.LastExecutedTime && x.DayOfWeek == now.DayOfWeek)));

            var monthlyListsQuery = baseQuery
                .Where(x => x.RepeatType == RepeatType.Month
                            && (x.LastExecutedTime == null ||
                                ((x.DayOfMonth <= now.Day || now.Day == firstDayOfMonth) &&
                                 ((now.Month - x.LastExecutedTime.Value.Month) + 12 * (now.Year - x.LastExecutedTime.Value.Year) >= x.RepeatEvery))));

//            Console.WriteLine($"Daily lists query: {dailyListsQuery.ToSql()}");
//            Console.WriteLine($"Weekly lists query: {weeklyListsQuery.ToSql()}");
//            Console.WriteLine($"Monthly lists query: {monthlyListsQuery.ToSql()}");

            var dailyPlannings = await dailyListsQuery.ToListAsync();
            var weeklyPlannings = await weeklyListsQuery.ToListAsync();
            var monthlyPlannings = await monthlyListsQuery.ToListAsync();

            // Find plannings where site not executed
            var newPlanningSites = await baseQuery
                .Where(x => x.LastExecutedTime != null)
                .Where(x => x.PlanningSites.Any(y => y.LastExecutedTime == null))
                .ToListAsync();

            Log.LogEvent($"SearchListJob.Task: Found {dailyPlannings.Count} daily plannings");
            Log.LogEvent($"SearchListJob.Task: Found {weeklyPlannings.Count} weekly plannings");
            Log.LogEvent($"SearchListJob.Task: Found {monthlyPlannings.Count} monthly plannings");

            var scheduledItemPlannings = new List<Planning>();
            scheduledItemPlannings.AddRange(dailyPlannings);
            scheduledItemPlannings.AddRange(weeklyPlannings);
            scheduledItemPlannings.AddRange(monthlyPlannings);

            await using var sdkDbContext = _sdkCore.dbContextHelper.GetDbContext();

            foreach (var planning in scheduledItemPlannings)
            {
                planning.LastExecutedTime = now;

                await planning.Update(_dbContext);

                foreach (var planningSite in planning.PlanningSites
                    .Where(y => y.WorkflowState != Constants.WorkflowStates.Removed))
                {
                    planningSite.LastExecutedTime = now;
                    await planningSite.Update(_dbContext);
                }

                await _bus.SendLocal(new ScheduledItemExecuted(planning.Id));


                Log.LogEvent($"SearchListJob.Task: Planning {planning.Id} executed");
            }

            // new plannings
            foreach (var newPlanningSite in newPlanningSites)
            {
                if (scheduledItemPlannings.All(x => x.Id != newPlanningSite.Id))
                {
                    foreach (var planningSite in newPlanningSite.PlanningSites)
                    {
                        if (planningSite.LastExecutedTime == null)
                        {
                            planningSite.LastExecutedTime = now;
                            await planningSite.Update(_dbContext);

                            await _bus.SendLocal(new ScheduledItemExecuted(newPlanningSite.Id, planningSite.SiteId));
                            //var newPlanningSiteName = translatedName.SingleOrDefault(x => x.PlanningId == newPlanningSite.Id)?.Name;
                            Log.LogEvent(
                                $"SearchListJob.Task: Planning {planningSite.PlanningId} executed with PlanningSite {planningSite.SiteId}");
                        }
                    }
                }
            }
        }
    }
}