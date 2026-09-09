using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;

namespace Bot.MorningJob.Console.Integrations.Aws
{
    /// <summary>
    /// Result of checking the payout-transactions SQS queue and its DLQ.
    /// </summary>
    public class SqsQueueStatus
    {
        public string? QueueName { get; set; }
        public int ApproximateMessageCount { get; set; }
    }

    /// <summary>
    /// AWS integration service. Assumes the target-workday-integration-operations role via
    /// AWS SSO (same flow as the standalone AWSKeysUpg tool: aws sso login, then
    /// aws sts assume-role), then queries SQS queue depths for the DLQ/GTV/Stripe report
    /// section.
    /// </summary>
    public interface IAwsService
    {
        /// <summary>
        /// Ensures AWS SSO credentials are available (logs in via "aws sso login" and assumes
        /// the configured role via "aws sts assume-role" if needed), then returns AWS
        /// credentials usable by the AWS SDK.
        /// </summary>
        Task<AWSCredentials> GetCredentialsAsync();

        /// <summary>
        /// Gets the approximate number of visible messages for the given queue name.
        /// </summary>
        Task<SqsQueueStatus> GetQueueMessageCountAsync(string queueName);
    }

    public class AwsService : IAwsService
    {
        private readonly ILogger<AwsService> _logger;
        private readonly AwsSettings _settings;
        private AWSCredentials? _cachedCredentials;

        public AwsService(ILogger<AwsService> logger, IOptions<AppSettings> appSettings)
        {
            _logger = logger;
            _settings = appSettings.Value.Aws ?? new AwsSettings();
        }

        public async Task<AWSCredentials> GetCredentialsAsync()
        {
            if (_cachedCredentials != null)
            {
                return _cachedCredentials;
            }

            var profileName = string.IsNullOrWhiteSpace(_settings.SsoProfileName) ? "default" : _settings.SsoProfileName;
            var roleArn = _settings.RoleArn
                ?? throw new InvalidOperationException("AppSettings:Aws:RoleArn is not configured.");
            var roleSessionName = string.IsNullOrWhiteSpace(_settings.RoleSessionName)
                ? "bot-morningjob-session"
                : _settings.RoleSessionName;

            await EnsureSsoLoginAsync(profileName);

            var (accessKeyId, secretAccessKey, sessionToken) = await AssumeRoleAsync(roleArn, roleSessionName, profileName);

            _cachedCredentials = new SessionAWSCredentials(accessKeyId, secretAccessKey, sessionToken);
            return _cachedCredentials;
        }

        /// <summary>
        /// Runs "aws sso login --profile {profileName}". If the SSO session is already valid,
        /// this typically returns immediately without prompting the browser.
        /// </summary>
        private async Task EnsureSsoLoginAsync(string profileName)
        {
            var ssoLoginProcess = new ProcessStartInfo
            {
                FileName = "aws",
                Arguments = $"sso login --profile {profileName}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _logger.LogInformation("Ensuring AWS SSO login for profile {ProfileName}...", profileName);

            using var process = Process.Start(ssoLoginProcess)
                ?? throw new InvalidOperationException("Failed to start AWS SSO login process.");

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"AWS SSO login failed: {error}");
            }

            _logger.LogInformation("AWS SSO login successful. {Output}", output.Trim());
        }

        /// <summary>
        /// Runs "aws sts assume-role --role-arn ... --role-session-name ... --profile ..."
        /// and parses the temporary AccessKeyId/SecretAccessKey/SessionToken from the JSON response.
        /// </summary>
        private async Task<(string AccessKeyId, string SecretAccessKey, string SessionToken)> AssumeRoleAsync(
            string roleArn, string roleSessionName, string profileName)
        {
            var assumeRoleProcess = new ProcessStartInfo
            {
                FileName = "aws",
                Arguments = $"sts assume-role --role-arn \"{roleArn}\" --role-session-name {roleSessionName} --profile {profileName}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(assumeRoleProcess)
                ?? throw new InvalidOperationException("Failed to start AWS STS assume-role process.");

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"AWS STS assume-role failed: {error}");
            }

            using var jsonDocument = JsonDocument.Parse(output);
            var credentials = jsonDocument.RootElement.GetProperty("Credentials");

            var accessKeyId = credentials.GetProperty("AccessKeyId").GetString();
            var secretAccessKey = credentials.GetProperty("SecretAccessKey").GetString();
            var sessionToken = credentials.GetProperty("SessionToken").GetString();

            if (string.IsNullOrEmpty(accessKeyId) || string.IsNullOrEmpty(secretAccessKey) || string.IsNullOrEmpty(sessionToken))
            {
                throw new InvalidOperationException("Failed to extract credentials from AWS STS assume-role response.");
            }

            _logger.LogInformation("Assumed role {RoleArn} successfully.", roleArn);

            return (accessKeyId, secretAccessKey, sessionToken);
        }

        public async Task<SqsQueueStatus> GetQueueMessageCountAsync(string queueName)
        {
            var credentials = await GetCredentialsAsync();
            var region = string.IsNullOrWhiteSpace(_settings.Region) ? "ap-southeast-2" : _settings.Region;

            using var sqsClient = new AmazonSQSClient(credentials, RegionEndpoint.GetBySystemName(region));

            var queueUrlResponse = await sqsClient.GetQueueUrlAsync(queueName);

            var attributesResponse = await sqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = queueUrlResponse.QueueUrl,
                AttributeNames = new List<string> { "ApproximateNumberOfMessages" }
            });

            var messageCount = attributesResponse.ApproximateNumberOfMessages;

            return new SqsQueueStatus
            {
                QueueName = queueName,
                ApproximateMessageCount = messageCount
            };
        }
    }
}
