﻿using eShopDashboard.Infrastructure.Data.Catalog;
using eShopDashboard.Infrastructure.Data.Ordering;
using eShopDashboard.Infrastructure.Setup;
using eShopForecast;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML;
using Microsoft.ML.Transforms.TimeSeries;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace eShopDashboard
{
    public class Program
    {
        private static int _seedingProgress = 100;

        private static Random rnd = new Random(12345);

        private readonly MLContext mlContext = new MLContext();

        private static RiskDTO risk =  new RiskDTO();

        public static IWebHost BuildWebHost(string[] args) =>
            WebHost.CreateDefaultBuilder(args)
                .UseContentRoot(Directory.GetCurrentDirectory())
                .UseStartup<Startup>()
                .UseSerilog()
                .ConfigureAppConfiguration((builderContext, config) =>
                {
                    config.AddEnvironmentVariables();
                })
                .Build();

        public static int GetSeedingProgress()
        {
            return _seedingProgress;
        }

        public static int Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .WriteTo.Seq("http://localhost:5341/")
                .CreateLogger();

            Log.Information("----- Starting web host");

            try
            {
                var host = BuildWebHost(args);

                Log.Information("----- Seeding Database");

                Task seeding = Task.Run(async () => { await ConfigureDatabaseAsync(host); });

                PopulateRiskData();

                Log.Information("----- Running Host");

                host.Run();

                Log.Information("----- Web host stopped");

                return 0;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "----- Host terminated unexpectedly");

                return 1;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static async Task ConfigureDatabaseAsync(IWebHost host)
        {
            _seedingProgress = 0;

            using (var scope = host.Services.CreateScope())
            {
                var services = scope.ServiceProvider;

                var catalogContext = services.GetService<CatalogContext>();
                await catalogContext.Database.MigrateAsync();

                var orderingContext = services.GetService<OrderingContext>();
                await orderingContext.Database.MigrateAsync();
            }

            await SeedDatabaseAsync(host);

            _seedingProgress = 100;
        }

        private static async Task SeedDatabaseAsync(IWebHost host)
        {
            try
            {
                using (var scope = host.Services.CreateScope())
                {
                    IServiceProvider services = scope.ServiceProvider;

                    Log.Information("----- Checking seeding status");

                    var catalogContextSetup = services.GetService<CatalogContextSetup>();
                    var orderingContextSetup = services.GetService<OrderingContextSetup>();

                    var catalogSeedingStatus = await catalogContextSetup.GetSeedingStatusAsync();
                    Log.Information("----- SeedingStatus ({Context}): {@SeedingStatus}", "Catalog", catalogSeedingStatus);

                    var orderingSeedingStatus = await orderingContextSetup.GetSeedingStatusAsync();
                    Log.Information("----- SeedingStatus ({Context}): {@SeedingStatus}", "Ordering", orderingSeedingStatus);

                    var seedingStatus = new SeedingStatus(catalogSeedingStatus, orderingSeedingStatus);
                    Log.Information("----- SeedingStatus ({Context}): {@SeedingStatus}", "Aggregated", seedingStatus);

                    if (!seedingStatus.NeedsSeeding)
                    {
                        Log.Information("----- No seeding needed");

                        return;
                    }

                    Log.Information("----- Seeding database");

                    var sw = new Stopwatch();
                    sw.Start();

                    void ProgressAggregator()
                    {
                        seedingStatus.RecordsLoaded = catalogSeedingStatus.RecordsLoaded + orderingSeedingStatus.RecordsLoaded;

                        Log.Debug("----- Seeding {SeedingPercentComplete}% complete", seedingStatus.PercentComplete);
                        _seedingProgress = seedingStatus.PercentComplete;
                    }

                    var catalogProgressHandler = new Progress<int>(value =>
                    {
                        catalogSeedingStatus.RecordsLoaded = value;
                        ProgressAggregator();
                    });

                    var orderingProgressHandler = new Progress<int>(value =>
                    {
                        orderingSeedingStatus.RecordsLoaded = value;
                        ProgressAggregator();
                    });

                    Log.Information("----- Seeding CatalogContext");
                    Task catalogSeedingTask = Task.Run(async () => await catalogContextSetup.SeedAsync(catalogProgressHandler));

                    Log.Information("----- Seeding OrderingContext");
                    Task orderingSeedingTask = Task.Run(async () => await orderingContextSetup.SeedAsync(orderingProgressHandler));

                    await Task.WhenAll(catalogSeedingTask, orderingSeedingTask);

                    seedingStatus.SetAsComplete();
                    _seedingProgress = seedingStatus.PercentComplete;

                    Log.Information("----- Database Seeded ({ElapsedTime:n3}s)", sw.Elapsed.TotalSeconds);
                }

            }
            catch (Exception ex)
            {
                Log.Error(ex, "----- Exception seeding database");
            }
        }

        public static void MoveByDay()
        {
            // Drop predicted
            risk.risk1.RemoveAll(d => Math.Abs(d.min) > 1e-6 && Math.Abs(d.max) > 1e-6);
            risk.riskBase1.RemoveAll(d => Math.Abs(d.min) > 1e-6 && Math.Abs(d.max) > 1e-6);
            risk.riskImpact1.RemoveAll(d => Math.Abs(d.min) > 1e-6 && Math.Abs(d.max) > 1e-6);

            risk.risk2.RemoveAll(d => Math.Abs(d.min) > 1e-6 && Math.Abs(d.max) > 1e-6);
            risk.riskBase2.RemoveAll(d => Math.Abs(d.min) > 1e-6 && Math.Abs(d.max) > 1e-6);
            risk.riskImpact2.RemoveAll(d => Math.Abs(d.min) > 1e-6 && Math.Abs(d.max) > 1e-6);

            risk.riskImpactEntity.RemoveAll(d => Math.Abs(d.min) > 1e-6 && Math.Abs(d.max) > 1e-6);

            // Drop the oldest
            risk.risk1.RemoveAll(d => Math.Abs(d.day+100.0f) < 1e-6);
            risk.riskBase1.RemoveAll(d => Math.Abs(d.day + 100.0f) < 1e-6);
            risk.riskImpact1.RemoveAll(d => Math.Abs(d.day + 100.0f) < 1e-6);

            risk.risk2.RemoveAll(d => Math.Abs(d.day + 100.0f) < 1e-6);
            risk.riskBase2.RemoveAll(d => Math.Abs(d.day + 100.0f) < 1e-6);
            risk.riskImpact2.RemoveAll(d => Math.Abs(d.day + 100.0f) < 1e-6);
            
            risk.riskImpactEntity.RemoveAll(d => Math.Abs(d.day + 100.0f) < 1e-6);

            // The last historical data
            float riskValue1lag = risk.risk1.Single(d => (d.day+1.0f) < 1e-6).riskValue;
            float riskBaseValue1lag = risk.riskBase1.Single(d => (d.day + 1.0f) < 1e-6).riskBaseValue;
            float riskValue2lag = risk.risk2.Single(d => (d.day + 1.0f) < 1e-6).riskValue;
            float riskBaseValue2lag = risk.riskBase2.Single(d => (d.day + 1.0f) < 1e-6).riskBaseValue;

            // Shift the rest by one day to the past
            risk.risk1.ForEach( d => { d.day -= 1.0f;} );
            risk.riskBase1.ForEach(d => { d.day -= 1.0f; });
            risk.riskImpact1.ForEach(d => { d.day -= 1.0f; });

            risk.risk2.ForEach(d => { d.day -= 1.0f; });
            risk.riskBase2.ForEach(d => { d.day -= 1.0f; });
            risk.riskImpact2.ForEach(d => { d.day -= 1.0f; });

            risk.riskImpactEntity.ForEach(d => { d.day -= 1.0f; });

            // Invent today (day -1.0f)
            { 
                float riskValue1 = riskValue1lag + 0.03f * (rnd.Next(0, 100) - 50);
                if (riskValue1 > 100f) riskValue1 = 100f;
                if (riskValue1 < 0f) riskValue1 = 0f;

                float riskBaseValue1 = riskBaseValue1lag + 0.01f * (rnd.Next(0, 100) - 50);
                if (riskBaseValue1 > 10f) riskBaseValue1 = 10f;
                if (riskBaseValue1 < 0f) riskBaseValue1 = 0f;

                float riskValue2 = riskValue2lag + 0.03f * (rnd.Next(0, 100) - 50);
                if (riskValue2 > 100f) riskValue2 = 100f;
                if (riskValue2 < 0f) riskValue2 = 0f;

                float riskBaseValue2 = riskBaseValue2lag + 0.01f * (rnd.Next(0, 100) - 50);
                if (riskBaseValue2 > 10f) riskBaseValue2 = 10f;
                if (riskBaseValue2 < 0f) riskBaseValue2 = 0f;

                risk.risk1.Add(new RiskData
                {
                    riskId = 1,
                    day = -1f,
                    count = 100,
                    riskValue = riskValue1
                });

                risk.riskBase1.Add(new RiskBaseData
                {
                    riskId = 2,
                    day = -1f,
                    count = 100,
                    riskBaseValue = riskBaseValue1
                });

                risk.riskImpact1.Add(new RiskImpactData
                {
                    riskId = 2,
                    day = -1f,
                    count = 100,
                    riskImpactValue = riskValue1 * riskBaseValue1
                });

                risk.risk2.Add(new RiskData
                {
                    riskId = 1,
                    day = -1f,
                    count = 100,
                    riskValue = riskValue2
                });

                risk.riskBase2.Add(new RiskBaseData
                {
                    riskId = 2,
                    day = -1f,
                    count = 100,
                    riskBaseValue = riskBaseValue2
                });

                risk.riskImpact2.Add(new RiskImpactData
                {
                    riskId = 2,
                    day = -1f,
                    count = 100,
                    riskImpactValue = riskValue2 * riskBaseValue2
                });

                risk.riskImpactEntity.Add(new RiskImpactData
                {
                    riskId = 2,
                    day = -1f,
                    count = 100,
                    riskImpactValue = riskValue1 * riskBaseValue1 + riskValue2 * riskBaseValue2
                });
            }

            // Insert predicted
            var mlContext = new MLContext();

            var risk1DataView = mlContext.Data.LoadFromEnumerable(risk.risk1);

            // Create and add the forecast estimator to the pipeline.
            IEstimator<ITransformer> risk1ForecastEstimator = 
                mlContext.Forecasting.ForecastBySsa(
                    outputColumnName: nameof(RiskPrediction.ForecastedValues),
                    inputColumnName: nameof(RiskData.riskValue), // This is the column being forecasted.
                    windowSize: 50, // Window size is set to the time period represented in the product data cycle; our product cycle is based on 12 months, so this is set to a factor of 12, e.g. 3.
                    seriesLength: 100, 
                    trainSize: 80,
                    horizon: 20, // Indicates the number of values to forecast
                    confidenceLevel: 0.95f, 
                    confidenceLowerBoundColumn: nameof(RiskPrediction.ConfidenceLowerBound), 
                    confidenceUpperBoundColumn: nameof(RiskPrediction.ConfidenceUpperBound));

            // Train the forecasting model for the specified risk's data series.
            ITransformer risk1ForecastTransformer = risk1ForecastEstimator.Fit(risk1DataView);

            // Create the forecast engine used for creating predictions.
            TimeSeriesPredictionEngine<RiskData, RiskPrediction> risk1ForecastEngine = 
                risk1ForecastTransformer.CreateTimeSeriesEngine<RiskData, RiskPrediction>(mlContext);

            // Predict
            var risk1Estimation = risk1ForecastEngine.Predict();

            // Apply estimations to risk data
            for (int i = 0; i<20; i++)
            {
                var riskValue1 = risk1Estimation.ForecastedValues[i];
                if (riskValue1 > 100f) riskValue1 = 100f;
                if (riskValue1 < 0f) riskValue1 = 0f;

                var riskValue1min = risk1Estimation.ConfidenceLowerBound[i];
                if (riskValue1min > 100f) riskValue1min = 100f;
                if (riskValue1min < 0f) riskValue1min = 0f;

                var riskValue1max = risk1Estimation.ConfidenceUpperBound[i];
                if (riskValue1max > 100f) riskValue1max = 100f;
                if (riskValue1max < 0f) riskValue1max = 0f;

                risk.risk1.Add(new RiskData
                {
                    riskId = 1,
                    day = i,
                    count = 100,
                    riskValue = riskValue1,
                    min = riskValue1min,
                    max = riskValue1max
                });

            }

            var risk2DataView = mlContext.Data.LoadFromEnumerable(risk.risk2);

            // Create and add the forecast estimator to the pipeline.
            IEstimator<ITransformer> risk2ForecastEstimator = 
                mlContext.Forecasting.ForecastBySsa(
                    outputColumnName: nameof(RiskPrediction.ForecastedValues),
                    inputColumnName: nameof(RiskData.riskValue), // This is the column being forecasted.
                    windowSize: 50, // Window size is set to the time period represented in the product data cycle; our product cycle is based on 12 months, so this is set to a factor of 12, e.g. 3.
                    seriesLength: 100, 
                    trainSize: 80,
                    horizon: 20, // Indicates the number of values to forecast
                    confidenceLevel: 0.95f, 
                    confidenceLowerBoundColumn: nameof(RiskPrediction.ConfidenceLowerBound), 
                    confidenceUpperBoundColumn: nameof(RiskPrediction.ConfidenceUpperBound));

            // Train the forecasting model for the specified risk's data series.
            ITransformer risk2ForecastTransformer = risk1ForecastEstimator.Fit(risk1DataView);

            // Create the forecast engine used for creating predictions.
            TimeSeriesPredictionEngine<RiskData, RiskPrediction> risk2ForecastEngine = 
                risk1ForecastTransformer.CreateTimeSeriesEngine<RiskData, RiskPrediction>(mlContext);

            // Predict
            var risk2Estimation = risk1ForecastEngine.Predict();

            // Apply estimations to risk data
            for (int i = 0; i<20; i++)
            {
                var riskValue2 = risk1Estimation.ForecastedValues[i];
                if (riskValue2 > 100f) riskValue2 = 100f;
                if (riskValue2 < 0f) riskValue2 = 0f;

                var riskValue2min = risk1Estimation.ConfidenceLowerBound[i];
                if (riskValue2min > 100f) riskValue2min = 100f;
                if (riskValue2min < 0f) riskValue2min = 0f;

                var riskValue2max = risk1Estimation.ConfidenceUpperBound[i];
                if (riskValue2max > 100f) riskValue2max = 100f;
                if (riskValue2max < 0f) riskValue2max = 0f;

                risk.risk2.Add(new RiskData
                {
                    riskId = 1,
                    day = i,
                    count = 100,
                    riskValue = riskValue2,
                    min = riskValue2min,
                    max = riskValue2max
                });

            }

            var riskBase1DataView = mlContext.Data.LoadFromEnumerable(risk.riskBase1);

            // Create and add the forecast estimator to the pipeline.
            IEstimator<ITransformer> riskBase1ForecastEstimator =
                mlContext.Forecasting.ForecastBySsa(
                    outputColumnName: nameof(RiskPrediction.ForecastedValues),
                    inputColumnName: nameof(RiskBaseData.riskBaseValue), // This is the column being forecasted.
                    windowSize: 50, // Window size is set to the time period represented in the product data cycle; our product cycle is based on 12 months, so this is set to a factor of 12, e.g. 3.
                    seriesLength: 100,
                    trainSize: 80,
                    horizon: 20, // Indicates the number of values to forecast
                    confidenceLevel: 0.95f,
                    confidenceLowerBoundColumn: nameof(RiskPrediction.ConfidenceLowerBound),
                    confidenceUpperBoundColumn: nameof(RiskPrediction.ConfidenceUpperBound));

            // Train the forecasting model for the specified risk's data series.
            ITransformer riskBase1ForecastTransformer = risk1ForecastEstimator.Fit(riskBase1DataView);

            // Create the forecast engine used for creating predictions.
            TimeSeriesPredictionEngine<RiskBaseData, RiskPrediction> riskBase1ForecastEngine =
                risk1ForecastTransformer.CreateTimeSeriesEngine<RiskBaseData, RiskPrediction>(mlContext);

            // Predict
            var riskBase1Estimation = riskBase1ForecastEngine.Predict();

            // Apply estimations to risk data
            for (int i = 0; i < 20; i++)
            {
                var riskBaseValue1 = riskBase1Estimation.ForecastedValues[i];
                if (riskBaseValue1 > 10f) riskBaseValue1 = 10f;
                if (riskBaseValue1 < 0f) riskBaseValue1 = 0f;

                var riskBaseValue1min = riskBase1Estimation.ConfidenceLowerBound[i];
                if (riskBaseValue1min > 10f) riskBaseValue1min = 10f;
                if (riskBaseValue1min < 0f) riskBaseValue1min = 0f;

                var riskBaseValue1max = riskBase1Estimation.ConfidenceUpperBound[i];
                if (riskBaseValue1max > 10f) riskBaseValue1max = 10f;
                if (riskBaseValue1max < 0f) riskBaseValue1max = 0f;

                risk.riskBase1.Add(new RiskBaseData
                {
                    riskId = 1,
                    day = i,
                    count = 100,
                    riskBaseValue = riskBaseValue1,
                    min = riskBaseValue1min,
                    max = riskBaseValue1max
                });

            }

            var riskBase2DataView = mlContext.Data.LoadFromEnumerable(risk.riskBase2);

            // Create and add the forecast estimator to the pipeline.
            IEstimator<ITransformer> riskBase2ForecastEstimator =
                mlContext.Forecasting.ForecastBySsa(
                    outputColumnName: nameof(RiskPrediction.ForecastedValues),
                    inputColumnName: nameof(RiskBaseData.riskBaseValue), // This is the column being forecasted.
                    windowSize: 50, // Window size is set to the time period represented in the product data cycle; our product cycle is based on 12 months, so this is set to a factor of 12, e.g. 3.
                    seriesLength: 100,
                    trainSize: 80,
                    horizon: 20, // Indicates the number of values to forecast
                    confidenceLevel: 0.95f,
                    confidenceLowerBoundColumn: nameof(RiskPrediction.ConfidenceLowerBound),
                    confidenceUpperBoundColumn: nameof(RiskPrediction.ConfidenceUpperBound));

            // Train the forecasting model for the specified risk's data series.
            ITransformer riskBase2ForecastTransformer = risk1ForecastEstimator.Fit(riskBase1DataView);

            // Create the forecast engine used for creating predictions.
            TimeSeriesPredictionEngine<RiskBaseData, RiskPrediction> riskBase2ForecastEngine =
                risk1ForecastTransformer.CreateTimeSeriesEngine<RiskBaseData, RiskPrediction>(mlContext);

            // Predict
            var riskBase2Estimation = riskBase1ForecastEngine.Predict();

            // Apply estimations to risk data
            for (int i = 0; i < 20; i++)
            {
                var riskBaseValue2 = riskBase2Estimation.ForecastedValues[i];
                if (riskBaseValue2 > 10f) riskBaseValue2 = 10f;
                if (riskBaseValue2 < 0f) riskBaseValue2 = 0f;

                var riskBaseValue2min = riskBase2Estimation.ConfidenceLowerBound[i];
                if (riskBaseValue2min > 10f) riskBaseValue2min = 10f;
                if (riskBaseValue2min < 0f) riskBaseValue2min = 0f;

                var riskBaseValue2max = riskBase2Estimation.ConfidenceUpperBound[i];
                if (riskBaseValue2max > 10f) riskBaseValue2max = 10f;
                if (riskBaseValue2max < 0f) riskBaseValue2max = 0f;

                risk.riskBase1.Add(new RiskBaseData
                {
                    riskId = 1,
                    day = i,
                    count = 100,
                    riskBaseValue = riskBaseValue2,
                    min = riskBaseValue2min,
                    max = riskBaseValue2max
                });

            }

            // Insert impact predictions
            for (int i = 0; i < 20; i++)
            {
                var riskValue1 = risk.risk1.Single(d => Math.Abs(d.day - i) < 1e-6).riskValue;
                var riskValue1min = risk.risk1.Single(d => Math.Abs(d.day - i) < 1e-6).min;
                var riskValue1max = risk.risk1.Single(d => Math.Abs(d.day - i) < 1e-6).max;
                
                var riskBaseValue1 = risk.riskBase1.Single(d => Math.Abs(d.day - i) < 1e-6).riskBaseValue;
                var riskBaseValue1min = risk.riskBase1.Single(d => Math.Abs(d.day - i) < 1e-6).min;
                var riskBaseValue1max = risk.riskBase1.Single(d => Math.Abs(d.day - i) < 1e-6).max;

                var riskValue2 = risk.risk2.Single(d => Math.Abs(d.day - i) < 1e-6).riskValue;
                var riskValue2min = risk.risk2.Single(d => Math.Abs(d.day - i) < 1e-6).min;
                var riskValue2max = risk.risk2.Single(d => Math.Abs(d.day - i) < 1e-6).max;

                var riskBaseValue2 = risk.riskBase2.Single(d => Math.Abs(d.day - i) < 1e-6).riskBaseValue;
                var riskBaseValue2min = risk.riskBase2.Single(d => Math.Abs(d.day - i) < 1e-6).min;
                var riskBaseValue2max = risk.riskBase2.Single(d => Math.Abs(d.day - i) < 1e-6).max;

                float riskImpactValue1 = riskValue1 * riskBaseValue1;
                float riskImpactValue1min = riskValue1min * riskBaseValue1min;
                float riskImpactValue1max = riskValue1max * riskBaseValue1max;
                float riskImpactValue2 = riskValue2 * riskBaseValue2;
                float riskImpactValue2min = riskValue2min * riskBaseValue2min;
                float riskImpactValue2max = riskValue2max * riskBaseValue2max;

                float riskImpactEntityValue = riskImpactValue1 + riskImpactValue2;
                float riskImpactEntityValuemin = riskImpactValue1min + riskImpactValue2max;
                float riskImpactEntityValuemax = riskImpactValue1max + riskImpactValue2max;

                risk.riskImpact1.Add(new RiskImpactData
                {
                    riskId = 2,
                    day = i,
                    count = 100,
                    riskImpactValue = riskValue1 * riskBaseValue1
                });

                risk.riskImpact2.Add(new RiskImpactData
                {
                    riskId = 2,
                    day = i,
                    count = 100,
                    riskImpactValue = riskValue2 * riskBaseValue2
                });

                risk.riskImpactEntity.Add(new RiskImpactData
                {
                    riskId = 2,
                    day = i,
                    count = 100,
                    riskImpactValue = riskValue1 * riskBaseValue1 + riskValue2 * riskBaseValue2
                });
            }
        }

        private static void PopulateRiskData()
        {

            float riskValue1lag = rnd.Next(0, 100);
            float riskBaseValue1lag = rnd.Next(0, 100) / 10.0f;
            float riskValue2lag = rnd.Next(0, 100);
            float riskBaseValue2lag = rnd.Next(0, 100) / 10.0f;

            for (int i = 0; i < 100; i++)
            {
                float riskValue1 = riskValue1lag + 0.03f * (rnd.Next(0, 100) - 50);
                if (riskValue1 > 100f) riskValue1 = 100f;
                if (riskValue1 < 0f) riskValue1 = 0f;

                float riskBaseValue1 = riskBaseValue1lag + 0.01f * (rnd.Next(0, 100) - 50);
                if (riskBaseValue1 > 10f) riskBaseValue1 = 10f;
                if (riskBaseValue1 < 0f) riskBaseValue1 = 0f;

                float riskValue2 = riskValue2lag + 0.03f * (rnd.Next(0, 100) - 50);
                if (riskValue2 > 100f) riskValue2 = 100f;
                if (riskValue2 < 0f) riskValue2 = 0f;

                float riskBaseValue2 = riskBaseValue2lag + 0.01f * (rnd.Next(0, 100) - 50);
                if (riskBaseValue2 > 10f) riskBaseValue2 = 10f;
                if (riskBaseValue2 < 0f) riskBaseValue2 = 0f;


                riskValue1lag = riskValue1;
                riskBaseValue1lag = riskBaseValue1;
                riskValue2lag = riskValue2;
                riskBaseValue2lag = riskBaseValue2;

                risk.risk1.Add(new RiskData
                {
                    riskId = 1,
                    day = -100 + i,
                    count = 100,
                    riskValue = riskValue1
                });

                risk.riskBase1.Add(new RiskBaseData
                {
                    riskId = 2,
                    day = -100 + i,
                    count = 100,
                    riskBaseValue = riskBaseValue1
                });

                risk.riskImpact1.Add(new RiskImpactData
                {
                    riskId = 2,
                    day = -100 + i,
                    count = 100,
                    riskImpactValue = riskValue1 * riskBaseValue1
                });

                risk.risk2.Add(new RiskData
                {
                    riskId = 1,
                    day = -100 + i,
                    count = 100,
                    riskValue = riskValue2
                });

                risk.riskBase2.Add(new RiskBaseData
                {
                    riskId = 2,
                    day = -100 + i,
                    count = 100,
                    riskBaseValue = riskBaseValue2
                });

                risk.riskImpact2.Add(new RiskImpactData
                {
                    riskId = 2,
                    day = -100 + i,
                    count = 100,
                    riskImpactValue = riskValue2 * riskBaseValue2
                });

                risk.riskImpactEntity.Add(new RiskImpactData
                {
                    riskId = 2,
                    day = -100 + i,
                    count = 100,
                    riskImpactValue = riskValue1 * riskBaseValue1 + riskValue2 * riskBaseValue2
                });

            }

            // Future

            for (int i = 0; i < 20; i++)
            {
                float riskValue1 = rnd.Next(0, 100);
                float riskValue1min = riskValue1 - (i + 1);
                float riskValue1max = riskValue1 + (i + 1);
                float riskValue2 = rnd.Next(0, 100);
                float riskValue2min = riskValue2 - (i + 1);
                float riskValue2max = riskValue2 + (i + 1);

                float riskBaseValue1 = rnd.Next(0, 100) / 10.0f;
                float riskBaseValue1min = riskBaseValue1 - (i + 1) / 10.0f;
                float riskBaseValue1max = riskBaseValue1 + (i + 1) / 10.0f;
                float riskBaseValue2 = rnd.Next(0, 100) / 10.0f;
                float riskBaseValue2min = riskBaseValue2 - (i + 1) / 10.0f;
                float riskBaseValue2max = riskBaseValue2 + (i + 1) / 10.0f;

                float riskImpactValue1 = riskValue1 * riskBaseValue1;
                float riskImpactValue1min = riskValue1min * riskBaseValue1min;
                float riskImpactValue1max = riskValue1max * riskBaseValue1max;
                float riskImpactValue2 = riskValue2 * riskBaseValue2;
                float riskImpactValue2min = riskValue2min * riskBaseValue2min;
                float riskImpactValue2max = riskValue2max * riskBaseValue2max;

                float riskImpactEntityValue = riskImpactValue1 + riskImpactValue2;
                float riskImpactEntityValuemin = riskImpactValue1min + riskImpactValue2max;
                float riskImpactEntityValuemax = riskImpactValue1max + riskImpactValue2max;

                risk.risk1.Add(new RiskData
                {
                    riskId = 1,
                    day = i,
                    count = 100,
                    riskValue = riskValue1,
                    min = riskValue1min,
                    max = riskValue1max
                });

                risk.riskBase1.Add(new RiskBaseData
                {
                    riskId = 2,
                    day = i,
                    count = 100,
                    riskBaseValue = riskBaseValue1,
                    min = riskBaseValue1min,
                    max = riskBaseValue1max
                });

                risk.riskImpact1.Add(new RiskImpactData
                {
                    riskId = 2,
                    day = i,
                    count = 100,
                    riskImpactValue = riskImpactValue1,
                    min = riskImpactValue1min,
                    max = riskImpactValue1max
                });

                risk.risk2.Add(new RiskData
                {
                    riskId = 1,
                    day = i,
                    count = 100,
                    riskValue = riskValue2,
                    min = riskValue2min,
                    max = riskValue2max
                });

                risk.riskBase2.Add(new RiskBaseData
                {
                    riskId = 2,
                    day = i,
                    count = 100,
                    riskBaseValue = riskBaseValue2,
                    min = riskBaseValue2min,
                    max = riskBaseValue2max
                });

                risk.riskImpact2.Add(new RiskImpactData
                {
                    riskId = 2,
                    day = i,
                    count = 100,
                    riskImpactValue = riskImpactValue2,
                    min = riskImpactValue2min,
                    max = riskImpactValue2max
                });

                risk.riskImpactEntity.Add(new RiskImpactData
                {
                    riskId = 2,
                    day = i,
                    count = 100,
                    riskImpactValue = riskImpactEntityValue,
                    min = riskImpactEntityValuemin,
                    max = riskImpactEntityValuemax
                });

            }
        }

        public static RiskDTO GetRiskData()
            => risk;
    }
}