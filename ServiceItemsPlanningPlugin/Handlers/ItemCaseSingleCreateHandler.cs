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

using Microting.eForm.Infrastructure;
using Microting.eFormApi.BasePn.Infrastructure.Helpers;

namespace ServiceItemsPlanningPlugin.Handlers
{
    using System;
    using System.Linq;
    using System.Threading.Tasks;
    using Infrastructure.Helpers;
    using Messages;
    using Microsoft.EntityFrameworkCore;
    using Microting.eForm.Infrastructure.Constants;
    using Microting.ItemsPlanningBase.Infrastructure.Data;
    using Microting.ItemsPlanningBase.Infrastructure.Data.Entities;
    using Rebus.Handlers;

    public class ItemCaseSingleCreateHandler : IHandleMessages<ItemCaseSingleCreate>
    {
        private readonly ItemsPlanningPnDbContext _dbContext;
        private readonly eFormCore.Core _sdkCore;

        public ItemCaseSingleCreateHandler(eFormCore.Core sdkCore, DbContextHelper dbContextHelper)
        {
            _sdkCore = sdkCore;
            _dbContext = dbContextHelper.GetDbContext();
        }

        public async Task Handle(ItemCaseSingleCreate message)
        {
            var item = await _dbContext.Items.SingleOrDefaultAsync(x => x.Id == message.ItemId);
            var mainElement = await _sdkCore.TemplateRead(message.RelatedEFormId);

            await using MicrotingDbContext dbContext = _sdkCore.dbContextHelper.GetDbContext();
            var folderId = dbContext.folders.Single(x => x.Id == item.eFormSdkFolderId).MicrotingUid.ToString();

            var planningCase = await _dbContext.PlanningCases
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Where(x => x.ItemId == item.Id)
                .Where(x => x.Status == 66)
                .Where(x => x.MicrotingSdkeFormId == message.RelatedEFormId)
                .SingleOrDefaultAsync();

            if (planningCase == null)
            {
                planningCase = new PlanningCase()
                {
                    ItemId = item.Id,
                    Status = 66,
                    MicrotingSdkeFormId = message.RelatedEFormId
                };
                await planningCase.Create(_dbContext);
            }

            var siteId = message.PlanningSiteId;
            var casesToDelete = await _dbContext.PlanningCaseSites
                .Where(x => x.ItemId == item.Id
                            && x.MicrotingSdkSiteId == siteId
                            && x.WorkflowState !=
                            Constants.WorkflowStates.Retracted)
                .ToListAsync();
            Log.LogEvent(
                $"ItemCaseCreateHandler.Task: Found {casesToDelete.Count} PlanningCaseSites, which has not yet been retracted, so retracting now.");

            foreach (var caseToDelete in casesToDelete)
            {
                Log.LogEvent($"ItemCaseCreateHandler.Task: Trying to retract the case with Id: {caseToDelete.Id}");
                var caseDto = await _sdkCore.CaseLookupCaseId(caseToDelete.MicrotingSdkCaseId);
                if (caseDto.MicrotingUId != null) await _sdkCore.CaseDelete((int) caseDto.MicrotingUId);
                caseToDelete.WorkflowState = Constants.WorkflowStates.Retracted;
                await caseToDelete.Update(_dbContext);
            }

            mainElement.Label = string.IsNullOrEmpty(item.ItemNumber) ? "" : item.ItemNumber;
            if (!string.IsNullOrEmpty(item.Name))
            {
                mainElement.Label += string.IsNullOrEmpty(mainElement.Label) ? $"{item.Name}" : $" - {item.Name}";
            }

            if (!string.IsNullOrEmpty(item.BuildYear))
            {
                mainElement.Label += string.IsNullOrEmpty(mainElement.Label)
                    ? $"{item.BuildYear}"
                    : $" - {item.BuildYear}";
            }

            if (!string.IsNullOrEmpty(item.Type))
            {
                mainElement.Label += string.IsNullOrEmpty(mainElement.Label) ? $"{item.Type}" : $" - {item.Type}";
            }

            mainElement.ElementList[0].Label = mainElement.Label;
            mainElement.CheckListFolderName = folderId;
            mainElement.StartDate = DateTime.Now.ToUniversalTime();
            mainElement.EndDate = DateTime.Now.AddYears(10).ToUniversalTime();

            long unixTimestamp = (long) (DateTime.UtcNow
                    .Subtract(new DateTime(1970, 1, 1)))
                .TotalSeconds;

            mainElement.ElementList[0].Description.InderValue = unixTimestamp.ToString();

            var planningCaseSite =
                await _dbContext.PlanningCaseSites.SingleOrDefaultAsync(x =>
                    x.PlanningCaseId == planningCase.Id && x.MicrotingSdkSiteId == siteId);

            if (planningCaseSite == null)
            {
                planningCaseSite = new PlanningCaseSite()
                {
                    MicrotingSdkSiteId = siteId,
                    MicrotingSdkeFormId = message.RelatedEFormId,
                    Status = 66,
                    ItemId = item.Id,
                    PlanningCaseId = planningCase.Id
                };

                await planningCaseSite.Create(_dbContext);
            }

            if (planningCaseSite.MicrotingSdkCaseId < 1)
            {
                await using var sdkDbContext = _sdkCore.dbContextHelper.GetDbContext();
                var sdkSite = await sdkDbContext.sites.SingleAsync(x => x.Id == siteId);
                var caseId = await _sdkCore.CaseCreate(mainElement, "", (int) sdkSite.MicrotingUid, null);
                if (caseId != null)
                {
                    var caseDto = await _sdkCore.CaseLookupMUId((int) caseId);
                    if (caseDto?.CaseId != null) planningCaseSite.MicrotingSdkCaseId = (int) caseDto.CaseId;
                    await planningCaseSite.Update(_dbContext);
                }
            }
        }
    }
}