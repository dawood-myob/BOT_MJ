using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Bot.MorningJob.Console.Integrations.SFTP;
using Bot.MorningJob.Console.Integrations.Int002;
using Bot.MorningJob.Console.Integrations.Database;
using Bot.MorningJob.Console.Integrations.Workday;
using Bot.MorningJob.Console.Integrations.Teams;
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

                if (args.Any(a => string.Equals(a, "--rollback-last-run", StringComparison.OrdinalIgnoreCase)))
                {
                    var int002SyncService = serviceProvider.GetRequiredService<IInt002SyncService>();
                    var rolledBack = await int002SyncService.RollbackLastRunAsync();
                    Log.Information(rolledBack
                        ? "Rollback of last run completed successfully."
                        : "Rollback of last run did not find any run to roll back.");
                    return;
                }

                // Posts an existing report text file to Teams without running any integrations or
                // touching job-run state (used to test the card/webhook safely).
                var postFileIndex = Array.FindIndex(args, a => string.Equals(a, "--post-report-file", StringComparison.OrdinalIgnoreCase));
                if (postFileIndex >= 0)
                {
                    if (postFileIndex + 1 >= args.Length || !File.Exists(args[postFileIndex + 1]))
                    {
                        Log.Error("--post-report-file requires the path of an existing report text file.");
                        return;
                    }

                    var teamsNotifier = serviceProvider.GetRequiredService<ITeamsNotifier>();
                    var dryRun = args.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));
                    var posted = await teamsNotifier.PostReportAsync(await File.ReadAllTextAsync(args[postFileIndex + 1]), dryRun);
                    if (!dryRun)
                    {
                        Log.Information(posted ? "Report posted to Teams." : "Report was not posted to Teams.");
                    }
                    return;
                }

                // Read-only SQS check (no database/file/job-run changes, no Teams post).
                if (args.Any(a => string.Equals(a, "--check-queues", StringComparison.OrdinalIgnoreCase)))
                {
                    var queueLines = await serviceProvider.GetRequiredService<IInt002SyncService>().CheckQueuesAsync();
                    foreach (var line in queueLines)
                    {
                        System.Console.WriteLine(line);
                    }

                    return;
                }

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
            services.AddScoped<Bot.MorningJob.Console.Integrations.Aws.IAwsService, Bot.MorningJob.Console.Integrations.Aws.AwsService>();
            services.AddScoped<Bot.MorningJob.Console.Integrations.Aws.IS3BucketMonitorService, Bot.MorningJob.Console.Integrations.Aws.S3BucketMonitorService>();
            services.AddScoped<ITeamsNotifier, TeamsNotifier>();
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
        public TeamsSettings? Teams { get; set; }
    }

    public class TeamsSettings
    {
        /// <summary>
        /// Power Automate workflow webhook URL. It embeds a signature (sig=...), so treat it as a
        /// secret: keep it only in the git-ignored appsettings.json, user secrets, or the
        /// TEAMS_FLOW_WEBHOOK_URL environment variable - never in a tracked file.
        /// </summary>
        public string? WebhookUrl { get; set; }
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
        public string? SsoProfileName { get; set; } = "default";
        public string? RoleArn { get; set; }
        public string? RoleSessionName { get; set; } = "bot-morningjob-session";
        public string? PayoutQueueName { get; set; } = "payout-transactions-queue";
        public string? PayoutDlqName { get; set; } = "payout-transactions-queue-dlq";
        public S3Settings? S3 { get; set; }
        public SqsSettings? Sqs { get; set; }

        /// <summary>
        /// Extra SQS queues to report on in the DLQ section, e.g. queues that live in another AWS
        /// account and are reached via their own role.
        /// </summary>
        public List<SqsQueueConfig>? AdditionalQueues { get; set; }
        public List<S3BucketConfig>? MonitoredBuckets { get; set; }
    }

    /// <summary>
    /// Configuration for an S3 bucket that lives in a separate AWS account, accessed via a
    /// dedicated cross-account role assumed through AWS SSO.
    /// </summary>
    public class S3BucketConfig
    {
        public string? BucketName { get; set; }
        public string? RoleArn { get; set; }
        public string? AccountId { get; set; }
        public string? Region { get; set; }

        /// <summary>
        /// "DateNested" (default): failed/&lt;yyyy&gt;/&lt;MM&gt;/&lt;dd&gt;/ and
        /// processed/&lt;yyyy&gt;/&lt;MM&gt;/&lt;dd&gt;/ sub-folder structure (e.g.
        /// stripe-payout-workday-adapter-bucket).
        /// "Flat": a single failed/, new/, and processed/ folder with no date sub-folders;
        /// objects are grouped by their S3 LastModified date instead (e.g.
        /// phoenix-prd-prism-integration).
        /// </summary>
        public string ScanMode { get; set; } = "DateNested";
    }

    public class S3Settings
    {
        public string? BucketName { get; set; }
        public List<string>? MonitoredPrefixes { get; set; }
    }

    /// <summary>
    /// An additional SQS queue to monitor, accessed through its own (possibly cross-account) role.
    /// </summary>
    public class SqsQueueConfig
    {
        public string? QueueName { get; set; }
        public string? RoleArn { get; set; }
        public string? Region { get; set; }
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

        /// <summary>
        /// Full path to the Notepad++ executable used to open the generated morning report
        /// file. Defaults to "C:\Program Files\Notepad++\notepad++.exe" if not configured. If
        /// the executable isn't found at the configured (or default) path, the report simply
        /// won't be auto-opened and the program continues/exits gracefully.
        /// </summary>
        public string? NotepadPlusPlusPath { get; set; }
    }
}
