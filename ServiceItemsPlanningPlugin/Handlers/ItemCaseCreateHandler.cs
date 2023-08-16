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

using System.Globalization;
using System.Threading;
using Microting.eForm.Infrastructure;
using Microting.eForm.Infrastructure.Data.Entities;
using Microting.eFormApi.BasePn.Infrastructure.Helpers;
using Microting.ItemsPlanningBase.Infrastructure.Enums;
using ServiceBackendConfigurationPlugin.Resources;

namespace ServiceItemsPlanningPlugin.Handlers;

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

public class ItemCaseCreateHandler : IHandleMessages<PlanningCaseCreate>
{
    private readonly ItemsPlanningPnDbContext _dbContext;
    private readonly eFormCore.Core _sdkCore;

    public ItemCaseCreateHandler(eFormCore.Core sdkCore, DbContextHelper dbContextHelper)
    {
        _sdkCore = sdkCore;
        _dbContext = dbContextHelper.GetDbContext();
    }

    public async Task Handle(PlanningCaseCreate message)
    {
        var planning = await _dbContext.Plannings
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .FirstOrDefaultAsync(x => x.Id == message.PlanningId);
        await using MicrotingDbContext microtingDbContext = _sdkCore.DbContextHelper.GetDbContext();
        if (planning != null)
        {
            var siteIds = _dbContext.PlanningSites
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed
                            && x.PlanningId == planning.Id)
                .Select(x => x.SiteId)
                .ToList();

            var planningCases = await _dbContext.PlanningCases
                .Where(x => x.PlanningId == planning.Id
                            && x.WorkflowState != Constants.WorkflowStates.Retracted
                            && x.WorkflowState != Constants.WorkflowStates.Removed)
                .ToListAsync();
            foreach (PlanningCase cPlanningCase in planningCases)
            {
                cPlanningCase.WorkflowState = Constants.WorkflowStates.Retracted;
                await cPlanningCase.Update(_dbContext);
            }

            PlanningCase planningCase = new PlanningCase()
            {
                PlanningId = planning.Id,
                Status = 66,
                MicrotingSdkeFormId = message.RelatedEFormId
            };
            await planningCase.Create(_dbContext);

            foreach (var siteId in siteIds)
            {
                var casesToDelete = await _dbContext.PlanningCaseSites.
                    Where(x => x.PlanningId == planning.Id
                               && x.MicrotingSdkSiteId == siteId
                               && x.WorkflowState != Constants.WorkflowStates.Retracted).ToListAsync();
                Log.LogEvent($"ItemCaseCreateHandler.Task: Found {casesToDelete.Count} PlanningCaseSites, which has not yet been retracted, so retracting now.");

                foreach (var caseToDelete in casesToDelete)
                {
                    Log.LogEvent($"ItemCaseCreateHandler.Task: Trying to retract the case with Id: {caseToDelete.Id}");
                    var sdkCase = await microtingDbContext.Cases.FirstOrDefaultAsync(x => x.Id == caseToDelete.MicrotingSdkCaseId);
                    if (sdkCase is { MicrotingUid: { } })
                    {
                        await _sdkCore.CaseDelete((int)sdkCase.MicrotingUid);
                    }
                    //var caseDto = await _sdkCore.CaseLookupCaseId(caseToDelete.MicrotingSdkCaseId);
                    //if (caseDto.MicrotingUId != null) await _sdkCore.CaseDelete((int) caseDto.MicrotingUId);
                    caseToDelete.WorkflowState = Constants.WorkflowStates.Retracted;
                    await caseToDelete.Update(_dbContext);
                }

                Site sdkSite = await microtingDbContext.Sites.FirstAsync(x => x.Id == siteId);
                Language language = await microtingDbContext.Languages.FirstAsync(x => x.Id == sdkSite.LanguageId);
                CultureInfo ci = new CultureInfo(language.LanguageCode);
                var mainElement = await _sdkCore.ReadeForm(message.RelatedEFormId, language);
                var translation = _dbContext.PlanningNameTranslation
                    .First(x => x.LanguageId == language.Id && x.PlanningId == planning.Id).Name;
                var folderId = microtingDbContext.Folders.First(x => x.Id == planning.SdkFolderId).MicrotingUid.ToString();

                mainElement.Label = string.IsNullOrEmpty(planning.PlanningNumber) ? "" : planning.PlanningNumber;
                mainElement.StartDate = DateTime.Now.ToUniversalTime();
                if (!string.IsNullOrEmpty(translation))
                {
                    mainElement.Label += string.IsNullOrEmpty(mainElement.Label) ? $"{translation}" : $" - {translation}";
                }

                if (!string.IsNullOrEmpty(planning.BuildYear))
                {
                    mainElement.Label += string.IsNullOrEmpty(mainElement.Label) ? $"{planning.BuildYear}" : $" - {planning.BuildYear}";
                }

                if (!string.IsNullOrEmpty(planning.Type))
                {
                    mainElement.Label += string.IsNullOrEmpty(mainElement.Label) ? $"{planning.Type}" : $" - {planning.Type}";
                }

                if (planning.RepeatType == RepeatType.Day && planning.RepeatEvery == 1)
                {
                    mainElement.Label = $"{mainElement.StartDate.ToString("dddd dd. MMM yyyy", ci)} - {mainElement.Label}";
                }

                if (mainElement.ElementList.Count == 1)
                {
                    mainElement.ElementList[0].Label = mainElement.Label;
                }
                mainElement.CheckListFolderName = folderId;
                // mainElement.EndDate = DateTime.Now.AddYears(10).ToUniversalTime();
                mainElement.EndDate = (DateTime) planning.NextExecutionTime;

                if (planning.ShowExpireDate)
                {
                    if (planning.NextExecutionTime != null)
                    {
                        DateTime beginningOfTime = new DateTime(2020, 1, 1);
                        mainElement.DisplayOrder = ((DateTime)planning.NextExecutionTime - beginningOfTime).Days;
                        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(language.LanguageCode);
                        Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language.LanguageCode);

                        if (string.IsNullOrEmpty(mainElement.ElementList[0].Description.InderValue))
                        {
                            mainElement.ElementList[0].Description.InderValue =
                                $"<strong>{Translations.Deadline}: {((DateTime)planning.NextExecutionTime).AddDays(-1).ToString("dd.MM.yyyy")}</strong>";
                        }
                        else
                        {
                            mainElement.ElementList[0].Description.InderValue +=
                                $"<br><strong>{Translations.Deadline}: {((DateTime)planning.NextExecutionTime).AddDays(-1).ToString("dd.MM.yyyy")}</strong>";
                        }
                    }
                }


                var planningCaseSite =
                    await _dbContext.PlanningCaseSites.FirstOrDefaultAsync(x => x.PlanningCaseId == planningCase.Id && x.MicrotingSdkSiteId == siteId);

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
                    long unixTimestamp = (long)planningCaseSite.MicrotingSdkCaseDoneAt.Value
                        .Subtract(new DateTime(1970, 1, 1))
                        .TotalSeconds;

                    mainElement.ElementList[0].Description.InderValue = unixTimestamp.ToString();
                }

                if (planningCaseSite.MicrotingSdkCaseId >= 1) continue;
                // if (planning.PushMessageOnDeployment)
                // {
                //     if (planning.RepeatType == RepeatType.Day && planning.RepeatEvery < 2)
                //     { }
                //     else
                //     {
                var folder = await GetTopFolderName((int) planning.SdkFolderId, microtingDbContext);
                string body = "";
                if (folder != null)
                {
                    planning.SdkFolderId = microtingDbContext.Folders
                        .FirstOrDefault(y => y.Id == planning.SdkFolderId).Id;
                    FolderTranslation folderTranslation =
                        await microtingDbContext.FolderTranslations.FirstOrDefaultAsync(x =>
                            x.FolderId == folder.Id && x.LanguageId == sdkSite.LanguageId);
                    body = $"{folderTranslation.Name} ({sdkSite.Name};{DateTime.Now:dd.MM.yyyy})";
                }

                PlanningNameTranslation planningNameTranslation =
                    await _dbContext.PlanningNameTranslation.FirstOrDefaultAsync(x =>
                        x.PlanningId == planning.Id
                        && x.LanguageId == sdkSite.LanguageId);

                mainElement.PushMessageBody = body;
                mainElement.PushMessageTitle = planningNameTranslation.Name;
                //     }
                //
                // }

                if (mainElement.EndDate > DateTime.UtcNow)
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

    private async Task<Folder> GetTopFolderName(int folderId, MicrotingDbContext dbContext)
    {
        var result = await dbContext.Folders.FirstAsync(y => y.Id == folderId);
        if (result.ParentId != null)
        {
            result = await GetTopFolderName((int)result.ParentId, dbContext);
        }
        return result;
    }
}