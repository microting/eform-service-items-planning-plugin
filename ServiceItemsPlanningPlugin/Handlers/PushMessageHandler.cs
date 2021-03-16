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

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure;
using Microting.eForm.Infrastructure.Data.Entities;
using Microting.ItemsPlanningBase.Infrastructure.Data;
using Microting.ItemsPlanningBase.Infrastructure.Data.Entities;
using Rebus.Handlers;
using ServiceItemsPlanningPlugin.Infrastructure.Helpers;
using ServiceItemsPlanningPlugin.Messages;

namespace ServiceItemsPlanningPlugin.Handlers
{
    public class PushMessageHandler : IHandleMessages<PushMessage>
    {
        private readonly eFormCore.Core _sdkCore;
        private readonly ItemsPlanningPnDbContext _dbContext;

        public PushMessageHandler(eFormCore.Core sdkCore, DbContextHelper dbContextHelper)
        {
            _dbContext = dbContextHelper.GetDbContext();
            _sdkCore = sdkCore;
        }
        
        public async Task Handle(PushMessage message)
        {
            Planning planning = await _dbContext.Plannings.SingleOrDefaultAsync(x => x.Id == message.PlanningId);
            if (planning != null)
            {
                await using MicrotingDbContext microtingDbContext = _sdkCore.DbContextHelper.GetDbContext();
                List<PlanningSite> planningSites =
                await _dbContext.PlanningSites.Where(x => x.PlanningId == message.PlanningId).ToListAsync();

                foreach (PlanningSite planningSite in planningSites)
                {
                    Site site = await microtingDbContext.Sites.SingleOrDefaultAsync(x => x.Id == planningSite.SiteId);
                    if (site != null)
                    {
                        PlanningNameTranslation planningNameTranslation =
                            await _dbContext.PlanningNameTranslation.SingleOrDefaultAsync(x =>
                                x.PlanningId == planning.Id
                                && x.LanguageId == site.LanguageId);
                        
                        var folder = microtingDbContext.Folders
                            .Include(x => x.Parent)
                            .Select(x => new
                            {
                                x.Name,
                                x.Parent,
                                x.Id,
                            })
                            .FirstOrDefault(y => y.Id == planning.SdkFolderId);
                        string body = "";
                        if (folder != null)
                        {
                            planning.SdkFolderId = folder.Id;
                            if (folder.Parent != null)
                            {
                                body = folder.Parent.Name + " - " + folder.Name;
                            }
                            else
                            {
                                body = folder.Name;
                            }
                        }
                        await _sdkCore.SendPushMessage(site.Id, planningNameTranslation.Name, body);
                    }
                }

                planning.PushMessageSent = true;
                await planning.Update(_dbContext);
            }
        }
    }
}