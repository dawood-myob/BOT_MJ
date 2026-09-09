using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bot.MorningJob.Console.Integrations.Workday
{
    /// <summary>
    /// Workday API Integration Service
    /// Handles Workday tenant connectivity and integration monitoring
    /// </summary>
    public interface IWorkdayService
    {
        /// <summary>
        /// Authenticates with Workday using OAuth2
        /// </summary>
        Task<bool> AuthenticateAsync();

        /// <summary>
        /// Gets integration execution status
        /// </summary>
        Task<WorkdayIntegrationStatus> GetIntegrationStatusAsync(string integrationName);

        /// <summary>
        /// Gets last execution details for an integration
        /// </summary>
        Task<WorkdayExecutionDetails> GetLastExecutionDetailsAsync(string integrationName);

        /// <summary>
        /// Gets health status of all monitored integrations
        /// </summary>
        Task<List<WorkdayIntegrationStatus>> GetAllIntegrationStatusesAsync();

        /// <summary>
        /// Validates connection to Workday API
        /// </summary>
        Task<bool> ValidateConnectionAsync();

        /// <summary>
        /// Fetches integration log entries from the RPT_INT059_SumoLogic_Integration_Logs
        /// Workday custom report for the given time window, persists them to SQL Server
        /// (WorkdayIntegrationLogRuns / WorkdayIntegrationLogEntries), and returns the parsed
        /// entries for immediate use (e.g. building the INT003/INT008/INT024 report sections).
        /// </summary>
        Task<List<WorkdayIntegrationLogEntry>> FetchAndStoreIntegrationLogsAsync(string sqlConnectionString, DateTimeOffset fromMoment, DateTimeOffset toMoment);
    }

    /// <summary>
    /// Workday integration status
    /// </summary>
    public class WorkdayIntegrationStatus
    {
        public string? IntegrationName { get; set; }
        public string? Status { get; set; }
        public DateTime LastExecutionTime { get; set; }
        public DateTime NextExecutionTime { get; set; }
        public string? LastResult { get; set; }
        public int SuccessCount { get; set; }
        public int ErrorCount { get; set; }
    }

    /// <summary>
    /// Workday execution details
    /// </summary>
    public class WorkdayExecutionDetails
    {
        public string? IntegrationName { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string? Status { get; set; }
        public int RecordsProcessed { get; set; }
        public int RecordsFailed { get; set; }
        public string? ErrorMessage { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
    }

    /// <summary>
    /// A single row from the RPT_INT059_SumoLogic_Integration_Logs report:
    /// Process (IntegrationName), Create Date and time (TimeStamp), Event link, Status and Messages.
    /// </summary>
    public class WorkdayIntegrationLogEntry
    {
        public string? IntegrationName { get; set; }
        public DateTimeOffset TimeStamp { get; set; }
        public string? EventLink { get; set; }
        public string? Status { get; set; }
        public string? Messages { get; set; }
    }

    /// <summary>
    /// Workday Service Implementation.
    /// Authenticates via OAuth2 refresh-token grant against Workday's token endpoint, then
    /// calls the RPT_INT059_SumoLogic_Integration_Logs custom report (JSON format) for a given
    /// From/To moment window, and persists the raw log entries to SQL Server for reuse by
    /// other report sections (INT003, INT008/Acumatica Journals, INT024/AHBT, etc.).
    /// </summary>
    public class WorkdayService : IWorkdayService
    {
        private readonly ILogger<WorkdayService> _logger;
        private readonly WorkdaySettings _settings;
        private readonly HttpClient _httpClient;

        public WorkdayService(ILogger<WorkdayService> logger, IOptions<AppSettings> appSettings)
        {
            _logger = logger;
            _settings = appSettings.Value.Workday ?? new WorkdaySettings();
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
        }

        public Task<bool> AuthenticateAsync()
        {
            throw new NotImplementedException();
        }

        public Task<WorkdayIntegrationStatus> GetIntegrationStatusAsync(string integrationName)
        {
            throw new NotImplementedException();
        }

        public Task<WorkdayExecutionDetails> GetLastExecutionDetailsAsync(string integrationName)
        {
            throw new NotImplementedException();
        }

        public Task<List<WorkdayIntegrationStatus>> GetAllIntegrationStatusesAsync()
        {
            throw new NotImplementedException();
        }

        public Task<bool> ValidateConnectionAsync()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Acquires an OAuth2 access token from AppSettings:Workday:TokenUrl using the
        /// refresh_token grant type with ClientId/ClientSecret/RefreshToken.
        /// </summary>
        private async Task<string> GetAccessTokenAsync()
        {
            if (string.IsNullOrWhiteSpace(_settings.TokenUrl) ||
                string.IsNullOrWhiteSpace(_settings.ClientId) ||
                string.IsNullOrWhiteSpace(_settings.ClientSecret) ||
                string.IsNullOrWhiteSpace(_settings.RefreshToken))
            {
                throw new InvalidOperationException(
                    "AppSettings:Workday OAuth settings (TokenUrl, ClientId, ClientSecret, RefreshToken) are not fully configured.");
            }

            var requestBody = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _settings.RefreshToken,
                ["client_id"] = _settings.ClientId,
                ["client_secret"] = _settings.ClientSecret
            };

            using var content = new FormUrlEncodedContent(requestBody);
            using var response = await _httpClient.PostAsync(_settings.TokenUrl, content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Workday OAuth token request failed with status {response.StatusCode}: {body}");
            }

            using var document = JsonDocument.Parse(body);
            var accessToken = document.RootElement.GetProperty("access_token").GetString();

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException("Workday OAuth token response did not contain an access_token.");
            }

            return accessToken;
        }

        public async Task<List<WorkdayIntegrationLogEntry>> FetchAndStoreIntegrationLogsAsync(
            string sqlConnectionString, DateTimeOffset fromMoment, DateTimeOffset toMoment)
        {
            if (string.IsNullOrWhiteSpace(_settings.ReportApiUrl))
            {
                throw new InvalidOperationException("AppSettings:Workday:ReportApiUrl is not configured.");
            }

            var accessToken = await GetAccessTokenAsync();

            var fromText = fromMoment.ToString("yyyy-MM-ddTHH:mm:ss.fffK");
            var toText = toMoment.ToString("yyyy-MM-ddTHH:mm:ss.fffK");
            var requestUrl = $"{_settings.ReportApiUrl}?To_Moment={Uri.EscapeDataString(toText)}&From_Moment={Uri.EscapeDataString(fromText)}&format=json";

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Workday integration log report request failed with status {response.StatusCode}: {body}");
            }

            var entries = ParseReportEntries(body);

            _logger.LogInformation("Fetched {Count} Workday integration log entries for window {From} to {To}.", entries.Count, fromMoment, toMoment);

            await EnsureWorkdayTablesAsync(sqlConnectionString);
            await StoreLogEntriesAsync(sqlConnectionString, entries, fromMoment, toMoment);

            return entries;
        }

        private static List<WorkdayIntegrationLogEntry> ParseReportEntries(string json)
        {
            var entries = new List<WorkdayIntegrationLogEntry>();

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Report_Entry", out var reportEntryElement))
            {
                return entries;
            }

            foreach (var entry in reportEntryElement.EnumerateArray())
            {
                var integrationName = entry.TryGetProperty("IntegrationName", out var nameEl) ? nameEl.GetString() : null;
                var status = entry.TryGetProperty("Status", out var statusEl) ? statusEl.GetString() : null;
                var eventLink = entry.TryGetProperty("EventLink", out var linkEl) ? linkEl.GetString() : null;
                var messages = entry.TryGetProperty("Messages", out var messagesEl) ? messagesEl.GetString() : null;
                var timeStampText = entry.TryGetProperty("TimeStamp", out var tsEl) ? tsEl.GetString() : null;

                var timeStamp = DateTimeOffset.TryParse(timeStampText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedTs)
                    ? parsedTs
                    : DateTimeOffset.MinValue;

                entries.Add(new WorkdayIntegrationLogEntry
                {
                    IntegrationName = integrationName,
                    Status = status,
                    EventLink = eventLink,
                    Messages = messages,
                    TimeStamp = timeStamp
                });
            }

            return entries;
        }

        private static async Task EnsureWorkdayTablesAsync(string connectionString)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            const string createTablesSql = @"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkdayIntegrationLogRuns')
BEGIN
    CREATE TABLE WorkdayIntegrationLogRuns (
        RunId INT IDENTITY(1,1) PRIMARY KEY,
        FetchedAtUtc DATETIME2 NOT NULL,
        FromMoment DATETIMEOFFSET NOT NULL,
        ToMoment DATETIMEOFFSET NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WorkdayIntegrationLogEntries')
BEGIN
    CREATE TABLE WorkdayIntegrationLogEntries (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        RunId INT NOT NULL,
        IntegrationName NVARCHAR(500) NULL,
        TimeStamp DATETIMEOFFSET NULL,
        EventLink NVARCHAR(1000) NULL,
        Status NVARCHAR(200) NULL,
        Messages NVARCHAR(MAX) NULL,
        CONSTRAINT FK_WorkdayIntegrationLogEntries_Run FOREIGN KEY (RunId) REFERENCES WorkdayIntegrationLogRuns(RunId)
    );
END";

            await using var command = new SqlCommand(createTablesSql, connection);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task StoreLogEntriesAsync(
            string connectionString,
            List<WorkdayIntegrationLogEntry> entries,
            DateTimeOffset fromMoment,
            DateTimeOffset toMoment)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            const string insertRunSql = @"
INSERT INTO WorkdayIntegrationLogRuns (FetchedAtUtc, FromMoment, ToMoment) VALUES (@FetchedAtUtc, @FromMoment, @ToMoment);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

            int runId;
            await using (var runCommand = new SqlCommand(insertRunSql, connection))
            {
                runCommand.Parameters.AddWithValue("@FetchedAtUtc", DateTime.UtcNow);
                runCommand.Parameters.AddWithValue("@FromMoment", fromMoment);
                runCommand.Parameters.AddWithValue("@ToMoment", toMoment);
                runId = Convert.ToInt32(await runCommand.ExecuteScalarAsync());
            }

            const string insertEntrySql = @"
INSERT INTO WorkdayIntegrationLogEntries (RunId, IntegrationName, TimeStamp, EventLink, Status, Messages)
VALUES (@RunId, @IntegrationName, @TimeStamp, @EventLink, @Status, @Messages);";

            foreach (var entry in entries)
            {
                await using var entryCommand = new SqlCommand(insertEntrySql, connection);
                entryCommand.Parameters.AddWithValue("@RunId", runId);
                entryCommand.Parameters.AddWithValue("@IntegrationName", (object?)entry.IntegrationName ?? DBNull.Value);
                entryCommand.Parameters.AddWithValue("@TimeStamp", entry.TimeStamp == DateTimeOffset.MinValue ? DBNull.Value : entry.TimeStamp);
                entryCommand.Parameters.AddWithValue("@EventLink", (object?)entry.EventLink ?? DBNull.Value);
                entryCommand.Parameters.AddWithValue("@Status", (object?)entry.Status ?? DBNull.Value);
                entryCommand.Parameters.AddWithValue("@Messages", (object?)entry.Messages ?? DBNull.Value);
                await entryCommand.ExecuteNonQueryAsync();
            }
        }
    }
}
