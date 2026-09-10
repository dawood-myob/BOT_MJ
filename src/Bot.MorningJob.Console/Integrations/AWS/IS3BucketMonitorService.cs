using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Bot.MorningJob.Console.Integrations.Aws
{
    /// <summary>
    /// One report line's worth of data for a monitored S3 bucket:
    /// e.g. " stripe-payout-workday-adapter-bucket - 0 FAILED 0 NEW  0 PROCESSED  ON - 10-09-2026".
    /// </summary>
    public class S3BucketReportLine
    {
        public string? BucketName { get; set; }
        public int FailedCount { get; set; }
        public int NewCount { get; set; }
        public int ProcessedCount { get; set; }
        public DateOnly ProcessedDate { get; set; }
        public bool ActionRequired => FailedCount > 0 || NewCount > 0;

        /// <summary>
        /// True when this date was already reported in a previous run and nothing new was found
        /// (i.e. this line is only present so the bucket isn't silently dropped from the report).
        /// The report text appends "** no change" for these lines instead of persisting them again.
        /// </summary>
        public bool IsAlreadyReported { get; set; }
    }

    /// <summary>
    /// Scans a monitored S3 bucket following the manual review process. Supports two layouts,
    /// selected via S3BucketConfig.ScanMode:
    ///
    /// "DateNested" (e.g. stripe-payout-workday-adapter-bucket):
    ///  1. failed/&lt;yyyy&gt;/&lt;MM&gt;/&lt;dd&gt;/ - any objects appearing here since the last scan are
    ///     new failures ("FAILED" count) and require action.
    ///  2. Any folder named "new" (case-insensitive), anywhere under the bucket, that currently
    ///     contains objects is flagged as the "NEW" count and requires action.
    ///  3. processed/&lt;yyyy&gt;/&lt;MM&gt;/&lt;dd&gt;/ - for the most recent unreported date(s)
    ///     (usually yesterday, but on Monday this can include Saturday/Sunday if not yet reported),
    ///     the object count becomes the "PROCESSED" count.
    ///
    /// "Flat" (e.g. phoenix-prd-prism-integration): a single failed/, new/, and processed/ folder
    /// with no date sub-folders. Objects are grouped by their S3 LastModified date instead, and one
    /// report line is produced per unreported date (supports catching up on a multi-day backlog if
    /// the job hasn't run for a few days).
    ///
    /// Scan state (known failed/new item keys, and which dates have already been reported) is
    /// tracked in SQL Server so re-running the job doesn't double-count or re-report.
    /// </summary>
    public interface IS3BucketMonitorService
    {
        Task<List<S3BucketReportLine>> ScanBucketAsync(S3BucketConfig bucketConfig, DateTime runningAtLocal, string sqlConnectionString);
    }

    public class S3BucketMonitorService : IS3BucketMonitorService
    {
        private readonly ILogger<S3BucketMonitorService> _logger;
        private readonly IAwsService _awsService;

        public S3BucketMonitorService(ILogger<S3BucketMonitorService> logger, IAwsService awsService)
        {
            _logger = logger;
            _awsService = awsService;
        }

        public async Task<List<S3BucketReportLine>> ScanBucketAsync(S3BucketConfig bucketConfig, DateTime runningAtLocal, string sqlConnectionString)
        {
            var bucketName = bucketConfig.BucketName
                ?? throw new InvalidOperationException("S3BucketConfig.BucketName is not configured.");

            await EnsureTablesAsync(sqlConnectionString);

            if (string.Equals(bucketConfig.ScanMode, "Flat", StringComparison.OrdinalIgnoreCase))
            {
                return await ScanFlatBucketAsync(bucketConfig, runningAtLocal, sqlConnectionString);
            }

            return await ScanDateNestedBucketAsync(bucketConfig, runningAtLocal, sqlConnectionString);
        }

        private async Task<List<S3BucketReportLine>> ScanDateNestedBucketAsync(S3BucketConfig bucketConfig, DateTime runningAtLocal, string sqlConnectionString)
        {
            var bucketName = bucketConfig.BucketName!;
            var roleArn = bucketConfig.RoleArn
                ?? throw new InvalidOperationException($"S3BucketConfig.RoleArn is not configured for bucket {bucketName}.");
            var region = bucketConfig.Region;

            // Step 1: failed/ - detect new failure objects since the last scan.
            var failedCount = await DetectNewFailedItemsAsync(bucketName, roleArn, region, sqlConnectionString);

            // Step 2: any "new" (case-insensitive) folder anywhere in the bucket that currently
            // has objects in it.
            var newCount = await CountNewFolderItemsAsync(bucketName, roleArn, region);

            // Step 3: processed/yyyy/MM/dd/ - report every unreported date up to yesterday
            // (captures weekend backlog reported on Monday).
            var processedDates = await GetUnreportedProcessedDatesAsync(bucketName, runningAtLocal, sqlConnectionString);

            var reportLines = new List<S3BucketReportLine>();

            foreach (var date in processedDates)
            {
                var prefix = $"processed/{date:yyyy/MM/dd}/";
                var objects = await _awsService.ListObjectsRecursiveAsync(bucketName, roleArn, prefix, region);
                var processedCount = objects.Count;

                await RecordProcessedDateAsync(bucketName, date, processedCount, sqlConnectionString);

                reportLines.Add(new S3BucketReportLine
                {
                    BucketName = bucketName,
                    FailedCount = failedCount,
                    NewCount = newCount,
                    ProcessedCount = processedCount,
                    ProcessedDate = date
                });

                // Only the first (most recent) line carries the FAILED/NEW counts; subsequent
                // backlog lines (e.g. Monday's Saturday/Sunday catch-up) show 0 to avoid double
                // counting the same bucket-wide FAILED/NEW check.
                failedCount = 0;
                newCount = 0;
            }

            if (reportLines.Count == 0)
            {
                // No unreported processed dates (e.g. already reported today) - still surface a
                // single line so FAILED/NEW issues aren't silently dropped from the report.
                reportLines.Add(new S3BucketReportLine
                {
                    BucketName = bucketName,
                    FailedCount = failedCount,
                    NewCount = newCount,
                    ProcessedCount = 0,
                    ProcessedDate = DateOnly.FromDateTime(runningAtLocal.AddDays(-1)),
                    IsAlreadyReported = failedCount == 0 && newCount == 0
                });
            }

            return reportLines;
        }

        /// <summary>
        /// Lists all objects under failed/ and compares against previously seen keys stored in
        /// S3BucketFailedItems. Any keys not previously seen are new failures; they are inserted
        /// into the tracking table so they aren't re-counted on subsequent runs.
        /// </summary>
        private async Task<int> DetectNewFailedItemsAsync(string bucketName, string roleArn, string? region, string connectionString)
        {
            var objects = await ListRealFilesAsync(bucketName, roleArn, "failed/", region);

            var knownKeys = await GetKnownFailedKeysAsync(bucketName, connectionString);

            var newItems = objects.Where(o => !knownKeys.Contains(o.Key)).ToList();

            if (newItems.Count > 0)
            {
                await InsertKnownFailedKeysAsync(bucketName, newItems.Select(o => o.Key), connectionString);
                _logger.LogWarning("Bucket {BucketName}: {Count} new failed file(s) detected under failed/.", bucketName, newItems.Count);
            }

            return newItems.Count;
        }

        /// <summary>
        /// Searches immediate children of the bucket root, failed/, and processed/&lt;year&gt;/
        /// for a folder literally named "new" (case-insensitive), and if found, counts how many
        /// objects currently exist under it.
        /// </summary>
        private async Task<int> CountNewFolderItemsAsync(string bucketName, string roleArn, string? region)
        {
            var candidatePrefixesToCheck = new List<string> { string.Empty, "failed/", "processed/" };
            var totalCount = 0;

            foreach (var basePrefix in candidatePrefixesToCheck)
            {
                var childPrefixes = await _awsService.ListCommonPrefixesAsync(bucketName, roleArn, basePrefix, region);

                foreach (var childPrefix in childPrefixes)
                {
                    var folderName = childPrefix.TrimEnd('/').Split('/').Last();
                    if (string.Equals(folderName, "new", StringComparison.OrdinalIgnoreCase))
                    {
                        var objects = await ListRealFilesAsync(bucketName, roleArn, childPrefix, region);
                        totalCount += objects.Count;
                    }
                }
            }

            if (totalCount > 0)
            {
                _logger.LogWarning("Bucket {BucketName}: {Count} item(s) found in a 'New' folder.", bucketName, totalCount);
            }

            return totalCount;
        }

        /// <summary>
        /// Determines which processed dates still need to be reported: every calendar date from
        /// the day after the last reported date (or yesterday, if nothing has ever been reported)
        /// up to and including yesterday relative to runningAtLocal. This naturally captures the
        /// Saturday/Sunday backlog on a Monday run.
        /// </summary>
        private async Task<List<DateOnly>> GetUnreportedProcessedDatesAsync(string bucketName, DateTime runningAtLocal, string connectionString)
        {
            var yesterday = DateOnly.FromDateTime(runningAtLocal.Date.AddDays(-1));

            var lastReportedDate = await GetLastReportedProcessedDateAsync(bucketName, connectionString);

            DateOnly startDate;
            if (lastReportedDate.HasValue)
            {
                startDate = lastReportedDate.Value.AddDays(1);
            }
            else
            {
                // First-ever scan for this bucket: only report yesterday, don't backfill history.
                startDate = yesterday;
            }

            if (startDate > yesterday)
            {
                return new List<DateOnly>();
            }

            var dates = new List<DateOnly>();
            for (var date = startDate; date <= yesterday; date = date.AddDays(1))
            {
                dates.Add(date);
            }

            return dates;
        }

        /// <summary>
        /// Resolves the actual case of a top-level folder name in the bucket (e.g. some buckets
        /// use "Failed/"/"New/"/"Processed/" instead of lowercase), by listing the bucket's
        /// top-level common prefixes and matching case-insensitively. Falls back to the
        /// lowercase name + "/" if no match is found (the subsequent listing will simply return
        /// zero objects).
        /// </summary>
        private async Task<string> ResolveFolderPrefixAsync(string bucketName, string roleArn, string folderName, string? region)
        {
            var topLevelPrefixes = await _awsService.ListCommonPrefixesAsync(bucketName, roleArn, string.Empty, region);

            var match = topLevelPrefixes.FirstOrDefault(p =>
                string.Equals(p.TrimEnd('/'), folderName, StringComparison.OrdinalIgnoreCase));

            return match ?? $"{folderName}/";
        }

        /// <summary>
        /// Lists objects under the given prefix, excluding zero-byte "folder marker" objects
        /// (i.e. the folder's own placeholder object, whose key equals the prefix itself).
        /// </summary>
        private async Task<List<Amazon.S3.Model.S3Object>> ListRealFilesAsync(string bucketName, string roleArn, string prefix, string? region)
        {
            var objects = await _awsService.ListObjectsRecursiveAsync(bucketName, roleArn, prefix, region);
            return objects.Where(o => !string.Equals(o.Key, prefix, StringComparison.OrdinalIgnoreCase) && o.Size > 0).ToList();
        }

        /// <summary>
        /// Flat-layout scan (e.g. phoenix-prd-prism-integration): failed/, new/, and processed/
        /// each contain files directly (no date sub-folders). Objects are grouped by their S3
        /// LastModified date. One report line is produced per unreported date, from the day
        /// after the last-reported date through yesterday (supports multi-day catch-up if the
        /// job hasn't run in a few days). FAILED/NEW counts reflect items present in those
        /// folders on that date; a line's ActionRequired flag surfaces **** Action required
        /// whenever FailedCount or NewCount &gt; 0.
        /// </summary>
        private async Task<List<S3BucketReportLine>> ScanFlatBucketAsync(S3BucketConfig bucketConfig, DateTime runningAtLocal, string sqlConnectionString)
        {
            var bucketName = bucketConfig.BucketName!;
            var roleArn = bucketConfig.RoleArn
                ?? throw new InvalidOperationException($"S3BucketConfig.RoleArn is not configured for bucket {bucketName}.");
            var region = bucketConfig.Region;

            var failedPrefix = await ResolveFolderPrefixAsync(bucketName, roleArn, "failed", region);
            var newPrefix = await ResolveFolderPrefixAsync(bucketName, roleArn, "new", region);
            var processedPrefix = await ResolveFolderPrefixAsync(bucketName, roleArn, "processed", region);

            var failedObjects = await ListRealFilesAsync(bucketName, roleArn, failedPrefix, region);
            var newObjects = await ListRealFilesAsync(bucketName, roleArn, newPrefix, region);
            var processedObjects = await ListRealFilesAsync(bucketName, roleArn, processedPrefix, region);

            var failedByDate = failedObjects.GroupBy(o => DateOnly.FromDateTime(o.LastModified.Date)).ToDictionary(g => g.Key, g => g.Count());
            var newByDate = newObjects.GroupBy(o => DateOnly.FromDateTime(o.LastModified.Date)).ToDictionary(g => g.Key, g => g.Count());
            var processedByDate = processedObjects.GroupBy(o => DateOnly.FromDateTime(o.LastModified.Date)).ToDictionary(g => g.Key, g => g.Count());

            var yesterday = DateOnly.FromDateTime(runningAtLocal.Date.AddDays(-1));
            var lastReportedDate = await GetLastReportedFlatDateAsync(bucketName, sqlConnectionString);

            var startDate = lastReportedDate.HasValue ? lastReportedDate.Value.AddDays(1) : yesterday;

            var reportLines = new List<S3BucketReportLine>();

            if (startDate > yesterday)
            {
                // Yesterday (and any earlier backlog) has already been reported for this bucket.
                // Still surface a single line for yesterday so the bucket is never silently
                // dropped from the S3 report section; FAILED/NEW reflect current live counts
                // (in case new items appeared today under yesterday's date), but this line is
                // not re-persisted since it was already recorded.
                reportLines.Add(new S3BucketReportLine
                {
                    BucketName = bucketName,
                    FailedCount = failedByDate.GetValueOrDefault(yesterday, 0),
                    NewCount = newByDate.GetValueOrDefault(yesterday, 0),
                    ProcessedCount = processedByDate.GetValueOrDefault(yesterday, 0),
                    ProcessedDate = yesterday,
                    IsAlreadyReported = true
                });

                return reportLines;
            }

            for (var date = startDate; date <= yesterday; date = date.AddDays(1))
            {
                var failedCount = failedByDate.GetValueOrDefault(date, 0);
                var newCount = newByDate.GetValueOrDefault(date, 0);
                var processedCount = processedByDate.GetValueOrDefault(date, 0);

                await RecordFlatReportDateAsync(bucketName, date, failedCount, newCount, processedCount, sqlConnectionString);

                if (failedCount > 0 || newCount > 0)
                {
                    _logger.LogWarning(
                        "Bucket {BucketName}: {FailedCount} failed and {NewCount} new item(s) found for {Date:dd-MM-yyyy}.",
                        bucketName, failedCount, newCount, date);
                }

                reportLines.Add(new S3BucketReportLine
                {
                    BucketName = bucketName,
                    FailedCount = failedCount,
                    NewCount = newCount,
                    ProcessedCount = processedCount,
                    ProcessedDate = date
                });
            }

            return reportLines;
        }

        private static async Task<DateOnly?> GetLastReportedFlatDateAsync(string bucketName, string connectionString)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            const string sql = "SELECT MAX(ReportDate) FROM S3BucketFlatReportDates WHERE BucketName = @BucketName";
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@BucketName", bucketName);

            var result = await command.ExecuteScalarAsync();
            if (result == null || result == DBNull.Value)
            {
                return null;
            }

            return DateOnly.FromDateTime((DateTime)result);
        }

        private static async Task RecordFlatReportDateAsync(
            string bucketName, DateOnly reportDate, int failedCount, int newCount, int processedCount, string connectionString)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            const string sql = @"
IF NOT EXISTS (SELECT 1 FROM S3BucketFlatReportDates WHERE BucketName = @BucketName AND ReportDate = @ReportDate)
INSERT INTO S3BucketFlatReportDates (BucketName, ReportDate, FailedCount, NewCount, ProcessedCount, RecordedAtUtc)
VALUES (@BucketName, @ReportDate, @FailedCount, @NewCount, @ProcessedCount, @RecordedAtUtc);";

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@BucketName", bucketName);
            command.Parameters.AddWithValue("@ReportDate", reportDate.ToDateTime(TimeOnly.MinValue));
            command.Parameters.AddWithValue("@FailedCount", failedCount);
            command.Parameters.AddWithValue("@NewCount", newCount);
            command.Parameters.AddWithValue("@ProcessedCount", processedCount);
            command.Parameters.AddWithValue("@RecordedAtUtc", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task EnsureTablesAsync(string connectionString)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            const string createTablesSql = @"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'S3BucketFailedItems')
BEGIN
    CREATE TABLE S3BucketFailedItems (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        BucketName NVARCHAR(255) NOT NULL,
        ObjectKey NVARCHAR(1000) NOT NULL,
        DetectedAtUtc DATETIME2 NOT NULL,
        CONSTRAINT UQ_S3BucketFailedItems UNIQUE (BucketName, ObjectKey)
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'S3BucketProcessedDates')
BEGIN
    CREATE TABLE S3BucketProcessedDates (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        BucketName NVARCHAR(255) NOT NULL,
        ProcessedDate DATE NOT NULL,
        ProcessedCount INT NOT NULL,
        RecordedAtUtc DATETIME2 NOT NULL,
        CONSTRAINT UQ_S3BucketProcessedDates UNIQUE (BucketName, ProcessedDate)
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'S3BucketFlatReportDates')
BEGIN
    CREATE TABLE S3BucketFlatReportDates (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        BucketName NVARCHAR(255) NOT NULL,
        ReportDate DATE NOT NULL,
        FailedCount INT NOT NULL,
        NewCount INT NOT NULL,
        ProcessedCount INT NOT NULL,
        RecordedAtUtc DATETIME2 NOT NULL,
        CONSTRAINT UQ_S3BucketFlatReportDates UNIQUE (BucketName, ReportDate)
    );
END";

            await using var command = new SqlCommand(createTablesSql, connection);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task<HashSet<string>> GetKnownFailedKeysAsync(string bucketName, string connectionString)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            const string sql = "SELECT ObjectKey FROM S3BucketFailedItems WHERE BucketName = @BucketName";
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@BucketName", bucketName);

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                keys.Add(reader.GetString(0));
            }

            return keys;
        }

        private static async Task InsertKnownFailedKeysAsync(string bucketName, IEnumerable<string> objectKeys, string connectionString)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            const string sql = @"
IF NOT EXISTS (SELECT 1 FROM S3BucketFailedItems WHERE BucketName = @BucketName AND ObjectKey = @ObjectKey)
INSERT INTO S3BucketFailedItems (BucketName, ObjectKey, DetectedAtUtc) VALUES (@BucketName, @ObjectKey, @DetectedAtUtc);";

            foreach (var objectKey in objectKeys)
            {
                await using var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@BucketName", bucketName);
                command.Parameters.AddWithValue("@ObjectKey", objectKey);
                command.Parameters.AddWithValue("@DetectedAtUtc", DateTime.UtcNow);
                await command.ExecuteNonQueryAsync();
            }
        }

        private static async Task<DateOnly?> GetLastReportedProcessedDateAsync(string bucketName, string connectionString)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            const string sql = "SELECT MAX(ProcessedDate) FROM S3BucketProcessedDates WHERE BucketName = @BucketName";
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@BucketName", bucketName);

            var result = await command.ExecuteScalarAsync();
            if (result == null || result == DBNull.Value)
            {
                return null;
            }

            return DateOnly.FromDateTime((DateTime)result);
        }

        private static async Task RecordProcessedDateAsync(string bucketName, DateOnly processedDate, int processedCount, string connectionString)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            const string sql = @"
IF NOT EXISTS (SELECT 1 FROM S3BucketProcessedDates WHERE BucketName = @BucketName AND ProcessedDate = @ProcessedDate)
INSERT INTO S3BucketProcessedDates (BucketName, ProcessedDate, ProcessedCount, RecordedAtUtc)
VALUES (@BucketName, @ProcessedDate, @ProcessedCount, @RecordedAtUtc);";

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@BucketName", bucketName);
            command.Parameters.AddWithValue("@ProcessedDate", processedDate.ToDateTime(TimeOnly.MinValue));
            command.Parameters.AddWithValue("@ProcessedCount", processedCount);
            command.Parameters.AddWithValue("@RecordedAtUtc", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync();
        }
    }
}
