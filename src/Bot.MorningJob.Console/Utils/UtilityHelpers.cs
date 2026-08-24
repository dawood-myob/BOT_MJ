using Microsoft.Extensions.Logging;

namespace Bot.MorningJob.Console.Utils
{
    /// <summary>
    /// Logger utility extension methods
    /// </summary>
    public static class LoggerExtensions
    {
        public static void LogOperationStart(this ILogger logger, string operationName)
        {
            logger.LogInformation("Starting operation: {OperationName}", operationName);
        }

        public static void LogOperationEnd(this ILogger logger, string operationName, bool success)
        {
            if (success)
            {
                logger.LogInformation("Completed operation: {OperationName}", operationName);
            }
            else
            {
                logger.LogError("Failed operation: {OperationName}", operationName);
            }
        }

        public static void LogConnectionAttempt(this ILogger logger, string service)
        {
            logger.LogInformation("Attempting connection to {Service}", service);
        }

        public static void LogConnectionSuccess(this ILogger logger, string service)
        {
            logger.LogInformation("Successfully connected to {Service}", service);
        }

        public static void LogConnectionFailure(this ILogger logger, string service, string? reason = null)
        {
            logger.LogError("Failed to connect to {Service}: {Reason}", service, reason ?? "Unknown reason");
        }
    }

    /// <summary>
    /// Retry policy utility
    /// </summary>
    public static class RetryPolicy
    {
        public static async Task<T> ExecuteWithRetryAsync<T>(
            Func<Task<T>> operation,
            int maxRetries = 3,
            int delayMilliseconds = 1000,
            ILogger? logger = null)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    logger?.LogInformation("Attempt {Attempt} of {MaxRetries}", i + 1, maxRetries);
                    return await operation();
                }
                catch (Exception ex)
                {
                    if (i == maxRetries - 1)
                    {
                        logger?.LogError(ex, "All retry attempts exhausted");
                        throw;
                    }

                    logger?.LogWarning(ex, "Attempt {Attempt} failed, retrying...", i + 1);
                    await Task.Delay(delayMilliseconds * (i + 1)); // Exponential backoff
                }
            }

            throw new InvalidOperationException("Unable to execute operation after all retries");
        }
    }

    /// <summary>
    /// Validation utility
    /// </summary>
    public static class ValidationHelper
    {
        public static bool IsValidUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            return Uri.TryCreate(url, UriKind.Absolute, out _);
        }

        public static bool IsValidEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return false;

            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }

        public static bool AreSettingsConfigured(string? setting)
        {
            return !string.IsNullOrWhiteSpace(setting);
        }
    }

    /// <summary>
    /// Date time utility
    /// </summary>
    public static class DateTimeHelper
    {
        public static bool IsRecentlyModified(DateTime lastModified, int minutes = 60)
        {
            var timeDifference = DateTime.UtcNow - lastModified;
            return timeDifference.TotalMinutes <= minutes;
        }

        public static string GetFriendlyTimespan(TimeSpan timespan)
        {
            if (timespan.TotalSeconds < 60)
                return $"{(int)timespan.TotalSeconds}s";
            if (timespan.TotalMinutes < 60)
                return $"{(int)timespan.TotalMinutes}m {(int)timespan.Seconds}s";
            if (timespan.TotalHours < 24)
                return $"{(int)timespan.TotalHours}h {(int)timespan.Minutes}m";
            return $"{(int)timespan.TotalDays}d {(int)timespan.Hours}h";
        }
    }
}
