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

using Microting.eForm.Dto;
using Microting.eForm.Infrastructure;
using Microting.eForm.Infrastructure.Models;

namespace ServiceItemsPlanningPlugin.Handlers
{
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
            var planningCaseSite = await _dbContext.PlanningCaseSites.SingleOrDefaultAsync(x => x.MicrotingSdkCaseId == message.caseId);
            using MicrotingDbContext sdkDbContext = _sdkCore.dbContextHelper.GetDbContext();
            if (planningCaseSite != null)
            {
                planningCaseSite.Status = 100;
                CaseDto caseDto = await _sdkCore.CaseReadByCaseId(message.caseId);
                var microtingUId = caseDto.MicrotingUId;
                var microtingCheckUId = caseDto.CheckUId;
                if (microtingUId != null && microtingCheckUId != null)
                {
                    ReplyElement theCase = await _sdkCore.CaseRead((int)microtingUId, (int)microtingCheckUId);

                    planningCaseSite = await SetFieldValue(planningCaseSite, theCase.Id);

                    planningCaseSite.MicrotingSdkCaseDoneAt = theCase.DoneAt;
                    planningCaseSite.DoneByUserId = theCase.DoneById;
                    var worker = await sdkDbContext.workers.SingleAsync(x => x.Id == planningCaseSite.DoneByUserId);
                    planningCaseSite.DoneByUserName = $"{worker.FirstName} {worker.LastName}";
                    await planningCaseSite.Update(_dbContext);

                    var planningCase = await _dbContext.PlanningCases.SingleOrDefaultAsync(x => x.Id == planningCaseSite.PlanningCaseId);
                    if (planningCase.Status != 100)
                    {
                        planningCase.Status = 100;
                        planningCase.MicrotingSdkCaseDoneAt = theCase.DoneAt;
                        planningCase.MicrotingSdkCaseId = planningCaseSite.MicrotingSdkCaseId;
                        planningCase.DoneByUserId = theCase.DoneById;
                        planningCase.WorkflowState = Constants.WorkflowStates.Processed;
                        // planningCase.DoneByUserName = $"{site.Result.FirstName} {site.Result.LastName}";

                        planningCase = await SetFieldValue(planningCase, theCase.Id);
                        await planningCase.Update(_dbContext);
                    }
                
                    await RetractFromMicroting(planningCase.Id);
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
                if (caseDto.MicrotingUId != null) await _sdkCore.CaseDelete((int) caseDto.MicrotingUId);
            }
        }

        private async Task<PlanningCaseSite> SetFieldValue(PlanningCaseSite itemCaseSite, int caseId)
        {
            var item = _dbContext.Items.SingleOrDefault(x => x.Id == itemCaseSite.ItemId);
            var planning = _dbContext.Plannings.SingleOrDefault(x => x.Id == item.PlanningId);
            var caseIds = new List<int>
            {
                itemCaseSite.MicrotingSdkCaseId
            };

            var fieldValues = await _sdkCore.Advanced_FieldValueReadList(caseIds);

            if (planning == null) return itemCaseSite;

            if (planning.SdkFieldEnabled1)
            {
                itemCaseSite.SdkFieldValue1 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId1)?.ValueReadable;
            }
            if (planning.SdkFieldEnabled2)
            {
                itemCaseSite.SdkFieldValue2 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId2)?.ValueReadable;
            }
            if (planning.SdkFieldEnabled3)
            {
                itemCaseSite.SdkFieldValue3 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId3)?.ValueReadable;
            }
            if (planning.SdkFieldEnabled4)
            {
                itemCaseSite.SdkFieldValue4 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId4)?.ValueReadable;
            }
            if (planning.SdkFieldEnabled5)
            {
                itemCaseSite.SdkFieldValue5 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId5)?.ValueReadable;
            }
            if (planning.SdkFieldEnabled6)
            {
                itemCaseSite.SdkFieldValue6 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId6)?.ValueReadable;
            }
            if (planning.SdkFieldEnabled7)
            {
                itemCaseSite.SdkFieldValue7 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId7)?.ValueReadable;
            }
            if (planning.SdkFieldEnabled8)
            {
                itemCaseSite.SdkFieldValue8 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId8)?.ValueReadable;
            }
            if (planning.SdkFieldEnabled9)
            {
                itemCaseSite.SdkFieldValue9 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId9)?.ValueReadable;
            }
            if (planning.SdkFieldEnabled10)
            {
                itemCaseSite.SdkFieldValue10 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId10)?.ValueReadable;
            }
            if (planning.NumberOfImagesEnabled)
            {
                itemCaseSite.NumberOfImages = 0;
                foreach (var fieldValue in fieldValues)
                {
                    if (fieldValue.FieldType == Constants.FieldTypes.Picture)
                    {
                        if (fieldValue.UploadedData != null)
                        {
                            itemCaseSite.NumberOfImages += 1;
                        }
                    }
                }
            }

            return itemCaseSite;
        }

        private async Task<PlanningCase> SetFieldValue(PlanningCase itemCase, int caseId)
        {
            var item = _dbContext.Items.SingleOrDefault(x => x.Id == itemCase.ItemId);
            var planning = _dbContext.Plannings.SingleOrDefault(x => x.Id == item.PlanningId);
            var caseIds = new List<int>();
            caseIds.Add(itemCase.MicrotingSdkCaseId);
            var fieldValues = await _sdkCore.Advanced_FieldValueReadList(caseIds);

            if (planning == null) return itemCase;

            if (planning.SdkFieldEnabled1)
            {
                itemCase.SdkFieldValue1 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId1)?.ValueReadable;
            }
            if (planning.SdkFieldEnabled2)
            {
                itemCase.SdkFieldValue2 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId2)?.ValueReadable;
            }
            if (planning.SdkFieldEnabled3)
            {
                itemCase.SdkFieldValue3 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId3)?.ValueReadable;
            }
            if (planning.SdkFieldEnabled4)
            {
                itemCase.SdkFieldValue4 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId4)?.ValueReadable;
            }
            if (planning.SdkFieldEnabled5)
            {
                itemCase.SdkFieldValue5 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId5)?.ValueReadable;
            }
            if (planning.SdkFieldEnabled6)
            {
                itemCase.SdkFieldValue6 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId6)?.ValueReadable;
            }
            if (planning.SdkFieldEnabled7)
            {
                itemCase.SdkFieldValue7 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId7)?.ValueReadable;
            }
            if (planning.SdkFieldEnabled8)
            {
                itemCase.SdkFieldValue8 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId8)?.ValueReadable;
            }
            if (planning.SdkFieldEnabled9)
            {
                itemCase.SdkFieldValue9 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId9)?.ValueReadable;
            }
            if (planning.SdkFieldEnabled10)
            {
                itemCase.SdkFieldValue10 =
                    fieldValues.SingleOrDefault(x => x.FieldId == planning.SdkFieldId10)?.ValueReadable;
            }
            if (planning.NumberOfImagesEnabled)
            {
                itemCase.NumberOfImages = 0;
                foreach (var fieldValue in fieldValues)
                {
                    if (fieldValue.FieldType == Constants.FieldTypes.Picture)
                    {
                        if (fieldValue.UploadedData != null)
                        {
                            itemCase.NumberOfImages += 1;
                        }
                    }
                }
            }

            return itemCase;
        }
    }
}