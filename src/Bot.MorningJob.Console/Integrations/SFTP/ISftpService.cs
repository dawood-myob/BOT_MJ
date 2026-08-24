using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bot.MorningJob.Console.Integrations.SFTP
{
    /// <summary>
    /// SFTP Integration Service
    /// Handles SFTP server connectivity and file monitoring
    /// </summary>
    public interface ISftpService
    {
        /// <summary>
        /// Connects to SFTP server and validates connection
        /// </summary>
        Task<bool> ConnectAsync();

        /// <summary>
        /// Checks for files in specified directory
        /// </summary>
        Task<List<SftpFileInfo>> GetFilesAsync(string directory);

        /// <summary>
        /// Validates file existence and metadata
        /// </summary>
        Task<SftpFileValidationResult> ValidateFileAsync(string filePath);

        /// <summary>
        /// Runs a daily inspection and downloads files newer than the previous inspection.
        /// </summary>
        Task<SftpInspectionResult> InspectAndDownloadNewFilesAsync();

        /// <summary>
        /// Downloads a single remote file to a local path.
        /// </summary>
        Task DownloadFileAsync(string remotePath, string localPath);

        /// <summary>
        /// Disconnects from SFTP server
        /// </summary>
        void Disconnect();
    }

    /// <summary>
    /// SFTP file information
    /// </summary>
    public class SftpFileInfo
    {
        public string? Name { get; set; }
        public string? FullPath { get; set; }
        public long Size { get; set; }
        public DateTime LastModified { get; set; }
        public bool IsDirectory { get; set; }
    }

    /// <summary>
    /// SFTP file validation result
    /// </summary>
    public class SftpFileValidationResult
    {
        public bool IsValid { get; set; }
        public string? FilePath { get; set; }
        public string? Message { get; set; }
        public DateTime CheckedAt { get; set; }
    }

    public class SftpInspectionResult
    {
        public DateTimeOffset InspectionStartedUtc { get; set; }
        public DateTimeOffset InspectionCompletedUtc { get; set; }
        public DateTimeOffset? PreviousInspectionUtc { get; set; }
        public string ProcessedDirectory { get; set; } = string.Empty;
        public string FailedDirectory { get; set; } = string.Empty;
        public List<SftpFileInfo> NewProcessedFiles { get; set; } = new();
        public List<SftpFileInfo> NewFailedFiles { get; set; } = new();
        public List<string> DownloadedLocalPaths { get; set; } = new();
    }

    internal sealed class SftpInspectionState
    {
        public DateTimeOffset? LastInspectionUtc { get; set; }
    }

    /// <summary>
    /// SFTP Service Implementation.
    ///
    /// GUARDRAIL: This service must NEVER modify, delete, upload, or rename anything on the
    /// remote SFTP server. It is strictly read-only (list directories, check existence/metadata,
    /// and download files). All remote interactions are funneled through <see cref="_readOnlyClient"/>,
    /// a narrow wrapper that only exposes safe/read operations from Renci.SshNet.SftpClient.
    /// Do not add write/delete/rename methods to <see cref="ReadOnlySftpClient"/> or bypass it
    /// by calling the underlying SftpClient directly.
    /// </summary>
    public class SftpService : ISftpService
    {
        private readonly ILogger<SftpService> _logger;
        private readonly SftpSettings _settings;
        private SftpClient? _client;
        private ReadOnlySftpClient? _readOnlyClient;

        public SftpService(IOptions<AppSettings> appSettings, ILogger<SftpService> logger)
        {
            _logger = logger;
            _settings = appSettings.Value.Sftp ?? new SftpSettings();
        }

        public Task<bool> ConnectAsync()
        {
            EnsureConnected();
            return Task.FromResult(_client?.IsConnected == true);
        }

        public Task DownloadFileAsync(string remotePath, string localPath)
        {
            EnsureConnected();
            GuardAgainstRemoteWrite(localPath, remotePath);

            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var localStream = File.Create(localPath);
            _readOnlyClient!.DownloadFile(remotePath, localStream);
            _logger.LogInformation("Downloaded remote file {RemotePath} to {LocalPath}", remotePath, localPath);

            return Task.CompletedTask;
        }

        public Task<List<SftpFileInfo>> GetFilesAsync(string directory)
        {
            EnsureConnected();

            var files = _readOnlyClient!
                .ListDirectory(directory)
                .Where(file => !file.IsDirectory && file.Name != "." && file.Name != "..")
                .Select(MapFileInfo)
                .OrderByDescending(file => file.LastModified)
                .ToList();

            return Task.FromResult(files);
        }

        public Task<SftpFileValidationResult> ValidateFileAsync(string filePath)
        {
            EnsureConnected();

            var result = new SftpFileValidationResult
            {
                FilePath = filePath,
                CheckedAt = DateTime.UtcNow
            };

            if (_readOnlyClient!.Exists(filePath))
            {
                var attributes = _readOnlyClient.GetAttributes(filePath);
                result.IsValid = attributes != null;
                result.Message = "File exists and metadata is accessible.";
            }
            else
            {
                result.IsValid = false;
                result.Message = "File does not exist.";
            }

            return Task.FromResult(result);
        }

        public async Task<SftpInspectionResult> InspectAndDownloadNewFilesAsync()
        {
            EnsureConnected();

            var inspectionStartedUtc = DateTimeOffset.UtcNow;
            var statePath = ResolvePath(_settings.LastInspectionStatePath, "./Config/sftp-inspection-state.json");
            var localDownloadRoot = ResolvePath(_settings.LocalDownloadRootPath, "./downloads/sftp");
            var processedDirectory = _settings.ProcessedDirectory ?? "/INT002/PROD/processed";
            var failedDirectory = _settings.FailedDirectory ?? "/INT002/PROD/failed";

            var state = await LoadStateAsync(statePath);
            var previousInspectionUtc = state.LastInspectionUtc;

            _logger.LogInformation(
                "SFTP inspection started. Previous inspection UTC: {PreviousInspectionUtc}",
                previousInspectionUtc?.ToString("O") ?? "(none)");

            var processedFiles = await GetFilesAsync(processedDirectory);
            var failedFiles = await GetFilesAsync(failedDirectory);

            var newProcessedFiles = FilterNewFiles(processedFiles, previousInspectionUtc);
            var newFailedFiles = FilterNewFiles(failedFiles, previousInspectionUtc);

            var timestampFolder = inspectionStartedUtc.ToString("yyyyMMdd-HHmmss");
            var processedDownloadDir = Path.Combine(localDownloadRoot, timestampFolder, "processed");
            var failedDownloadDir = Path.Combine(localDownloadRoot, timestampFolder, "failed");
            Directory.CreateDirectory(processedDownloadDir);
            Directory.CreateDirectory(failedDownloadDir);

            var downloadedPaths = new List<string>();

            foreach (var file in newProcessedFiles)
            {
                var localPath = DownloadFile(file, processedDownloadDir);
                downloadedPaths.Add(localPath);
            }

            foreach (var file in newFailedFiles)
            {
                var localPath = DownloadFile(file, failedDownloadDir);
                downloadedPaths.Add(localPath);
            }

            var completedUtc = DateTimeOffset.UtcNow;
            state.LastInspectionUtc = completedUtc;
            await SaveStateAsync(statePath, state);

            return new SftpInspectionResult
            {
                InspectionStartedUtc = inspectionStartedUtc,
                InspectionCompletedUtc = completedUtc,
                PreviousInspectionUtc = previousInspectionUtc,
                ProcessedDirectory = processedDirectory,
                FailedDirectory = failedDirectory,
                NewProcessedFiles = newProcessedFiles,
                NewFailedFiles = newFailedFiles,
                DownloadedLocalPaths = downloadedPaths
            };
        }

        public void Disconnect()
        {
            if (_client == null)
            {
                return;
            }

            if (_client.IsConnected)
            {
                _client.Disconnect();
            }

            _client.Dispose();
            _client = null;
            _readOnlyClient = null;
        }

        private void EnsureConnected()
        {
            if (_client?.IsConnected == true)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_settings.Host))
            {
                throw new InvalidOperationException("SFTP host is not configured.");
            }

            if (string.IsNullOrWhiteSpace(_settings.Username))
            {
                throw new InvalidOperationException("SFTP username is not configured.");
            }

            var authMethods = new List<AuthenticationMethod>();

            var resolvedPrivateKeyPath = ResolvePrivateKeyPath();
            if (!string.IsNullOrWhiteSpace(resolvedPrivateKeyPath))
            {
                var workingCopyPath = ResolvePath(
                    _settings.PrivateKeyWorkingCopyPath,
                    "./Config/keys/PG_Prod_SSH.ppk");

                var keyToUse = PrepareKeyWorkingCopy(resolvedPrivateKeyPath, workingCopyPath);

                var privateKeyAdded = TryAddPrivateKeyAuthentication(authMethods, keyToUse, out var keyLoadException);
                if (!privateKeyAdded)
                {
                    if (!string.IsNullOrWhiteSpace(_settings.Password))
                    {
                        _logger.LogWarning(
                            keyLoadException,
                            "Failed to load SFTP private key from '{KeyPath}'. Falling back to password authentication.",
                            keyToUse);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"Failed to load private key from '{keyToUse}'. If this is a PuTTY .ppk key and parsing fails, convert it to OpenSSH PEM format and update AppSettings:Sftp:PrivateKeyPath.",
                            keyLoadException);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(_settings.Password))
            {
                authMethods.Add(new PasswordAuthenticationMethod(_settings.Username, _settings.Password));
            }

            if (authMethods.Count == 0)
            {
                throw new InvalidOperationException("No SFTP authentication method configured. Configure PrivateKeyPath or Password.");
            }

            var connectionInfo = new ConnectionInfo(
                _settings.Host,
                _settings.Port,
                _settings.Username,
                authMethods.ToArray());

            _client = new SftpClient(connectionInfo);
            _client.Connect();
            _readOnlyClient = new ReadOnlySftpClient(_client);

            _logger.LogInformation("Connected to SFTP host {Host}:{Port}", _settings.Host, _settings.Port);
        }

        /// <summary>
        /// Defense-in-depth guard: this service is read-only against the remote SFTP server.
        /// This throws if anything ever attempts to treat a remote path as a local write target
        /// (e.g. a future bug that swaps arguments), preventing accidental writes to the SFTP mount
        /// or misdirected local writes.
        /// </summary>
        private static void GuardAgainstRemoteWrite(string localPath, string remotePath)
        {
            if (string.Equals(localPath, remotePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to proceed: local and remote paths are identical, which could indicate an attempt to write to the remote SFTP location.");
            }
        }

        private static string PrepareKeyWorkingCopy(string sourcePath, string destinationPath)
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"SFTP private key not found at '{sourcePath}'.", sourcePath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
            return destinationPath;
        }

        private bool TryAddPrivateKeyAuthentication(List<AuthenticationMethod> authMethods, string keyPath, out Exception? failure)
        {
            failure = null;

            if (TryLoadPrivateKeyAuthentication(authMethods, keyPath, out failure))
            {
                return true;
            }

            if (!keyPath.EndsWith(".ppk", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var pemPath = Path.ChangeExtension(keyPath, ".pem");
            if (TryConvertPpkToPem(keyPath, pemPath, out var conversionError))
            {
                if (TryLoadPrivateKeyAuthentication(authMethods, pemPath, out var pemFailure))
                {
                    _logger.LogInformation("Converted PuTTY key '{PpkPath}' to OpenSSH PEM '{PemPath}' for SFTP authentication.", keyPath, pemPath);
                    return true;
                }

                failure = pemFailure;
            }
            else
            {
                failure = conversionError;
            }

            return false;
        }

        private bool TryLoadPrivateKeyAuthentication(List<AuthenticationMethod> authMethods, string keyPath, out Exception? failure)
        {
            failure = null;

            try
            {
                authMethods.Add(new PrivateKeyAuthenticationMethod(
                    _settings.Username!,
                    new PrivateKeyFile(keyPath)));
                return true;
            }
            catch (Exception ex)
            {
                failure = ex;
                return false;
            }
        }

        private bool TryConvertPpkToPem(string ppkPath, string pemPath, out Exception? error)
        {
            error = null;

            try
            {
                var lines = File.ReadAllLines(ppkPath);
                if (lines.Length == 0 || !lines[0].StartsWith("PuTTY-User-Key-File", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("File does not have a valid PuTTY header.");
                }

                var header = lines[0];
                var keyType = header.Split(':', 2)[1].Trim();

                string? encryption = null;
                int publicLines = 0;
                int privateLines = 0;
                var publicBlob = new StringBuilder();
                var privateBlob = new StringBuilder();

                var i = 1;
                for (; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.StartsWith("Encryption:", StringComparison.OrdinalIgnoreCase))
                    {
                        encryption = line.Split(':', 2)[1].Trim();
                    }
                    else if (line.StartsWith("Public-Lines:", StringComparison.OrdinalIgnoreCase))
                    {
                        publicLines = int.Parse(line.Split(':', 2)[1].Trim());
                        for (int j = 0; j < publicLines; j++)
                        {
                            publicBlob.Append(lines[++i].Trim());
                        }
                    }
                    else if (line.StartsWith("Private-Lines:", StringComparison.OrdinalIgnoreCase))
                    {
                        privateLines = int.Parse(line.Split(':', 2)[1].Trim());
                        for (int j = 0; j < privateLines; j++)
                        {
                            privateBlob.Append(lines[++i].Trim());
                        }
                    }
                }

                if (!string.Equals(encryption, "none", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException("Encrypted PuTTY keys are not supported for automatic conversion. Please provide an unencrypted key or convert it manually using PuTTYgen.");
                }

                if (!keyType.Equals("ssh-rsa", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException($"Automatic conversion only supports ssh-rsa keys. Found '{keyType}'.");
                }

                var publicKeyBytes = Convert.FromBase64String(publicBlob.ToString());
                var privateKeyBytes = Convert.FromBase64String(privateBlob.ToString());

                using var pubReader = new SshDataReader(publicKeyBytes);
                var algName = pubReader.ReadString();
                var e = pubReader.ReadMpint();
                var n = pubReader.ReadMpint();

                using var privReader = new SshDataReader(privateKeyBytes);
                var d = privReader.ReadMpint();
                var p = privReader.ReadMpint();
                var q = privReader.ReadMpint();
                var iqmp = privReader.ReadMpint();

                var modulus = TrimLeadingZero(n);
                var pTrimmed = TrimLeadingZero(p);
                var qTrimmed = TrimLeadingZero(q);

                var modulusLength = modulus.Length;
                var halfLength = (modulusLength + 1) / 2;

                var rsaParameters = new RSAParameters
                {
                    Modulus = modulus,
                    Exponent = TrimLeadingZero(e),
                    D = PadTo(d, modulusLength),
                    P = PadTo(pTrimmed, halfLength),
                    Q = PadTo(qTrimmed, halfLength),
                    InverseQ = PadTo(iqmp, halfLength)
                };

                var dp = ModInverseHelper.ComputeExponent(TrimLeadingZero(d), pTrimmed);
                var dq = ModInverseHelper.ComputeExponent(TrimLeadingZero(d), qTrimmed);
                rsaParameters.DP = PadTo(dp, halfLength);
                rsaParameters.DQ = PadTo(dq, halfLength);

                using var rsa = RSA.Create();
                rsa.ImportParameters(rsaParameters);

                var pkcs1Bytes = rsa.ExportRSAPrivateKey();
                var base64 = Convert.ToBase64String(pkcs1Bytes);

                var pem = new StringBuilder();
                pem.AppendLine("-----BEGIN RSA PRIVATE KEY-----");
                for (int offset = 0; offset < base64.Length; offset += 64)
                {
                    pem.AppendLine(base64.Substring(offset, Math.Min(64, base64.Length - offset)));
                }
                pem.AppendLine("-----END RSA PRIVATE KEY-----");

                File.WriteAllText(pemPath, pem.ToString());
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }

        private static byte[] TrimLeadingZero(byte[] data)
        {
            if (data.Length > 1 && data[0] == 0)
            {
                return data[1..];
            }

            return data;
        }

        private static byte[] PadTo(byte[] data, int length)
        {
            var trimmed = TrimLeadingZero(data);
            if (trimmed.Length >= length)
            {
                return trimmed;
            }

            var result = new byte[length];
            Buffer.BlockCopy(trimmed, 0, result, length - trimmed.Length, trimmed.Length);
            return result;
        }

        private sealed class SshDataReader : IDisposable
        {
            private readonly byte[] _data;
            private int _position;

            public SshDataReader(byte[] data)
            {
                _data = data;
            }

            public string ReadString()
            {
                var length = ReadUInt32();
                var value = Encoding.ASCII.GetString(_data, _position, (int)length);
                _position += (int)length;
                return value;
            }

            public byte[] ReadMpint()
            {
                var length = ReadUInt32();
                var value = new byte[length];
                Array.Copy(_data, _position, value, 0, (int)length);
                _position += (int)length;
                return value;
            }

            private uint ReadUInt32()
            {
                var value = (uint)((_data[_position] << 24) | (_data[_position + 1] << 16) | (_data[_position + 2] << 8) | _data[_position + 3]);
                _position += 4;
                return value;
            }

            public void Dispose()
            {
            }
        }

        private static class ModInverseHelper
        {
            public static byte[] ComputeExponent(byte[] d, byte[] prime)
            {
                var dBig = new System.Numerics.BigInteger(PrependZero(d), isUnsigned: false, isBigEndian: true);
                var primeBig = new System.Numerics.BigInteger(PrependZero(prime), isUnsigned: false, isBigEndian: true);
                var exponent = dBig % (primeBig - 1);

                var bytes = exponent.ToByteArray(isUnsigned: false, isBigEndian: true);
                return TrimLeadingZero(bytes);
            }

            private static byte[] PrependZero(byte[] data)
            {
                if (data.Length == 0 || data[0] < 0x80)
                {
                    return data;
                }

                var result = new byte[data.Length + 1];
                Buffer.BlockCopy(data, 0, result, 1, data.Length);
                return result;
            }

            private static byte[] TrimLeadingZero(byte[] data)
            {
                if (data.Length > 1 && data[0] == 0)
                {
                    return data[1..];
                }

                return data;
            }
        }

        private string? ResolvePrivateKeyPath()
        {
            var candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(_settings.PrivateKeyPath))
            {
                candidates.Add(ResolvePath(_settings.PrivateKeyPath, _settings.PrivateKeyPath));
            }

            candidates.Add(ResolvePath("./PG_Prod_SSH.ppk", "./PG_Prod_SSH.ppk"));
            candidates.Add(ResolvePath("./Config/keys/PG_Prod_SSH.ppk", "./Config/keys/PG_Prod_SSH.ppk"));

            var existingPath = candidates.FirstOrDefault(File.Exists);

            if (!string.IsNullOrWhiteSpace(existingPath))
            {
                return existingPath;
            }

            if (!string.IsNullOrWhiteSpace(_settings.PrivateKeyPath))
            {
                _logger.LogWarning("Configured SFTP private key path '{PrivateKeyPath}' was not found. Falling back to password auth if configured.", _settings.PrivateKeyPath);
            }

            return null;
        }

        private List<SftpFileInfo> FilterNewFiles(List<SftpFileInfo> files, DateTimeOffset? previousInspectionUtc)
        {
            if (previousInspectionUtc is null)
            {
                return files;
            }

            return files
                .Where(file => file.LastModified > previousInspectionUtc.Value.UtcDateTime)
                .OrderBy(file => file.LastModified)
                .ToList();
        }

        private string DownloadFile(SftpFileInfo file, string localDirectory)
        {
            if (string.IsNullOrWhiteSpace(file.FullPath) || string.IsNullOrWhiteSpace(file.Name))
            {
                throw new InvalidOperationException("Cannot download a file with missing remote path or file name.");
            }

            var safeName = Path.GetFileName(file.Name);
            var localPath = Path.Combine(localDirectory, safeName);

            if (File.Exists(localPath))
            {
                var uniqueName = $"{Path.GetFileNameWithoutExtension(safeName)}-{DateTime.UtcNow:HHmmssfff}{Path.GetExtension(safeName)}";
                localPath = Path.Combine(localDirectory, uniqueName);
            }

            using var localStream = File.Create(localPath);
            _readOnlyClient!.DownloadFile(file.FullPath, localStream);
            _logger.LogInformation("Downloaded remote file {RemotePath} to {LocalPath}", file.FullPath, localPath);

            return localPath;
        }

        private static SftpFileInfo MapFileInfo(ISftpFile file)
        {
            return new SftpFileInfo
            {
                Name = file.Name,
                FullPath = file.FullName,
                Size = file.Attributes.Size,
                LastModified = file.LastWriteTimeUtc,
                IsDirectory = file.IsDirectory
            };
        }

        private static async Task<SftpInspectionState> LoadStateAsync(string statePath)
        {
            if (!File.Exists(statePath))
            {
                return new SftpInspectionState();
            }

            var json = await File.ReadAllTextAsync(statePath);
            return JsonSerializer.Deserialize<SftpInspectionState>(json) ?? new SftpInspectionState();
        }

        private static async Task SaveStateAsync(string statePath, SftpInspectionState state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(statePath, json);
        }

        private static string ResolvePath(string? configuredPath, string fallback)
        {
            var path = string.IsNullOrWhiteSpace(configuredPath) ? fallback : configuredPath;

            if (Path.IsPathRooted(path))
            {
                return path;
            }

            return Path.GetFullPath(path, Directory.GetCurrentDirectory());
        }
    }

    /// <summary>
    /// GUARDRAIL: A narrow, read-only facade over <see cref="SftpClient"/>.
    ///
    /// This class intentionally exposes ONLY safe, non-destructive operations
    /// (list directory, check existence, read attributes, download a file).
    ///
    /// Do NOT add any of the following to this class or use the underlying
    /// SftpClient directly elsewhere in this codebase:
    ///   - UploadFile / WriteAllText / Create (write to remote)
    ///   - DeleteFile / DeleteDirectory
    ///   - RenameFile / Move
    ///   - SetAttributes / ChangePermissions / SymbolicLink
    ///
    /// This bot must never modify or remove files on the remote SFTP server
    /// under any circumstances.
    /// </summary>
    internal sealed class ReadOnlySftpClient
    {
        private readonly SftpClient _client;

        public ReadOnlySftpClient(SftpClient client)
        {
            _client = client;
        }

        public IEnumerable<ISftpFile> ListDirectory(string path) => _client.ListDirectory(path);

        public bool Exists(string path) => _client.Exists(path);

        public SftpFileAttributes GetAttributes(string path) => _client.GetAttributes(path);

        /// <summary>
        /// Reads a remote file into a local stream. This only reads from the remote server;
        /// the destination stream is always a local file provided by the caller.
        /// </summary>
        public void DownloadFile(string remotePath, Stream localDestination) => _client.DownloadFile(remotePath, localDestination);
    }
}
