/*
The MIT License (MIT)

Copyright (c) 2007 - 2020 Microting A/S

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
    using Rebus.Bus;

    public class SearchListJob : IJob
    {
        private readonly ItemsPlanningPnDbContext _dbContext;
        private readonly IBus _bus;

        public SearchListJob(DbContextHelper dbContextHelper, IBus bus)
        {
            _dbContext = dbContextHelper.GetDbContext();
            _bus = bus;
        }

        public async Task Execute()
        {
            Console.WriteLine("SearchListJob.Execute got called");
            var now = DateTime.UtcNow;
            var lastDayOfMonth = new DateTime(now.Year, now.Month, 1).AddMonths(1).AddDays(-1).Day;


            var baseQuery = _dbContext.Plannings.Where(x =>
                (x.RepeatUntil == null || DateTime.UtcNow <= x.RepeatUntil) &&
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
                                ((x.DayOfMonth <= now.Day || now.Day == lastDayOfMonth) &&
                                 ((now.Month - x.LastExecutedTime.Value.Month) + 12 * (now.Year - x.LastExecutedTime.Value.Year) >= x.RepeatEvery))));
            
//            Console.WriteLine($"Daily lists query: {dailyListsQuery.ToSql()}");
//            Console.WriteLine($"Weekly lists query: {weeklyListsQuery.ToSql()}");
//            Console.WriteLine($"Monthly lists query: {monthlyListsQuery.ToSql()}");

            var dailyLists = await dailyListsQuery.ToListAsync();
            var weeklyLists = await weeklyListsQuery.ToListAsync();
            var monthlyLists = await monthlyListsQuery.ToListAsync();

            Console.WriteLine($"Found {dailyLists.Count} daily lists");
            Console.WriteLine($"Found {weeklyLists.Count} weekly lists");
            Console.WriteLine($"Found {monthlyLists.Count} monthly lists");

            var scheduledItemLists = new List<Planning>();
            scheduledItemLists.AddRange(dailyLists);
            scheduledItemLists.AddRange(weeklyLists);
            scheduledItemLists.AddRange(monthlyLists);

            foreach (var list in scheduledItemLists)
            {
                list.LastExecutedTime = now;
                await list.Update(_dbContext);

                await _bus.SendLocal(new ScheduledItemExecuted(list.Id));

                Console.WriteLine($"List {list.Name} executed");
            }
        }
    }
}