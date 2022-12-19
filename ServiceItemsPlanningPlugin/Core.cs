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

namespace ServiceItemsPlanningPlugin
{
    using System;
    using System.ComponentModel.Composition;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using Castle.MicroKernel.Registration;
    using Castle.Windsor;
    using Infrastructure.Helpers;
    using Installers;
    using Messages;
    using Microsoft.EntityFrameworkCore;
    using Microting.eForm.Dto;
    using Microting.ItemsPlanningBase.Infrastructure.Data;
    using Microting.ItemsPlanningBase.Infrastructure.Data.Factories;
    using Microting.WindowsService.BasePn;
    using Rebus.Bus;
    using Scheduler.Jobs;

    [Export(typeof(ISdkEventHandler))]
    public class Core : ISdkEventHandler
    {
        private eFormCore.Core _sdkCore;
        private IWindsorContainer _container;
        private IBus _bus;
        // private bool _coreThreadRunning = false;
        private bool _coreStatChanging;
        private bool _coreAvailable;
        private string _serviceLocation;
        private static int _maxParallelism = 1;
        private static int _numberOfWorkers = 1;
        private ItemsPlanningPnDbContext _dbContext;
        private Timer _scheduleTimer;
        private DbContextHelper _dbContextHelper;

        public void CoreEventException(object sender, EventArgs args)
        {
            // Do nothing
        }

        public void UnitActivated(object sender, EventArgs args)
        {
            // Do nothing
        }

        public void eFormProcessed(object sender, EventArgs args)
        {
            CaseDto trigger = (CaseDto)sender;

            int? caseId = trigger.MicrotingUId;
            if (caseId != null) _bus.SendLocal(new EformParsedByServer((int) caseId));
        }

        public void eFormProcessingError(object sender, EventArgs args)
        {
            CaseDto trigger = (CaseDto)sender;

            int? caseId = trigger.MicrotingUId;
            if (caseId != null) _bus.SendLocal(new EformParsingError((int) caseId));
        }

        public void eFormRetrived(object sender, EventArgs args)
        {
            CaseDto trigger = (CaseDto)sender;

            int? caseId = trigger.MicrotingUId;
            if (caseId != null) _bus.SendLocal(new eFormRetrieved((int) caseId));
        }

        public void CaseCompleted(object sender, EventArgs args)
        {
            CaseDto trigger = (CaseDto)sender;

            if (trigger.MicrotingUId != null)
            {
                _bus.SendLocal(new eFormCompleted(trigger.CaseId, trigger.MicrotingUId, trigger.CheckUId));
            }
        }

        public void CaseDeleted(object sender, EventArgs args)
        {
            // Do nothing
        }

        public void NotificationNotFound(object sender, EventArgs args)
        {
            // Do nothing
        }

        public bool Start(string sdkConnectionString, string serviceLocation)
        {
            Console.WriteLine("ServiceItemsPlanningPlugin start called");
            try
            {
                var dbNameSection = Regex.Match(sdkConnectionString, @"(Database=\w*;)").Groups[0].Value;
                var dbPrefix = Regex.Match(sdkConnectionString, @"Database=(\d*)_").Groups[1].Value;

                var pluginDbName = $"Database={dbPrefix}_eform-angular-items-planning-plugin;";
                var connectionString = sdkConnectionString.Replace(dbNameSection, pluginDbName);

                if (!_coreAvailable && !_coreStatChanging)
                {
                    _serviceLocation = serviceLocation;
                    _coreStatChanging = true;

                    if (string.IsNullOrEmpty(_serviceLocation))
                        throw new ArgumentException("serviceLocation is not allowed to be null or empty");

                    if (string.IsNullOrEmpty(connectionString))
                        throw new ArgumentException("serverConnectionString is not allowed to be null or empty");

                    ItemsPlanningPnContextFactory contextFactory = new ItemsPlanningPnContextFactory();

                    _dbContext = contextFactory.CreateDbContext(new[] { connectionString });
                    _dbContext.Database.Migrate();

                    _dbContextHelper = new DbContextHelper(connectionString);

                    _coreAvailable = true;
                    _coreStatChanging = false;

                    StartSdkCoreSqlOnly(sdkConnectionString);
                    Console.WriteLine($"Connection string: {sdkConnectionString}");

                    var rabbitmqHost = _sdkCore.GetSdkSetting(Settings.rabbitMqHost).GetAwaiter().GetResult();
                    Console.WriteLine($"rabbitmqHost: {rabbitmqHost}");

                    string temp = _dbContext.PluginConfigurationValues
                        .SingleOrDefault(x => x.Name == "ItemsPlanningBaseSettings:MaxParallelism")?.Value;
                    _maxParallelism = string.IsNullOrEmpty(temp) ? 1 : int.Parse(temp);

                    temp = _dbContext.PluginConfigurationValues
                        .SingleOrDefault(x => x.Name == "ItemsPlanningBaseSettings:NumberOfWorkers")?.Value;
                    _numberOfWorkers = string.IsNullOrEmpty(temp) ? 1 : int.Parse(temp);


                    _container = new WindsorContainer();
                    _container.Register(Component.For<IWindsorContainer>().Instance(_container));
                    _container.Register(Component.For<DbContextHelper>().Instance(_dbContextHelper));
                    _container.Register(Component.For<eFormCore.Core>().Instance(_sdkCore));
                    _container.Install(
                        new RebusHandlerInstaller()
                        , new RebusInstaller(connectionString, _maxParallelism, _numberOfWorkers, "admin", "password", rabbitmqHost)
                    );
                    _container.Register(Component.For<SearchListJob>());

                    _bus = _container.Resolve<IBus>();

                    ConfigureScheduler();
                }
                Console.WriteLine("ServiceItemsPlanningPlugin started");
                return true;
            }
            catch(Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.WriteLine("Start failed " + ex.Message);
                throw;
            }
        }

        public bool Stop(bool shutdownReallyFast)
        {
            if (_coreAvailable && !_coreStatChanging)
            {
                _coreStatChanging = true;

                _coreAvailable = false;

                _sdkCore.Close();

                _coreStatChanging = false;
            }

            _scheduleTimer.Dispose();

            return true;
        }

        public bool Restart(int sameExceptionCount, int sameExceptionCountMax, bool shutdownReallyFast)
        {
            return true;
        }

        private void StartSdkCoreSqlOnly(string sdkConnectionString)
        {
            _sdkCore = new eFormCore.Core();

            _sdkCore.StartSqlOnly(sdkConnectionString);
        }

        private void ConfigureScheduler()
        {
            var job = _container.Resolve<SearchListJob>();

            _scheduleTimer = new Timer(async x =>
            {
                await job.Execute();
            }, null, TimeSpan.Zero, TimeSpan.FromMinutes(60));
        }
    }
}