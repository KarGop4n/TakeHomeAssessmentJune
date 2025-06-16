using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;
using TradingAlertSystem.Core.Interfaces;
using TradingAlertSystem.Infrastructure.Notifications;
using TradingAlertSystem.Infrastructure.Providers;
using TradingAlertSystem.Services;

namespace TradingAlertSystem.Console;

class Program
{
    static async Task Main(string[] args)
    {
        // Run with dotnet run --continuous for continuous mode
        var runContinuous = args.Contains("--continuous") || args.Contains("-c");

        var host = CreateHostBuilder(args).Build();

        using var scope = host.Services.CreateScope();
        var tradingAlertService = scope.ServiceProvider.GetRequiredService<ITradingAlertService>();

        var cancellationTokenSource = new CancellationTokenSource();

        System.Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            System.Console.WriteLine("\n🛑 Graceful shutdown initiated...");
            cancellationTokenSource.Cancel();
        };

        try
        {
            if (runContinuous)
            {
                // Continuous monitoring mode
                await tradingAlertService.StartContinuousMonitoringAsync(cancellationTokenSource.Token);
            }
            else
            {
                await RunSingleCycleMode(tradingAlertService, cancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
            System.Console.WriteLine("Operation was cancelled");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Fatal error: {ex.Message}");
            var logger = scope.ServiceProvider.GetService<ILogger<Program>>();
            logger?.LogCritical(ex, "Application terminated unexpectedly");
        }
    }

    private static async Task RunSingleCycleMode(ITradingAlertService tradingAlertService, CancellationToken cancellationToken)
    {
        System.Console.WriteLine("Trading Alert System Starting (Single Run Mode)...");
        System.Console.WriteLine("=====================================");
        System.Console.WriteLine("Use --continuous or -c flag for production continuous monitoring");
        System.Console.WriteLine();

        var signals = await tradingAlertService.RunSingleMonitoringCycleAsync(cancellationToken);

        System.Console.WriteLine("\nSingle monitoring cycle complete.");
        System.Console.WriteLine($"Total new signals processed: {signals.Count}");
        System.Console.WriteLine("Add --continuous flag to run continuously");
        System.Console.WriteLine("Press any key to exit...");
        System.Console.ReadKey();
    }

    static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                config.AddEnvironmentVariables();
                config.AddCommandLine(args);
            })
            .ConfigureServices((context, services) =>
            {
                var configuration = context.Configuration;

                // Register configuration sections
                services.Configure<SlackSettings>(configuration.GetSection("Slack"));
                services.Configure<EmailSettings>(configuration.GetSection("Email"));
                services.Configure<DetectionSettings>(configuration.GetSection("Detection"));
                services.Configure<MonitoringSettings>(configuration.GetSection("Monitoring"));
                services.Configure<NotificationSettings>(configuration.GetSection("Notification"));

                // Register HTTP clients with Polly retries for each pipeline provider
                services.AddHttpClient<AnrPipelineDataProvider>(client =>
                {
                    client.Timeout = TimeSpan.FromMinutes(2);
                    client.DefaultRequestHeaders.Add("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                })
                    .AddPolicyHandler(GetRetryPolicy());

                services.AddHttpClient<ColumbiaGulfCsvProvider>(client =>
                {
                    client.Timeout = TimeSpan.FromMinutes(3);
                    client.DefaultRequestHeaders.Add("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                })
                    .AddPolicyHandler(GetRetryPolicy());

                services.AddHttpClient<CreoleTrailPipelineDataProvider>(client =>
                {
                    client.Timeout = TimeSpan.FromMinutes(2);
                    client.DefaultRequestHeaders.Add("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                    client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
                    client.DefaultRequestHeaders.Add("Referer", "https://lngconnection.cheniere.com/");
                    client.DefaultRequestHeaders.Add("Origin", "https://lngconnection.cheniere.com");
                })
                    .AddPolicyHandler(GetRetryPolicy());

                services.AddHttpClient<TetcoPipelineDataProvider>(client =>
                {
                    client.Timeout = TimeSpan.FromMinutes(3);
                    client.DefaultRequestHeaders.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                })
                    .AddPolicyHandler(GetRetryPolicy());

                services.AddHttpClient<GulfSouthPipelineDataProvider>(client =>
                {
                    client.Timeout = TimeSpan.FromMinutes(2);
                })
                    .AddPolicyHandler(GetRetryPolicy());

                services.AddHttpClient<SabinePipelineDataProvider>(client =>
                {
                    client.Timeout = TimeSpan.FromMinutes(2);
                    client.DefaultRequestHeaders.Add("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                })
                    .AddPolicyHandler(GetRetryPolicy());

                services.AddHttpClient<NGPLPipelineDataProvider>(client =>
                {
                    client.Timeout = TimeSpan.FromMinutes(2);
                    client.DefaultRequestHeaders.Add("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                })
                    .AddPolicyHandler(GetRetryPolicy());

                services.AddHttpClient<SlackNotificationService>()
                    .AddPolicyHandler(GetRetryPolicy());

                // Register pipeline data providers
                services.AddScoped<IPipelineDataProvider, AnrPipelineDataProvider>();
                services.AddScoped<IPipelineDataProvider, ColumbiaGulfCsvProvider>();
                services.AddScoped<IPipelineDataProvider, CreoleTrailPipelineDataProvider>();
                services.AddScoped<IPipelineDataProvider, TetcoPipelineDataProvider>();
                services.AddScoped<IPipelineDataProvider, GulfSouthPipelineDataProvider>();
                services.AddScoped<IPipelineDataProvider, SabinePipelineDataProvider>();
                services.AddScoped<IPipelineDataProvider, NGPLPipelineDataProvider>();

                // Register notification services
                services.AddScoped<SlackNotificationService>();
                services.AddScoped<NotificationOrchestrator>();
                services.AddScoped<EmailDigestService>(); 

                services.AddScoped<ISignalDetectionService, SignalDetectionService>();
                services.AddScoped<IPipelineMonitoringService, PipelineMonitoringService>();
                services.AddScoped<ITradingAlertService, TradingAlertService>();

                services.AddSingleton<NotificationTrackingService>(); // Singleton to persist notification state        

                // Register logging
                services.AddLogging(builder =>
                {
                    builder.AddConsole();
                    builder.SetMinimumLevel(LogLevel.Information);
                });
            });

    // Retry setup
    static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    System.Console.WriteLine($"Retry {retryCount} after {timespan} seconds");
                });
    }
}