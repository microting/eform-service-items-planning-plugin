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
using Microting.eForm.Infrastructure.Data.Entities;
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

    public class ItemCaseSingleCreateHandler : IHandleMessages<PlanningCaseSingleCreate>
    {
        private readonly ItemsPlanningPnDbContext _dbContext;
        private readonly eFormCore.Core _sdkCore;

        public ItemCaseSingleCreateHandler(eFormCore.Core sdkCore, DbContextHelper dbContextHelper)
        {
            _sdkCore = sdkCore;
            _dbContext = dbContextHelper.GetDbContext();
        }

        public async Task Handle(PlanningCaseSingleCreate message)
        {
            var planning = await _dbContext.Plannings.SingleOrDefaultAsync(x => x.Id == message.PlanningId);
            var siteId = message.PlanningSiteId;
            await using MicrotingDbContext sdkDbContext = _sdkCore.DbContextHelper.GetDbContext();
            var sdkSite = await sdkDbContext.Sites.SingleAsync(x => x.Id == siteId);
            Language language = await sdkDbContext.Languages.SingleAsync(x => x.Id == sdkSite.LanguageId);
            var mainElement = await _sdkCore.ReadeForm(message.RelatedEFormId, language);

            var folder = await sdkDbContext.Folders.SingleAsync(x => x.Id == planning.SdkFolderId);
            var folderId = folder.MicrotingUid.ToString();

            var planningCase = await _dbContext.PlanningCases
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Where(x => x.PlanningId == planning.Id)
                .Where(x => x.Status == 66)
                .Where(x => x.MicrotingSdkeFormId == message.RelatedEFormId)
                .SingleOrDefaultAsync();

            if (planningCase == null)
            {
                planningCase = new PlanningCase()
                {
                    PlanningId = planning.Id,
                    Status = 66,
                    MicrotingSdkeFormId = message.RelatedEFormId
                };
                await planningCase.Create(_dbContext);
            }

            var casesToDelete = await _dbContext.PlanningCaseSites
                .Where(x => x.PlanningId == planning.Id
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

            var translation = _dbContext.PlanningNameTranslation
                .Single(x => x.LanguageId == language.Id && x.PlanningId == planning.Id).Name;

            mainElement.Label = string.IsNullOrEmpty(planning.PlanningNumber) ? "" : planning.PlanningNumber;
            if (!string.IsNullOrEmpty(translation))
            {
                mainElement.Label += string.IsNullOrEmpty(mainElement.Label) ? $"{translation}" : $" - {translation}";
            }

            if (!string.IsNullOrEmpty(planning.BuildYear))
            {
                mainElement.Label += string.IsNullOrEmpty(mainElement.Label)
                    ? $"{planning.BuildYear}"
                    : $" - {planning.BuildYear}";
            }

            if (!string.IsNullOrEmpty(planning.Type))
            {
                mainElement.Label += string.IsNullOrEmpty(mainElement.Label) ? $"{planning.Type}" : $" - {planning.Type}";
            }

            mainElement.ElementList[0].Label = mainElement.Label;
            mainElement.CheckListFolderName = folderId;
            mainElement.StartDate = DateTime.Now.ToUniversalTime();
            mainElement.EndDate = DateTime.Now.AddYears(10).ToUniversalTime();
            // mainElement.PushMessageBody = mainElement.Label;
            // mainElement.PushMessageTitle = folder.Name;
            // if (folder.ParentId != null)
            // {
            //     var parentFolder = await sdkDbContext.Folders.SingleAsync(x => x.Id == folder.ParentId);
            //     mainElement.PushMessageTitle = parentFolder.Name;
            //     mainElement.PushMessageBody = $"{folder.Name}\n{mainElement.Label}";
            // }

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
                    PlanningId = planning.Id,
                    PlanningCaseId = planningCase.Id
                };

                await planningCaseSite.Create(_dbContext);
            }

            if (planningCaseSite.MicrotingSdkCaseDoneAt.HasValue)
            {
                long unixTimestamp = (long) (planningCaseSite.MicrotingSdkCaseDoneAt.Value
                        .Subtract(new DateTime(1970, 1, 1)))
                    .TotalSeconds;

                mainElement.ElementList[0].Description.InderValue = unixTimestamp.ToString();
            }

            if (planningCaseSite.MicrotingSdkCaseId < 1)
            {
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