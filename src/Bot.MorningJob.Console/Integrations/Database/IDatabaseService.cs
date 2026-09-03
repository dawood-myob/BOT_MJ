using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http;
using System.Text.Json;

namespace Bot.MorningJob.Console.Integrations.Database
{
    /// <summary>
    /// PostgreSQL Integration Service
    /// Handles database connectivity and query execution
    /// </summary>
    public interface IDatabaseService
    {
        /// <summary>
        /// Validates connection to PostgreSQL database
        /// </summary>
        Task<bool> ValidateConnectionAsync();

        /// <summary>
        /// Retrieves the last execution date from the integration logs table
        /// </summary>
        Task<DateTime?> GetLastExecutionDateAsync(string integrationName);

        /// <summary>
        /// Gets integration execution log records
        /// </summary>
        Task<List<IntegrationExecutionLog>> GetExecutionLogsAsync(string integrationName, int limitDays = 7);

        /// <summary>
        /// Gets the last successful execution details
        /// </summary>
        Task<IntegrationExecutionLog?> GetLastSuccessfulExecutionAsync(string integrationName);

        /// <summary>
        /// Gets all integration statuses from database
        /// </summary>
        Task<List<IntegrationStatus>> GetAllIntegrationStatusesAsync();

        /// <summary>
        /// Retrieves the latest completed Stripe payout event date from the Stripe database,
        /// equivalent to: SELECT MAX(event_creation_date) FROM stripe_payout_details;
        /// Returns null if the value could not be determined (e.g. connection not configured,
        /// or no rows present).
        /// </summary>
        Task<DateTime?> GetStripeLatestCompletedAsync();
    }

    /// <summary>
    /// Integration execution log entry
    /// </summary>
    public class IntegrationExecutionLog
    {
        public int Id { get; set; }
        public string? IntegrationName { get; set; }
        public DateTime ExecutionStartTime { get; set; }
        public DateTime ExecutionEndTime { get; set; }
        public string? Status { get; set; }
        public int RecordsProcessed { get; set; }
        public int RecordsFailed { get; set; }
        public string? ErrorMessage { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
    }

    /// <summary>
    /// Integration status summary
    /// </summary>
    public class IntegrationStatus
    {
        public string? IntegrationName { get; set; }
        public string? Status { get; set; }
        public DateTime LastExecuted { get; set; }
        public int TotalRuns { get; set; }
        public int SuccessfulRuns { get; set; }
        public int FailedRuns { get; set; }
        public double SuccessRate { get; set; }
    }

    /// <summary>
    /// PostgreSQL Service Implementation.
    /// Queries are executed via the pgweb REST API exposed locally by
    /// "myob-auth kube db-proxy -d stripe-db -n billing-platform" (the proxy forwards to a
    /// pgweb web client, not a raw Postgres TCP port, so HTTP is used instead of Npgsql).
    /// </summary>
    public class DatabaseService : IDatabaseService
    {
        private readonly ILogger<DatabaseService> _logger;
        private readonly DatabaseSettings _settings;
        private readonly HttpClient _httpClient;

        public DatabaseService(ILogger<DatabaseService> logger, IOptions<AppSettings> appSettings)
        {
            _logger = logger;
            _settings = appSettings.Value.Database ?? new DatabaseSettings();
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        public Task<bool> ValidateConnectionAsync()
        {
            throw new NotImplementedException();
        }

        public Task<DateTime?> GetLastExecutionDateAsync(string integrationName)
        {
            throw new NotImplementedException();
        }

        public Task<List<IntegrationExecutionLog>> GetExecutionLogsAsync(string integrationName, int limitDays = 7)
        {
            throw new NotImplementedException();
        }

        public Task<IntegrationExecutionLog?> GetLastSuccessfulExecutionAsync(string integrationName)
        {
            throw new NotImplementedException();
        }

        public Task<List<IntegrationStatus>> GetAllIntegrationStatusesAsync()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Queries the pgweb proxy (started via "myob-auth kube db-proxy -d stripe-db -n
        /// billing-platform", listening locally at AppSettings:Database:PgWebBaseUrl, default
        /// http://127.0.0.1:8081) for MAX(event_creation_date) FROM stripe_payout_details.
        /// Returns null if the proxy is not running/reachable or the query fails.
        /// </summary>
        public async Task<DateTime?> GetStripeLatestCompletedAsync()
        {
            var baseUrl = string.IsNullOrWhiteSpace(_settings.PgWebBaseUrl)
                ? "http://127.0.0.1:8081"
                : _settings.PgWebBaseUrl.TrimEnd('/');

            const string sql = "SELECT MAX(event_creation_date) AS stripe_latest_completed FROM stripe_payout_details";
            var requestUrl = $"{baseUrl}/api/query?query={Uri.EscapeDataString(sql)}";

            try
            {
                using var response = await _httpClient.GetAsync(requestUrl);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(json);

                var rows = document.RootElement.GetProperty("rows");
                if (rows.GetArrayLength() == 0)
                {
                    return null;
                }

                var firstRow = rows[0];
                if (firstRow.GetArrayLength() == 0 || firstRow[0].ValueKind == JsonValueKind.Null)
                {
                    return null;
                }

                var rawValue = firstRow[0].GetString();
                if (string.IsNullOrWhiteSpace(rawValue))
                {
                    return null;
                }

                return DateTime.Parse(rawValue, null, System.Globalization.DateTimeStyles.RoundtripKind);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to retrieve Stripe latest completed date from stripe_payout_details via pgweb proxy at {BaseUrl}. " +
                    "Ensure 'myob-auth kube db-proxy -d stripe-db -n billing-platform' is running.",
                    baseUrl);
                return null;
            }
        }
    }
}
