using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Bot.MorningJob.Console.Integrations.SFTP;
using Bot.MorningJob.Console.Integrations.Int002;
using Bot.MorningJob.Console.Integrations.Database;
using Bot.MorningJob.Console.Integrations.Workday;
using Serilog;
using System.Reflection;

namespace Bot.MorningJob.Console
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Setup configuration
            var appBasePath = AppContext.BaseDirectory;
            var configuration = new ConfigurationBuilder()
                .SetBasePath(appBasePath)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development"}.json", optional: true)
                .AddUserSecrets<Program>(optional: true)
                .AddEnvironmentVariables()
                .Build();

            // Setup Serilog
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File("logs/bot-morningjob-.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();

            try
            {
                Log.Information("Bot MorningJob starting up...");
                
                // Setup dependency injection
                var services = new ServiceCollection();
                ConfigureServices(services, configuration);
                var serviceProvider = services.BuildServiceProvider();

                // Get the main application service
                var app = serviceProvider.GetRequiredService<IApplicationService>();
                await app.ExecuteAsync();

                Log.Information("Bot MorningJob completed successfully");
             }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Bot MorningJob terminated unexpectedly");
                System.Console.WriteLine($"Fatal error: {ex.Message}");
                Environment.Exit(1);
            }
            finally
            {
                await Log.CloseAndFlushAsync();
            }
        }

        private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
            // Add configuration sections
            services.Configure<AppSettings>(options => configuration.GetSection("AppSettings").Bind(options));

            // Add logging
            services.AddLogging(configure =>
            {
                configure.AddSerilog();
            });

            // Register services
            services.AddScoped<ISftpService, SftpService>();
            services.AddScoped<IDatabaseService, DatabaseService>();
            services.AddScoped<IWorkdayService, WorkdayService>();
            services.AddScoped<IInt002SyncService, Int002SyncService>();

            // Application service
            services.AddScoped<IApplicationService, ApplicationService>();
        }
    }

    /// <summary>
    /// Main application service that orchestrates all jobs
    /// </summary>
    public interface IApplicationService
    {
        Task ExecuteAsync();
    }

    /// <summary>
    /// Implementation of the main application service
    /// </summary>
    public class ApplicationService : IApplicationService
    {
        private readonly ILogger<ApplicationService> _logger;
        private readonly ISftpService _sftpService;
        private readonly IInt002SyncService _int002SyncService;

        public ApplicationService(ILogger<ApplicationService> logger, ISftpService sftpService, IInt002SyncService int002SyncService)
        {
            _logger = logger;
            _sftpService = sftpService;
            _int002SyncService = int002SyncService;
        }

        public async Task ExecuteAsync()
        {
            _logger.LogInformation("Starting morning job execution...");

            try
            {
                // TODO: Add job execution logic here
                _logger.LogInformation("Morning job workflow started");

                // Step 1: INT002 SFTP sync to local folders + SQL Server tracking
                var int002Result = await _int002SyncService.SyncAsync();
                _logger.LogInformation(
                    "INT002 sync completed. JobRunId: {JobRunId}. New processed files: {ProcessedCount}. New failed files: {FailedCount}",
                    int002Result.JobRunId,
                    int002Result.NewProcessedFiles.Count,
                    int002Result.NewFailedFiles.Count);

                foreach (var fileName in int002Result.NewProcessedFiles)
                {
                    _logger.LogInformation("Copied processed file: {FileName}", fileName);
                }

                foreach (var fileName in int002Result.NewFailedFiles)
                {
                    _logger.LogInformation("Copied failed file: {FileName}", fileName);
                }

                // Step 2: AWS S3 bucket checks
                // TODO: Implement AWS S3 checks

                // Step 3: AWS SQS queue checks
                // TODO: Implement AWS SQS checks

                // Step 4: Workday API integration checks
                // TODO: Implement Workday API checks

                // Step 5: PostgreSQL database query
                // TODO: Implement PostgreSQL checks

                _logger.LogInformation("Morning job workflow completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during morning job execution");
                throw;
            }

            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Application settings configuration class
    /// </summary>
    public class AppSettings
    {
        public SftpSettings? Sftp { get; set; }
        public AwsSettings? Aws { get; set; }
        public WorkdaySettings? Workday { get; set; }
        public DatabaseSettings? Database { get; set; }
        public ReportSettings? Report { get; set; }
        public Int002SyncSettings? Int002Sync { get; set; }
    }

    public class SftpSettings
    {
        public string? Host { get; set; }
        public int Port { get; set; } = 22;
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? PrivateKeyPath { get; set; }
        public string? PrivateKeyWorkingCopyPath { get; set; }
        public string? ProcessedDirectory { get; set; }
        public string? FailedDirectory { get; set; }
        public string? LocalDownloadRootPath { get; set; }
        public string? LastInspectionStatePath { get; set; }
        public List<string>? MonitoredDirectories { get; set; }
    }

    public class AwsSettings
    {
        public string? AccessKeyId { get; set; }
        public string? SecretAccessKey { get; set; }
        public string? Region { get; set; }
        public S3Settings? S3 { get; set; }
        public SqsSettings? Sqs { get; set; }
    }

    public class S3Settings
    {
        public string? BucketName { get; set; }
        public List<string>? MonitoredPrefixes { get; set; }
    }

    public class SqsSettings
    {
        public string? QueueUrl { get; set; }
        public int MaxMessages { get; set; } = 10;
    }

    public class WorkdaySettings
    {
        public string? TenantName { get; set; }
        public string? BaseUrl { get; set; }
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
        public string? RefreshToken { get; set; }
        public string? TokenUrl { get; set; }
        public string? ReportApiUrl { get; set; }
        public string? MomentOffset { get; set; } = "-07:00";
        public int LookbackHours { get; set; } = 24;
        public List<string>? IntegrationNames { get; set; }
    }

    public class DatabaseSettings
    {
        public string? ConnectionString { get; set; }
        public string? Host { get; set; }
        public int Port { get; set; } = 5432;
        public string? Database { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? QueryTable { get; set; }
        public string? PgWebBaseUrl { get; set; }
    }

    public class ReportSettings
    {
        public string? OutputPath { get; set; }
        public string? EmailRecipients { get; set; }
        public string? ReportFormat { get; set; } = "html";
    }

    public class Int002SyncSettings
    {
        public string? LocalRootPath { get; set; }
        public string? SqlConnectionString { get; set; }
        public string? WorkUploadRootPath { get; set; }
    }
}
