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

using System.Runtime.InteropServices;
using Sentry;

namespace ServiceItemsPlanningPlugin;

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
        SentrySdk.Init(options =>
        {
            // A Sentry Data Source Name (DSN) is required.
            // See https://docs.sentry.io/product/sentry-basics/dsn-explainer/
            // You can set it in the SENTRY_DSN environment variable, or you can set it in code here.
            options.Dsn = "https://02c3a53dede99c38d555a4fd98b9d16c@o4506241219428352.ingest.sentry.io/4506294303588352";

            // When debug is enabled, the Sentry client will emit detailed debugging information to the console.
            // This might be helpful, or might interfere with the normal operation of your application.
            // We enable it here for demonstration purposes when first trying Sentry.
            // You shouldn't do this in your applications unless you're troubleshooting issues with Sentry.
            options.Debug = false;

            // This option is recommended. It enables Sentry's "Release Health" feature.
            options.AutoSessionTracking = true;

            // Enabling this option is recommended for client applications only. It ensures all threads use the same global scope.
            options.IsGlobalModeEnabled = false;

            // Example sample rate for your transactions: captures 10% of transactions
            options.TracesSampleRate = 0.1;
        });
        Console.WriteLine("info: ServiceItemsPlanningPlugin start called");
        try
        {
            var dbNameSection = Regex.Match(sdkConnectionString, @"(Database=\w*;)").Groups[0].Value;
            var dbPrefix = Regex.Match(sdkConnectionString, @"Database=(\d*)_").Groups[1].Value;

            var pluginDbName = $"Database={dbPrefix}_eform-angular-items-planning-plugin;";
            var connectionString = sdkConnectionString.Replace(dbNameSection, pluginDbName);
            string pattern = @"Database=(\d+)_Angular;";

            int number = int.Parse(dbPrefix);
            SentrySdk.ConfigureScope(scope =>
            {
                scope.SetTag("customerNo", number.ToString());
                Console.WriteLine("info: customerNo: " + number);
                scope.SetTag("osVersion", Environment.OSVersion.ToString());
                Console.WriteLine("info: osVersion: " + Environment.OSVersion);
                scope.SetTag("osArchitecture", RuntimeInformation.OSArchitecture.ToString());
                Console.WriteLine("info: osArchitecture: " + RuntimeInformation.OSArchitecture);
                scope.SetTag("osName", RuntimeInformation.OSDescription);
                Console.WriteLine("info: osName: " + RuntimeInformation.OSDescription);
            });

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
                Console.WriteLine($"info: Connection string: {sdkConnectionString}");

                var rabbitmqHost = _sdkCore.GetSdkSetting(Settings.rabbitMqHost).GetAwaiter().GetResult();
                Console.WriteLine($"info: rabbitmqHost: {rabbitmqHost}");
                var rabbitMqUser = _sdkCore.GetSdkSetting(Settings.rabbitMqUser).GetAwaiter().GetResult();
                Console.WriteLine($"info: rabbitMqUser: {rabbitMqUser}");
                var rabbitMqPassword = _sdkCore.GetSdkSetting(Settings.rabbitMqPassword).GetAwaiter().GetResult();
                Console.WriteLine($"info: rabbitMqPassword: {rabbitMqPassword}");

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
                    , new RebusInstaller(dbPrefix, connectionString, _maxParallelism, _numberOfWorkers, rabbitMqUser, rabbitMqPassword, rabbitmqHost)
                );
                _container.Register(Component.For<SearchListJob>());

                _bus = _container.Resolve<IBus>();

                ConfigureScheduler();
            }
            Console.WriteLine("info: ServiceItemsPlanningPlugin started");
            return true;
        }
        catch(Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkRed;
            Console.WriteLine("fail: Start failed " + ex.Message);
            throw;
        }
    }

    public bool Stop(bool shutdownReallyFast)
    {
        if (_coreAvailable && !_coreStatChanging)
        {
            _coreStatChanging = true;

            _coreAvailable = false;

            _sdkCore.Close().GetAwaiter().GetResult();

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

        _sdkCore.StartSqlOnly(sdkConnectionString).GetAwaiter().GetResult();
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