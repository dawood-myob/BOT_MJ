using Bot.MorningJob.Console.Integrations.SFTP;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Bot.MorningJob.Console.Integrations.Database;
using Bot.MorningJob.Console.Integrations.Workday;

namespace Bot.MorningJob.Console.Integrations.Int002
{
    /// <summary>
    /// Result of an INT002 sync run.
    /// </summary>
    public class Int002SyncResult
    {
        public int JobRunId { get; set; }
        public DateTime RunningAtUtc { get; set; }
        public List<string> NewProcessedFiles { get; set; } = new();
        public List<string> NewFailedFiles { get; set; } = new();
    }

    /// <summary>
    /// Data derived from the Workday RPT_INT059_SumoLogic_Integration_Logs report, used to
    /// populate the INT003 and Acumatica Issues (INT008/INT024) sections of the daily report.
    /// </summary>
    public class WorkdayReportData
    {
        public string? Int003Status { get; set; }
        public string? Int008Status { get; set; }
        public string? Int008ReferenceRange { get; set; }
        public string? Int024Status { get; set; }
        public string? Int024ReferenceRange { get; set; }
        public string? BankingStatus { get; set; }
        public string? XeStatus { get; set; }
    }

    /// <summary>
    /// Syncs INT002 processed/failed files from SFTP to a local folder structure
    /// (C:\Drive\MorningJob\INT002\Processed and \Failed) and records job runs
    /// and copied files in SQL Server.
    /// </summary>
    public interface IInt002SyncService
    {
        Task<Int002SyncResult> SyncAsync();
    }

    public class Int002SyncService : IInt002SyncService
    {
        private const string IntegrationFolderName = "INT002";
        private const string ProcessedFolderName = "Processed";
        private const string FailedFolderName = "Failed";
        private const string IntegrationDisplayName = "INT002 PayGlobal Worker Inbound";

        private readonly ILogger<Int002SyncService> _logger;
        private readonly ISftpService _sftpService;
        private readonly IDatabaseService _databaseService;
        private readonly IWorkdayService _workdayService;
        private readonly SftpSettings _sftpSettings;
        private readonly Int002SyncSettings _settings;

        public Int002SyncService(
            ILogger<Int002SyncService> logger,
            ISftpService sftpService,
            IDatabaseService databaseService,
            IWorkdayService workdayService,
            IOptions<AppSettings> appSettings)
        {
            _logger = logger;
            _sftpService = sftpService;
            _databaseService = databaseService;
            _workdayService = workdayService;
            _sftpSettings = appSettings.Value.Sftp ?? new SftpSettings();
            _settings = appSettings.Value.Int002Sync ?? new Int002SyncSettings();
        }

        public async Task<Int002SyncResult> SyncAsync()
        {
            var localRoot = string.IsNullOrWhiteSpace(_settings.LocalRootPath)
                ? @"C:\Drive\MorningJob"
                : _settings.LocalRootPath;

            var connectionString = _settings.SqlConnectionString
                ?? throw new InvalidOperationException("AppSettings:Int002Sync:SqlConnectionString is not configured.");

            var processedLocalDir = Path.Combine(localRoot, IntegrationFolderName, ProcessedFolderName);
            var failedLocalDir = Path.Combine(localRoot, IntegrationFolderName, FailedFolderName);
            Directory.CreateDirectory(processedLocalDir);
            Directory.CreateDirectory(failedLocalDir);

            await EnsureDatabaseAsync(connectionString);

            var runningAtUtc = DateTime.UtcNow;
            var jobRunId = await InsertJobRunAsync(connectionString, runningAtUtc);

            var result = new Int002SyncResult
            {
                JobRunId = jobRunId,
                RunningAtUtc = runningAtUtc
            };

            var processedDirectory = _sftpSettings.ProcessedDirectory ?? "/INT002/PROD/processed";
            var failedDirectory = _sftpSettings.FailedDirectory ?? "/INT002/PROD/failed";

            await _sftpService.ConnectAsync();

            try
            {
                var existingProcessed = await GetExistingFileNamesAsync(connectionString, "Int002ProcessedFiles");
                var existingFailed = await GetExistingFileNamesAsync(connectionString, "Int002FailedFiles");

                var remoteProcessedFiles = await _sftpService.GetFilesAsync(processedDirectory);
                var remoteFailedFiles = await _sftpService.GetFilesAsync(failedDirectory);

                foreach (var file in remoteProcessedFiles)
                {
                    if (string.IsNullOrWhiteSpace(file.Name) || string.IsNullOrWhiteSpace(file.FullPath))
                    {
                        continue;
                    }

                    if (existingProcessed.Contains(file.Name))
                    {
                        continue;
                    }

                    var localPath = Path.Combine(processedLocalDir, file.Name);
                    await _sftpService.DownloadFileAsync(file.FullPath, localPath);

                    var copiedAtUtc = DateTime.UtcNow;
                    await InsertFileRecordAsync(connectionString, "Int002ProcessedFiles", jobRunId, file.Name, copiedAtUtc);

                    result.NewProcessedFiles.Add(file.Name);
                    _logger.LogInformation("Copied new processed file {FileName} to {LocalPath}", file.Name, localPath);
                }

                var failedFileErrorCounts = new List<(string NormalizedFileName, int ErrorCount, List<int> ErrorRowNumbers, DateOnly? FileDate)>();
                string? workUploadDir = null;

                foreach (var file in remoteFailedFiles)
                {
                    if (string.IsNullOrWhiteSpace(file.Name) || string.IsNullOrWhiteSpace(file.FullPath))
                    {
                        continue;
                    }

                    if (existingFailed.Contains(file.Name))
                    {
                        continue;
                    }

                    var localPath = Path.Combine(failedLocalDir, file.Name);
                    await _sftpService.DownloadFileAsync(file.FullPath, localPath);

                    var copiedAtUtc = DateTime.UtcNow;
                    await InsertFileRecordAsync(connectionString, "Int002FailedFiles", jobRunId, file.Name, copiedAtUtc);

                    result.NewFailedFiles.Add(file.Name);
                    _logger.LogInformation("Copied new failed file {FileName} to {LocalPath}", file.Name, localPath);

                    var errorRowNumbers = GetDataRowNumbers(localPath);
                    var normalizedFileName = NormalizeFileName(file.Name);
                    var fileDate = ExtractFileDate(file.Name);
                    failedFileErrorCounts.Add((normalizedFileName, errorRowNumbers.Count, errorRowNumbers, fileDate));

                    workUploadDir ??= GetTodaysWorkUploadFolder();
                    var workUploadPath = Path.Combine(workUploadDir, file.Name);
                    File.Copy(localPath, workUploadPath, overwrite: true);
                    _logger.LogInformation("Copied failed file {FileName} to manual upload folder {WorkUploadPath}", file.Name, workUploadPath);
                }

                if (failedFileErrorCounts.Count > 0)
                {
                    await CreateReportAsync(connectionString, jobRunId, failedFileErrorCounts);
                }

                workUploadDir ??= GetTodaysWorkUploadFolder();
                var stripeLatestCompleted = await _databaseService.GetStripeLatestCompletedAsync();
                var workdayReportData = await TryFetchWorkdayReportDataAsync(connectionString, runningAtUtc);
                var reportFilePath = GenerateDailyReportTextFile(workUploadDir, runningAtUtc, failedFileErrorCounts, stripeLatestCompleted, workdayReportData);
                _logger.LogInformation("Generated daily report file at {ReportFilePath}", reportFilePath);

                OpenFolderInExplorer(workUploadDir);
            }
            finally
            {
                _sftpService.Disconnect();
            }

            return result;
        }

        /// <summary>
        /// Mirrors the existing manual batch script: builds (and creates if missing) a
        /// C:\work\yyyy\mm\dd folder for today's date, where new failed files are copied
        /// for manual upload to SharePoint.
        /// </summary>
        private string GetTodaysWorkUploadFolder()
        {
            var workRoot = string.IsNullOrWhiteSpace(_settings.WorkUploadRootPath)
                ? @"C:\work"
                : _settings.WorkUploadRootPath;

            var today = DateTime.Now;
            var folder = Path.Combine(workRoot, today.ToString("yyyy"), today.ToString("MM"), today.ToString("dd"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private void OpenFolderInExplorer(string folderPath)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = folderPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to open work upload folder {FolderPath} in Explorer.", folderPath);
            }
        }

        /// <summary>
        /// Strips the trailing "_PayGlobal_yyyy-MM-dd_HHmmss" date/time component and extension from
        /// a file name, then removes separators to produce a compact item name.
        /// e.g. "SupervisoryOrgData_PayGlobal_2026-06-15_210026.csv" -> "SupervisoryOrgData"
        ///      "Non-Manager_WorkerData_PayGlobal_2026-06-15_210036.csv" -> "NonManagerWorkerData"
        /// </summary>
        private static string NormalizeFileName(string fileName)
        {
            var withoutExtension = Path.GetFileNameWithoutExtension(fileName);
            var withoutSuffix = Regex.Replace(withoutExtension, @"_PayGlobal_\d{4}-\d{2}-\d{2}_\d{6}$", string.Empty);
            return withoutSuffix.Replace("-", string.Empty).Replace("_", string.Empty);
        }

        /// <summary>
        /// Extracts the "_PayGlobal_yyyy-MM-dd_" date component from a file name, if present.
        /// Used to indicate which date's file is being reported on Monday runs, when the
        /// SFTP failed folder may contain a backlog of Saturday/Sunday files alongside Monday's.
        /// </summary>
        private static DateOnly? ExtractFileDate(string fileName)
        {
            var match = Regex.Match(fileName, @"_PayGlobal_(\d{4})-(\d{2})-(\d{2})_\d{6}");
            if (!match.Success)
            {
                return null;
            }

            var year = int.Parse(match.Groups[1].Value);
            var month = int.Parse(match.Groups[2].Value);
            var day = int.Parse(match.Groups[3].Value);
            return new DateOnly(year, month, day);
        }

        /// <summary>
        /// Returns the 1-based data row numbers (excluding the header row) for every row in a CSV file.
        /// Currently every data row is treated as an "error row" since these files come from the
        /// SFTP "failed" directory. Row numbers are used for the report line, e.g. "(28,29,30)".
        /// </summary>
        private static List<int> GetDataRowNumbers(string filePath)
        {
            var rowNumbers = new List<int>();
            using var reader = new StreamReader(filePath);

            var lineNumber = 0;
            var dataRowNumber = 0;
            while (reader.ReadLine() != null)
            {
                lineNumber++;
                if (lineNumber == 1)
                {
                    // Skip header row.
                    continue;
                }

                dataRowNumber++;
                rowNumbers.Add(dataRowNumber);
            }

            return rowNumbers;
        }

        /// <summary>
        /// Fetches Workday integration logs (INT003, INT008, INT024) for the last 24 hours,
        /// persists them to SQL Server via IWorkdayService, and derives the report data used
        /// for the INT003 and Acumatica Issues sections. Returns null if the fetch fails
        /// (e.g. OAuth credentials invalid), so the report can gracefully fall back to a
        /// static placeholder instead of failing the whole run.
        /// </summary>
        private async Task<WorkdayReportData?> TryFetchWorkdayReportDataAsync(string connectionString, DateTimeOffset runningAtUtc)
        {
            try
            {
                var toMoment = runningAtUtc;
                var fromMoment = toMoment.AddHours(-24);

                var entries = await _workdayService.FetchAndStoreIntegrationLogsAsync(connectionString, fromMoment, toMoment);

                return new WorkdayReportData
                {
                    Int003Status = BuildStatusText(entries, "INT003_PayGlobal_Payroll_Journals_Inbound"),
                    Int008Status = BuildStatusText(entries, "INT008_Acumatica_Billing_Journals_Inbound"),
                    Int008ReferenceRange = BuildReferenceRange(entries, "INT008_Acumatica_Billing_Journals_Inbound"),
                    Int024Status = BuildStatusText(entries, "INT024_Acumatica_to_Workday_AHBT_Inbound"),
                    Int024ReferenceRange = BuildReferenceRange(entries, "INT024_Acumatica_to_Workday_AHBT_Inbound"),
                    BankingStatus = BuildBankingStatus(entries),
                    XeStatus = BuildXeStatus(entries)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch Workday integration logs for INT003/INT008/INT024 report sections.");
                return null;
            }
        }

        /// <summary>
        /// Picks the most recent log entry for the given integration and builds a human-friendly
        /// status line, e.g. "COMPLETED  OK", "Completed with warning (no inbound file)".
        /// </summary>
        private static string? BuildStatusText(List<Workday.WorkdayIntegrationLogEntry> entries, string integrationName)
        {
            var latest = entries
                .Where(e => string.Equals(e.IntegrationName, integrationName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.TimeStamp)
                .FirstOrDefault();

            if (latest == null)
            {
                return null;
            }

            var status = latest.Status ?? string.Empty;
            var messages = latest.Messages ?? string.Empty;

            if (status.Contains("Completed with Warning", StringComparison.OrdinalIgnoreCase))
            {
                if (messages.Contains("No inbound document found", StringComparison.OrdinalIgnoreCase) ||
                    messages.Contains("No retrieved documents found", StringComparison.OrdinalIgnoreCase))
                {
                    return "Completed with warning (no inbound file)";
                }

                return "Completed with warning";
            }

            if (status.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Failed", StringComparison.OrdinalIgnoreCase))
            {
                return "COMPLETED  ERROR";
            }

            if (status.Contains("Completed", StringComparison.OrdinalIgnoreCase))
            {
                return "COMPLETED  OK";
            }

            return status;
        }

        /// <summary>
        /// Extracts the Acumatica batch reference numbers (e.g. "Acumatica-1087-AU-AD-01") embedded
        /// in the latest log entry's messages for the given integration, and formats them as a
        /// range, e.g. "1087 - 1088". Returns null if no references are found.
        /// </summary>
        private static string? BuildReferenceRange(List<Workday.WorkdayIntegrationLogEntry> entries, string integrationName)
        {
            var latest = entries
                .Where(e => string.Equals(e.IntegrationName, integrationName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.TimeStamp)
                .FirstOrDefault();

            if (latest?.Messages == null)
            {
                return null;
            }

            var matches = Regex.Matches(latest.Messages, @"Acumatica-(\d+)-");
            var referenceNumbers = matches
                .Select(m => int.Parse(m.Groups[1].Value))
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            if (referenceNumbers.Count == 0)
            {
                return null;
            }

            return referenceNumbers.Count == 1
                ? referenceNumbers[0].ToString()
                : $"{referenceNumbers.First()} - {referenceNumbers.Last()}";
        }

        /// <summary>
        /// Searches all Workday log entries whose process/integration name matches any of the
        /// bank keywords (anz, cba, westpac, visa, common wealth) and determines the banking
        /// status line. If every matching entry is "Completed with Warnings" (or better), the
        /// usual "no retrieved documents found" line is returned. If any matching entry shows
        /// "Completed With Errors" or a failed status, that status is surfaced with an
        /// "**** Action required" marker.
        /// </summary>
        private static readonly string[] BankKeywords = { "anz", "cba", "westpac", "visa", "common wealth", "commonwealth" };

        private static string BuildBankingStatus(List<Workday.WorkdayIntegrationLogEntry> entries)
        {
            var bankingEntries = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.IntegrationName) &&
                            BankKeywords.Any(k => e.IntegrationName!.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (bankingEntries.Count == 0)
            {
                return "Westpac/ANZ/CBA - (No retrieved documents found) as usual";
            }

            var problemEntries = bankingEntries
                .Where(e =>
                {
                    var status = e.Status ?? string.Empty;
                    return status.Contains("Completed With Errors", StringComparison.OrdinalIgnoreCase) ||
                           status.Contains("Completed with Errors", StringComparison.OrdinalIgnoreCase) ||
                           status.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
                           status.Contains("Error", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(e => e.TimeStamp)
                .ToList();

            if (problemEntries.Count == 0)
            {
                return "Westpac/ANZ/CBA - (No retrieved documents found) as usual";
            }

            var lines = problemEntries
                .GroupBy(e => e.IntegrationName)
                .Select(g => g.OrderByDescending(e => e.TimeStamp).First())
                .Select(e => $"{e.IntegrationName} - {e.Status} | recorded on {FormatOrdinalDate(e.TimeStamp.LocalDateTime)} **** Action required");

            return string.Join(Environment.NewLine + "  ", lines);
        }

        /// <summary>
        /// Searches all Workday log entries whose process/integration name matches any of the
        /// XE keywords (INT005 Studio XE Exchange Rates Inbound Preprocessor, INT005 XE Currency
        /// Exchange Rates Inbound) and determines the XE status line. If every matching entry
        /// completed without error or warning, "No issues reported." is returned. If any matching
        /// entry shows an error, warning, or failed status, that status is surfaced with an
        /// "**** Action required" marker.
        /// </summary>
        private static readonly string[] XeKeywords = { "XE Currency Exchange Rates Inbound", "Studio XE Exchange Rates Inbound Preprocessor" };

        private static string BuildXeStatus(List<Workday.WorkdayIntegrationLogEntry> entries)
        {
            var xeEntries = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.IntegrationName) &&
                            XeKeywords.Any(k => e.IntegrationName!.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (xeEntries.Count == 0)
            {
                return "No issues reported.";
            }

            var problemEntries = xeEntries
                .Where(e =>
                {
                    var status = e.Status ?? string.Empty;
                    return status.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                           status.Contains("Warning", StringComparison.OrdinalIgnoreCase) ||
                           status.Contains("Failed", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(e => e.TimeStamp)
                .ToList();

            if (problemEntries.Count == 0)
            {
                return "No issues reported.";
            }

            var lines = problemEntries
                .GroupBy(e => e.IntegrationName)
                .Select(g => g.OrderByDescending(e => e.TimeStamp).First())
                .Select(e => $"{e.IntegrationName} - {e.Status} | recorded on {FormatOrdinalDate(e.TimeStamp.LocalDateTime)} **** Action required");

            return string.Join(Environment.NewLine + "  ", lines);
        }

        /// <summary>
        /// Generates (or appends to) today's plain-text morning report file, following the
        /// team's standard "Pay Global Issues" template. Only the INT002 section is populated
        /// dynamically from the sync results; the remaining sections are included as a static
        /// scaffold to be filled in manually.
        /// </summary>
        private string GenerateDailyReportTextFile(
            string workUploadDir,
            DateTimeOffset runningAtUtc,
            List<(string NormalizedFileName, int ErrorCount, List<int> ErrorRowNumbers, DateOnly? FileDate)> failedFileErrorCounts,
            DateTime? stripeLatestCompleted,
            WorkdayReportData? workdayReportData)
        {
            var reportDate = runningAtUtc.LocalDateTime;
            var reportFileName = $"MorningJobReport_{reportDate:dd-MM-yyyy}.txt";
            var reportFilePath = Path.Combine(workUploadDir, reportFileName);
            var isMonday = reportDate.DayOfWeek == DayOfWeek.Monday;

            var sb = new StringBuilder();
            sb.AppendLine("Pay Global Issues");
            sb.AppendLine();
            sb.AppendLine("INT002");
            sb.AppendLine($"Recorded on {FormatOrdinalDate(reportDate)}");

            AppendInt002Line(sb, "SupervisoryOrgData", failedFileErrorCounts, isMonday);
            AppendInt002Line(sb, "NonManagerWorkerData", failedFileErrorCounts, isMonday);
            AppendInt002Line(sb, "ManagerWorkerData", failedFileErrorCounts, isMonday);

            sb.AppendLine();
            if (failedFileErrorCounts.Count > 0)
            {
                sb.AppendLine("Action Taken: Error file(s) uploaded to :");
                sb.AppendLine("  https://myobo365.sharepoint.com/:f:/r/sites/GSWorkdayPlatform/Integrations/PayGlobal/Error%20files%20%5Bto%20be%20checked%5D?csf=1&web=1&e=c2GefJ");
            }
            else
            {
                sb.AppendLine("Action Taken: No error files today.");
            }

            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("INT003");
            var int003StatusText = workdayReportData?.Int003Status ?? "Completed ---";
            sb.AppendLine($" \u2666 {int003StatusText} | recorded on {FormatOrdinalDate(reportDate)}");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine($"Banking Issues recorded on {FormatOrdinalDate(reportDate)}");
            var bankingStatusText = workdayReportData?.BankingStatus ?? "Westpac/ANZ/CBA - (No retrieved documents found) as usual";
            sb.AppendLine($" \u2666 {bankingStatusText}");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Acumatica Issues");
            sb.AppendLine();
            sb.AppendLine($"Recorded on {FormatOrdinalDate(reportDate)}");
            var int008StatusText = workdayReportData?.Int008Status ?? "COMPLETED  OK";
            var int008RangeText = workdayReportData?.Int008ReferenceRange != null ? $" | {workdayReportData.Int008ReferenceRange}" : string.Empty;
            sb.AppendLine($" \u2666 INT008 - Journals - {int008StatusText}{int008RangeText}");
            var int024StatusText = workdayReportData?.Int024Status ?? "COMPLETED  OK";
            var int024RangeText = workdayReportData?.Int024ReferenceRange != null ? $" | {workdayReportData.Int024ReferenceRange}" : string.Empty;
            sb.AppendLine($" \u2666 INT024 - AHBT     - {int024StatusText}{int024RangeText}");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("GTV, Prism, & Stripe Issues");
            sb.AppendLine("GTV, Prism, & Stripe Issues | Failures / Error(s)");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine($"DLQ Recorded on {FormatOrdinalDate(reportDate)}");
            sb.AppendLine(" \u2666 0 Messages on payout-transactions-queue");
            sb.AppendLine(" \u2666 0 Messages on payout-transactions-queue-dlq  ");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("S3:");
            sb.AppendLine(" \u2666 stripe-payout-workday-adapter-bucket - 0 FAILED 0 NEW  0 PROCESSED  ON - " + reportDate.ToString("dd-MM-yyyy"));
            sb.AppendLine(" \u2666 phoenix-prd-prism-integration        - 0 FAILED 0 NEW  Recorded      ON - " + reportDate.ToString("dd-MM-yyyy"));
            sb.AppendLine(" \u2666 gt-transaction-prism-bucket          - 0 FAILED 0 NEW  Recorded      ON - " + reportDate.ToString("dd-MM-yyyy"));
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Sumo Logs");
            sb.AppendLine("Execute Data Change: PG_PayrollbyWorker_TBL SFTP DCT");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine($"DB - Recorded on {FormatOrdinalDate(reportDate)}");
            var stripeLatestCompletedText = stripeLatestCompleted.HasValue
                ? stripeLatestCompleted.Value.ToString("dd-MM-yyyy HH:mm")
                : "Unavailable";
            sb.AppendLine($" \u2666 Stripe Latest completed - {stripeLatestCompletedText}");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Miscellaneous Issues");
            sb.AppendLine();
            sb.AppendLine("XE");
            var xeStatusText = workdayReportData?.XeStatus ?? "No issues reported.";
            sb.AppendLine($" \u2666 {xeStatusText}");
            sb.AppendLine();
            sb.AppendLine("Workday Community Updates:");
            sb.AppendLine(" \u2666 No updates reported.");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Items to Follow Up ");
            sb.AppendLine(" \u2666 None.");

            File.WriteAllText(reportFilePath, sb.ToString());
            return reportFilePath;
        }

        private static void AppendInt002Line(
            StringBuilder sb,
            string itemName,
            List<(string NormalizedFileName, int ErrorCount, List<int> ErrorRowNumbers, DateOnly? FileDate)> failedFileErrorCounts,
            bool isMonday)
        {
            var match = failedFileErrorCounts.FirstOrDefault(f => string.Equals(f.NormalizedFileName, itemName, StringComparison.OrdinalIgnoreCase));
            var errorCount = match.NormalizedFileName != null ? match.ErrorCount : 0;
            var rowNumbers = match.NormalizedFileName != null ? match.ErrorRowNumbers : new List<int>();

            var errorWord = errorCount == 1 ? "Error" : "Errors";

            // Monday runs may see a backlog of Saturday/Sunday files in the SFTP failed folder,
            // so the row numbers are annotated with the specific file date. Tuesday-Friday runs
            // only ever contain the previous day's file, so the row-number detail is omitted.
            var rowNumbersText = isMonday && rowNumbers.Count > 0
                ? $" ({string.Join(",", rowNumbers)}{(match.FileDate.HasValue ? $" - {match.FileDate.Value:dd-MM-yyyy}" : string.Empty)})"
                : string.Empty;

            sb.AppendLine($" \u2666{itemName,-22}- {errorCount} Data {errorWord}(s){rowNumbersText}");
        }

        private static string FormatOrdinalDate(DateTime date)
        {
            var day = date.Day;
            var suffix = (day % 10, day / 10 % 10) switch
            {
                (1, not 1) => "st",
                (2, not 1) => "nd",
                (3, not 1) => "rd",
                _ => "th"
            };

            return $"{day}{suffix} {date:MMMM}";
        }

        private static async Task CreateReportAsync(string connectionString, int jobRunId, List<(string NormalizedFileName, int ErrorCount, List<int> ErrorRowNumbers, DateOnly? FileDate)> failedFileErrorCounts)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            const string insertReportSql = @"
INSERT INTO Reports (JobRunId, ReportDate) VALUES (@JobRunId, @ReportDate);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

            int reportId;
            await using (var reportCommand = new SqlCommand(insertReportSql, connection))
            {
                reportCommand.Parameters.AddWithValue("@JobRunId", jobRunId);
                reportCommand.Parameters.AddWithValue("@ReportDate", DateTime.UtcNow);
                reportId = Convert.ToInt32(await reportCommand.ExecuteScalarAsync());
            }

            const string insertReportLineSql = @"
INSERT INTO ReportLines (ReportId, GroupName, Integration, ItemName, ErrorCount, LineText, RecordedOn)
VALUES (@ReportId, @GroupName, @Integration, @ItemName, @ErrorCount, @LineText, @RecordedOn);";

            var recordedOnUtc = DateTime.UtcNow;

            foreach (var (normalizedFileName, errorCount, errorRowNumbers, _) in failedFileErrorCounts)
            {
                var errorWord = errorCount == 1 ? "Error" : "Errors";
                var rowNumbersText = errorRowNumbers.Count > 0 ? $" ({string.Join(",", errorRowNumbers)})" : string.Empty;
                var lineText = $" \u2022{normalizedFileName,-22}- {errorCount} Data {errorWord}(s){rowNumbersText}";

                await using var lineCommand = new SqlCommand(insertReportLineSql, connection);
                lineCommand.Parameters.AddWithValue("@ReportId", reportId);
                lineCommand.Parameters.AddWithValue("@GroupName", "Pay Global Issues");
                lineCommand.Parameters.AddWithValue("@Integration", IntegrationDisplayName);
                lineCommand.Parameters.AddWithValue("@ItemName", normalizedFileName);
                lineCommand.Parameters.AddWithValue("@ErrorCount", errorCount);
                lineCommand.Parameters.AddWithValue("@LineText", lineText);
                lineCommand.Parameters.AddWithValue("@RecordedOn", recordedOnUtc);
                await lineCommand.ExecuteNonQueryAsync();
            }
        }

        private static async Task EnsureDatabaseAsync(string connectionString)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var databaseName = builder.InitialCatalog;

            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new InvalidOperationException("SQL connection string must specify an initial catalog (database name).");
            }

            builder.InitialCatalog = "master";

            await using (var masterConnection = new SqlConnection(builder.ConnectionString))
            {
                await masterConnection.OpenAsync();

                var createDbSql = $@"
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'{databaseName}')
BEGIN
    CREATE DATABASE [{databaseName}];
END";

                await using var createDbCommand = new SqlCommand(createDbSql, masterConnection);
                await createDbCommand.ExecuteNonQueryAsync();
            }

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var createTablesSql = @"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'JobRun')
BEGIN
    CREATE TABLE JobRun (
        JobRunId INT IDENTITY(1,1) PRIMARY KEY,
        RunningAt DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Int002ProcessedFiles')
BEGIN
    CREATE TABLE Int002ProcessedFiles (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        JobRunId INT NOT NULL,
        FileName NVARCHAR(500) NOT NULL,
        CopiedAt DATETIME2 NOT NULL,
        CONSTRAINT FK_Int002ProcessedFiles_JobRun FOREIGN KEY (JobRunId) REFERENCES JobRun(JobRunId)
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Int002FailedFiles')
BEGIN
    CREATE TABLE Int002FailedFiles (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        JobRunId INT NOT NULL,
        FileName NVARCHAR(500) NOT NULL,
        CopiedAt DATETIME2 NOT NULL,
        CONSTRAINT FK_Int002FailedFiles_JobRun FOREIGN KEY (JobRunId) REFERENCES JobRun(JobRunId)
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Reports')
BEGIN
    CREATE TABLE Reports (
        ReportId INT IDENTITY(1,1) PRIMARY KEY,
        JobRunId INT NOT NULL,
        ReportDate DATETIME2 NOT NULL,
        CONSTRAINT FK_Reports_JobRun FOREIGN KEY (JobRunId) REFERENCES JobRun(JobRunId)
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ReportLines')
BEGIN
    CREATE TABLE ReportLines (
        ReportLineId INT IDENTITY(1,1) PRIMARY KEY,
        ReportId INT NOT NULL,
        GroupName NVARCHAR(200) NOT NULL,
        Integration NVARCHAR(200) NOT NULL,
        ItemName NVARCHAR(500) NOT NULL,
        ErrorCount INT NULL,
        LineText NVARCHAR(1000) NOT NULL,
        RecordedOn DATETIME2 NOT NULL,
        CONSTRAINT FK_ReportLines_Reports FOREIGN KEY (ReportId) REFERENCES Reports(ReportId)
    );
END";

            await using var createTablesCommand = new SqlCommand(createTablesSql, connection);
            await createTablesCommand.ExecuteNonQueryAsync();
        }

        private static async Task<int> InsertJobRunAsync(string connectionString, DateTime runningAtUtc)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            const string sql = @"
INSERT INTO JobRun (RunningAt) VALUES (@RunningAt);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@RunningAt", runningAtUtc);

            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }

        private static async Task<HashSet<string>> GetExistingFileNamesAsync(string connectionString, string tableName)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var sql = $"SELECT DISTINCT FileName FROM {tableName};";

            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }

        private static async Task InsertFileRecordAsync(string connectionString, string tableName, int jobRunId, string fileName, DateTime copiedAtUtc)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var sql = $"INSERT INTO {tableName} (JobRunId, FileName, CopiedAt) VALUES (@JobRunId, @FileName, @CopiedAt);";

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@JobRunId", jobRunId);
            command.Parameters.AddWithValue("@FileName", fileName);
            command.Parameters.AddWithValue("@CopiedAt", copiedAtUtc);

            await command.ExecuteNonQueryAsync();
        }
    }
}
