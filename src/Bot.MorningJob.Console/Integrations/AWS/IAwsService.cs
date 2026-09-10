using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.S3;
using Amazon.S3.Model;
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
    /// Result of checking connectivity/access to an S3 bucket that lives in another AWS
    /// account, reached via a role assumed in that account.
    /// </summary>
    public class S3BucketStatus
    {
        public string? BucketName { get; set; }
        public bool IsAccessible { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// AWS integration service. Assumes a target role via AWS SSO (same flow as the standalone
    /// AWSKeysUpg tool: aws sso login, then aws sts assume-role), then queries SQS queue depths
    /// and S3 bucket accessibility for the DLQ/GTV/Stripe report section. Each S3 bucket may live
    /// in a different AWS account, so credentials are assumed per role ARN and cached.
    /// </summary>
    public interface IAwsService
    {
        /// <summary>
        /// Ensures AWS SSO credentials are available (logs in via "aws sso login" and assumes
        /// the given role via "aws sts assume-role" if not already cached for that role), then
        /// returns AWS credentials usable by the AWS SDK.
        /// </summary>
        Task<AWSCredentials> GetCredentialsAsync(string roleArn);

        /// <summary>
        /// Gets the approximate number of visible messages for the given queue name, using the
        /// role configured in AppSettings:Aws:RoleArn.
        /// </summary>
        Task<SqsQueueStatus> GetQueueMessageCountAsync(string queueName);

        /// <summary>
        /// Verifies connectivity/access to the given S3 bucket by assuming the given role and
        /// performing a lightweight HeadBucket / ListObjectsV2 (MaxKeys=1) call.
        /// </summary>
        Task<S3BucketStatus> TestBucketConnectionAsync(string bucketName, string roleArn, string? region = null);

        /// <summary>
        /// Lists the immediate "folder" names (S3 CommonPrefixes) directly under the given prefix,
        /// e.g. ListCommonPrefixesAsync(bucket, role, "failed/") returns ["2026/"], and
        /// ListCommonPrefixesAsync(bucket, role, "failed/2026/") returns ["01/", "02/", ...].
        /// </summary>
        Task<List<string>> ListCommonPrefixesAsync(string bucketName, string roleArn, string prefix, string? region = null);

        /// <summary>
        /// Lists all objects (recursively, paginated) under the given prefix, e.g. every file
        /// under "processed/2026/09/09/" or every file under "failed/".
        /// </summary>
        Task<List<Amazon.S3.Model.S3Object>> ListObjectsRecursiveAsync(string bucketName, string roleArn, string prefix, string? region = null);
    }

    public class AwsService : IAwsService
    {
        private readonly ILogger<AwsService> _logger;
        private readonly AwsSettings _settings;
        private readonly Dictionary<string, AWSCredentials> _cachedCredentialsByRole = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ssoLoggedInProfiles = new(StringComparer.OrdinalIgnoreCase);

        public AwsService(ILogger<AwsService> logger, IOptions<AppSettings> appSettings)
        {
            _logger = logger;
            _settings = appSettings.Value.Aws ?? new AwsSettings();
        }

        public async Task<AWSCredentials> GetCredentialsAsync(string roleArn)
        {
            if (_cachedCredentialsByRole.TryGetValue(roleArn, out var cached))
            {
                return cached;
            }

            var profileName = string.IsNullOrWhiteSpace(_settings.SsoProfileName) ? "default" : _settings.SsoProfileName;
            var roleSessionName = string.IsNullOrWhiteSpace(_settings.RoleSessionName)
                ? "bot-morningjob-session"
                : _settings.RoleSessionName;

            await EnsureSsoLoginAsync(profileName);

            var (accessKeyId, secretAccessKey, sessionToken) = await AssumeRoleAsync(roleArn, roleSessionName, profileName);

            var credentials = new SessionAWSCredentials(accessKeyId, secretAccessKey, sessionToken);
            _cachedCredentialsByRole[roleArn] = credentials;
            return credentials;
        }

        /// <summary>
        /// Runs "aws sso login --profile {profileName}". If the SSO session is already valid,
        /// this typically returns immediately without prompting the browser. The login is only
        /// performed once per profile per process run (subsequent calls for the same profile,
        /// e.g. when assuming multiple different roles, are skipped) to avoid opening multiple
        /// browser tabs for what is really the same SSO session.
        /// </summary>
        private async Task EnsureSsoLoginAsync(string profileName)
        {
            if (_ssoLoggedInProfiles.Contains(profileName))
            {
                return;
            }

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

            _ssoLoggedInProfiles.Add(profileName);
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
            var roleArn = _settings.RoleArn
                ?? throw new InvalidOperationException("AppSettings:Aws:RoleArn is not configured.");

            var credentials = await GetCredentialsAsync(roleArn);
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

        public async Task<S3BucketStatus> TestBucketConnectionAsync(string bucketName, string roleArn, string? region = null)
        {
            try
            {
                var credentials = await GetCredentialsAsync(roleArn);
                var effectiveRegion = string.IsNullOrWhiteSpace(region)
                    ? (string.IsNullOrWhiteSpace(_settings.Region) ? "ap-southeast-2" : _settings.Region)
                    : region;

                using var s3Client = new AmazonS3Client(credentials, RegionEndpoint.GetBySystemName(effectiveRegion));

                await s3Client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = bucketName,
                    MaxKeys = 1
                });

                _logger.LogInformation("Connected to S3 bucket {BucketName} via role {RoleArn}.", bucketName, roleArn);

                return new S3BucketStatus
                {
                    BucketName = bucketName,
                    IsAccessible = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to S3 bucket {BucketName} via role {RoleArn}.", bucketName, roleArn);
                return new S3BucketStatus
                {
                    BucketName = bucketName,
                    IsAccessible = false,
                    ErrorMessage = ex.Message
                };
            }
        }
            public async Task<List<string>> ListCommonPrefixesAsync(string bucketName, string roleArn, string prefix, string? region = null)
            {
                var credentials = await GetCredentialsAsync(roleArn);
                var effectiveRegion = string.IsNullOrWhiteSpace(region)
                    ? (string.IsNullOrWhiteSpace(_settings.Region) ? "ap-southeast-2" : _settings.Region)
                    : region;

                using var s3Client = new AmazonS3Client(credentials, RegionEndpoint.GetBySystemName(effectiveRegion));

                var results = new List<string>();
                string? continuationToken = null;

                do
                {
                    var response = await s3Client.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = bucketName,
                        Prefix = prefix,
                        Delimiter = "/",
                        ContinuationToken = continuationToken
                    });

                    results.AddRange(response.CommonPrefixes);
                    continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
                }
                while (continuationToken != null);

                return results;
            }

            public async Task<List<Amazon.S3.Model.S3Object>> ListObjectsRecursiveAsync(string bucketName, string roleArn, string prefix, string? region = null)
            {
                var credentials = await GetCredentialsAsync(roleArn);
                var effectiveRegion = string.IsNullOrWhiteSpace(region)
                    ? (string.IsNullOrWhiteSpace(_settings.Region) ? "ap-southeast-2" : _settings.Region)
                    : region;

                using var s3Client = new AmazonS3Client(credentials, RegionEndpoint.GetBySystemName(effectiveRegion));

                var results = new List<Amazon.S3.Model.S3Object>();
                string? continuationToken = null;

                do
                {
                    var response = await s3Client.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = bucketName,
                        Prefix = prefix,
                        ContinuationToken = continuationToken
                    });

                    results.AddRange(response.S3Objects);
                    continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
                }
                while (continuationToken != null);

                return results;
            }
        }
    }
