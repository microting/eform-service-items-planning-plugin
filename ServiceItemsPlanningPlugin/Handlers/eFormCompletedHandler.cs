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

using System;
using Microting.eForm.Dto;
using Microting.eForm.Infrastructure;
using Microting.eForm.Infrastructure.Data.Entities;
using Microting.eForm.Infrastructure.Models;
using Microting.ItemsPlanningBase.Infrastructure.Enums;

namespace ServiceItemsPlanningPlugin.Handlers;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Infrastructure.Helpers;
using Messages;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.ItemsPlanningBase.Infrastructure.Data;
using Microting.ItemsPlanningBase.Infrastructure.Data.Entities;
using Rebus.Handlers;

public class EFormCompletedHandler : IHandleMessages<eFormCompleted>
{
    private readonly eFormCore.Core _sdkCore;
    private readonly ItemsPlanningPnDbContext _dbContext;

    public EFormCompletedHandler(eFormCore.Core sdkCore, DbContextHelper dbContextHelper)
    {
        _dbContext = dbContextHelper.GetDbContext();
        _sdkCore = sdkCore;
    }

    public async Task Handle(eFormCompleted message)
    {
        await using MicrotingDbContext sdkDbContext = _sdkCore.DbContextHelper.GetDbContext();
        Console.WriteLine($"info: Checking PlanningCaseSites with MicrotingSdkCaseId == {message.caseId}");
        Console.WriteLine($"info: Checking Cases with Id == {message.caseId}");
        Console.WriteLine($"info: Checking Cases with MicrotingCheckUid == {message.CheckId}");
        var planningCaseSite =
            await _dbContext.PlanningCaseSites.FirstOrDefaultAsync(x => x.MicrotingSdkCaseId == message.caseId);
        var dbCase = await sdkDbContext.Cases.FirstOrDefaultAsync(x => x.Id == message.caseId) ?? await sdkDbContext.Cases.FirstOrDefaultAsync(x => x.MicrotingCheckUid == message.CheckId);

        if (planningCaseSite == null)
        {
            Console.WriteLine($"info: Checking CheckListSites with MicrotingUid == {message.MicrotingUId}");
            var checkListSite = await sdkDbContext.CheckListSites.FirstOrDefaultAsync(x =>
                x.MicrotingUid == message.MicrotingUId);
            if (checkListSite != null)
            {
                Console.WriteLine($"info: Checking PlanningCaseSites with MicrotingCheckListSitId == {checkListSite.Id}");
                planningCaseSite =
                    await _dbContext.PlanningCaseSites.FirstOrDefaultAsync(x =>
                        x.MicrotingCheckListSitId == checkListSite.Id);
            }
        }
        if (planningCaseSite != null)
        {
            Planning planning =
                await _dbContext.Plannings.FirstAsync(x => x.Id == planningCaseSite.PlanningId);
            Site site = await sdkDbContext.Sites.FirstAsync(x => x.Id == dbCase.SiteId);
            Language language = await sdkDbContext.Languages.FirstAsync(x => x.Id == site.LanguageId);
            if (dbCase.MicrotingUid != null && dbCase.MicrotingCheckUid != null)
            {
                ReplyElement theCase =
                    await _sdkCore.CaseRead((int)dbCase.MicrotingUid, (int)dbCase.MicrotingCheckUid, language);

                if (planning.RepeatType == RepeatType.Day && planning.RepeatEvery == 0)
                {
                    var planningCase =
                        await _dbContext.PlanningCases.FirstOrDefaultAsync(x =>
                            x.Id == planningCaseSite.PlanningCaseId);
                    if (planningCase != null && planningCase.Status != 100)
                    {
                        planningCase.Status = 100;
                        planningCase.MicrotingSdkCaseDoneAt = theCase.DoneAt;
                        planningCase.MicrotingSdkCaseId = dbCase.Id;
                        planningCase.DoneByUserId = theCase.DoneById;
                        planningCase.DoneByUserName = site.Name;
                        planningCase.MicrotingSdkeFormId = (int)dbCase.CheckListId;
                        planningCase.WorkflowState = Constants.WorkflowStates.Processed;

                        planningCase = await SetFieldValue(planningCase, theCase.Id, language);
                        await planningCase.Update(_dbContext);
                    }
                    else
                    {
                        if (planningCase == null || planningCase.MicrotingSdkCaseId != dbCase.Id)
                        {
                            planningCase = new PlanningCase
                            {
                                Status = 100,
                                MicrotingSdkCaseDoneAt = theCase.DoneAt,
                                MicrotingSdkCaseId = dbCase.Id,
                                DoneByUserId = theCase.DoneById,
                                DoneByUserName = site.Name,
                                WorkflowState = Constants.WorkflowStates.Processed,
                                MicrotingSdkeFormId = (int)dbCase.CheckListId,
                                PlanningId = planning.Id
                            };
                            await planningCase.Create(_dbContext);
                            planningCase = await SetFieldValue(planningCase, theCase.Id, language);
                            await planningCase.Update(_dbContext);
                        }
                    }
                }
                else
                {
                    planningCaseSite.Status = 100;
                    planningCaseSite = await SetFieldValue(planningCaseSite, theCase.Id, language);

                    planningCaseSite.MicrotingSdkCaseDoneAt = theCase.DoneAt;
                    planningCaseSite.DoneByUserId = theCase.DoneById;
                    var worker = await sdkDbContext.Workers.FirstAsync(x => x.Id == planningCaseSite.DoneByUserId);
                    planningCaseSite.DoneByUserName = $"{worker.FirstName} {worker.LastName}";
                    await planningCaseSite.Update(_dbContext);

                    var planningCase =
                        await _dbContext.PlanningCases.FirstOrDefaultAsync(x =>
                            x.Id == planningCaseSite.PlanningCaseId);
                    if (planningCase.Status != 100)
                    {
                        planningCase.Status = 100;
                        planningCase.MicrotingSdkCaseDoneAt = theCase.DoneAt;
                        planningCase.MicrotingSdkCaseId = dbCase.Id;
                        planningCase.DoneByUserId = theCase.DoneById;
                        planningCase.DoneByUserName = site.Name;
                        planningCase.WorkflowState = Constants.WorkflowStates.Processed;
                        // planningCase.DoneByUserName = $"{site.Result.FirstName} {site.Result.LastName}";

                        planningCase = await SetFieldValue(planningCase, theCase.Id, language);
                        await planningCase.Update(_dbContext);
                    }

                    planning.DoneInPeriod = true;
                    await planning.Update(_dbContext);

                    await RetractFromMicroting(planningCase.Id);
                }
            }
        }
    }

    private async Task RetractFromMicroting(int itemCaseId)
    {
        var planningCaseSites =
            _dbContext.PlanningCaseSites.Where(x => x.PlanningCaseId == itemCaseId).ToList();

        foreach (var caseSite in planningCaseSites)
        {
            var caseDto = await _sdkCore.CaseReadByCaseId(caseSite.MicrotingSdkCaseId);
            if (caseDto.MicrotingUId != null) await _sdkCore.CaseDelete((int)caseDto.MicrotingUId);
            caseSite.WorkflowState = Constants.WorkflowStates.Retracted;
            await caseSite.Update(_dbContext);
        }
    }

    private async Task<PlanningCaseSite> SetFieldValue(PlanningCaseSite planningCaseSite, int caseId,
        Language language)
    {
        var planning = _dbContext.Plannings.FirstOrDefault(x => x.Id == planningCaseSite.PlanningId);
        var caseIds = new List<int>
        {
            planningCaseSite.MicrotingSdkCaseId
        };

        var fieldValues = await _sdkCore.Advanced_FieldValueReadList(caseIds, language);

        if (planning == null) return planningCaseSite;
        if (planning.NumberOfImagesEnabled)
        {
            planningCaseSite.NumberOfImages = 0;
            foreach (var fieldValue in fieldValues)
            {
                if (fieldValue.FieldType == Constants.FieldTypes.Picture)
                {
                    if (fieldValue.UploadedData != null)
                    {
                        planningCaseSite.NumberOfImages += 1;
                    }
                }
            }
        }

        return planningCaseSite;
    }

    private async Task<PlanningCase> SetFieldValue(PlanningCase planningCase, int caseId, Language language)
    {
        var planning = await _dbContext.Plannings.FirstOrDefaultAsync(x => x.Id == planningCase.PlanningId).ConfigureAwait(false);
        var caseIds = new List<int> { planningCase.MicrotingSdkCaseId };
        var fieldValues = await _sdkCore.Advanced_FieldValueReadList(caseIds, language).ConfigureAwait(false);

        if (planning == null) return planningCase;
        if (planning.NumberOfImagesEnabled)
        {
            planningCase.NumberOfImages = 0;
            foreach (var fieldValue in fieldValues)
            {
                if (fieldValue.FieldType == Constants.FieldTypes.Picture)
                {
                    if (fieldValue.UploadedData != null)
                    {
                        planningCase.NumberOfImages += 1;
                    }
                }
            }
        }

        return planningCase;
    }
}