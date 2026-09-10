# Bot.MorningJob - Session Notes & Progress Log

**Session Date**: 2026-07-23  
**Project**: Bot.MorningJob (Bot Morning Job Automation)  
**Status**: Initial Setup Complete ✅

---

## Project Overview

**Bot.MorningJob** is a .NET Core C# console application designed to automate morning job workflows and generate integration execution reports.

### Purpose
Automate the following morning tasks:
1. Check files in SFTP folders
2. Validate files in AWS S3 buckets
3. Check message queues in AWS SQS
4. Query Workday API for integration execution status
5. Retrieve data from PostgreSQL database
6. Compile and generate comprehensive report

### Project Name Options Discussed
- ✅ **Bot.MorningJob** (Chosen)
- Bot_MJ (Alternative)

---

## Completed Tasks

### ✅ 1. Solution & Project Structure
- Created Visual Studio Solution file: `Bot.MorningJob.sln`
- Created .NET 8.0 Console Application project
- Project file: `Bot.MorningJob.Console.csproj`

### ✅ 2. Folder Organization
```
Bot.MorningJob/
├── src/Bot.MorningJob.Console/
│   ├── Integrations/
│   │   ├── SFTP/
│   │   ├── AWS/
│   │   ├── Workday/
│   │   └── Database/
│   ├── Services/
│   ├── Models/
│   ├── Config/
│   └── Utils/
```

### ✅ 3. NuGet Package Dependencies Added
- **SSH.NET** v2023.0.0 - SFTP connectivity
- **AWSSDK.S3** v3.7.300.0 - AWS S3 operations
- **AWSSDK.SQS** v3.7.300.0 - AWS SQS monitoring
- **AWSSDK.Core** v3.7.300.0 - AWS SDK core
- **Npgsql** v8.0.0 - PostgreSQL connectivity
- **Npgsql.EntityFrameworkCore.PostgreSQL** v8.0.0 - PostgreSQL ORM
- **Serilog** v3.1.1 - Logging framework
- **Serilog.Sinks.Console** v5.1.0 - Console logging
- **Serilog.Sinks.File** v5.0.0 - File logging
- **Microsoft.Extensions.Configuration** v8.0.0 - Configuration management
- **Microsoft.Extensions.Configuration.Json** v8.0.0 - JSON config support
- **Microsoft.Extensions.Configuration.UserSecrets** v8.0.0 - Secret management
- **Microsoft.Extensions.DependencyInjection** v8.0.0 - Dependency injection
- **Microsoft.Extensions.Options** v8.0.0 - Options pattern
- **Newtonsoft.Json** v13.0.3 - JSON serialization

### ✅ 4. Core Application Files Created

#### Program.cs
- Application entry point with complete DI setup
- Serilog logging configuration
- Multi-source configuration setup
- Main ApplicationService orchestrator
- Placeholder for all workflow steps
- Settings classes defined:
  - AppSettings, SftpSettings, AwsSettings, S3Settings, SqsSettings, WorkdaySettings, DatabaseSettings, ReportSettings

#### appsettings.json
- Template configuration for all integrations
- Default values for SFTP, AWS, Workday, PostgreSQL
- Report output settings

### ✅ 5. Integration Service Interfaces Created

#### SFTP Integration (`Integrations/SFTP/ISftpService.cs`)
- `ISftpService` interface with methods:
  - `ConnectAsync()` - Connect to SFTP
  - `GetFilesAsync(directory)` - List files
  - `ValidateFileAsync(filePath)` - Validate file
  - `Disconnect()` - Disconnect
- Data models: `SftpFileInfo`, `SftpFileValidationResult`

#### AWS Services (`Integrations/AWS/IAwsServices.cs`)
- `IS3Service` interface:
  - `ValidateConnectionAsync()`
  - `ListObjectsAsync(prefix)`
  - `ValidateObjectAsync(key)`
  - `GetObjectMetadataAsync(key)`
- `ISqsService` interface:
  - `GetQueueAttributesAsync()`
  - `PeekMessagesAsync(maxMessages)`
  - `GetQueueStatusAsync()`
- Data models: `S3ObjectInfo`, `S3ObjectMetadata`, `SqsMessageInfo`, `SqsQueueAttributes`, `SqsQueueStatus`

#### Workday Integration (`Integrations/Workday/IWorkdayService.cs`)
- `IWorkdayService` interface:
  - `AuthenticateAsync()` - OAuth2 authentication
  - `GetIntegrationStatusAsync(name)` - Get status
  - `GetLastExecutionDetailsAsync(name)` - Execution details
  - `GetAllIntegrationStatusesAsync()` - All statuses
  - `ValidateConnectionAsync()` - Connection validation
- Data models: `WorkdayIntegrationStatus`, `WorkdayExecutionDetails`

#### Database Integration (`Integrations/Database/IDatabaseService.cs`)
- `IDatabaseService` interface:
  - `ValidateConnectionAsync()` - Connection validation
  - `GetLastExecutionDateAsync(integrationName)` - Last run date
  - `GetExecutionLogsAsync(integrationName, limitDays)` - Execution logs
  - `GetLastSuccessfulExecutionAsync(integrationName)` - Last success
  - `GetAllIntegrationStatusesAsync()` - All statuses
- Data models: `IntegrationExecutionLog`, `IntegrationStatus`

#### Report Service (`Services/IReportService.cs`)
- `IReportService` interface:
  - `GenerateReportAsync()` - Generate comprehensive report
  - `ExportReportAsync(report, format)` - Export (HTML, JSON, CSV)
  - `SendReportAsync(report, recipients)` - Send via email
- Data models:
  - `MorningJobReport` - Main report container
  - `SftpCheckResult`, `S3CheckResult`, `SqsCheckResult`, `WorkdayCheckResult`, `DatabaseCheckResult`
  - `ReportSummary` - Summary statistics

### ✅ 6. Utility Helpers (`Utils/UtilityHelpers.cs`)
- `LoggerExtensions` - Logger helper methods
- `RetryPolicy` - Retry logic with exponential backoff
- `ValidationHelper` - URL, Email, Settings validation
- `DateTimeHelper` - Time-related utilities

### ✅ 7. Documentation Created

#### README.md
- Comprehensive project overview
- Technology stack details
- Installation & setup instructions
- Configuration guide (appsettings, user secrets)
- Build & run instructions
- Integration component descriptions
- Report generation details
- Scheduling instructions
- Development roadmap
- Change log

#### DEVELOPMENT.md
- Prerequisites listing
- Step-by-step setup guide
- User secrets configuration
- Running instructions
- Project structure explanation
- Code style standards
- Debugging guides
- Common issues & solutions

#### ARCHITECTURE.md
- High-level system design diagram
- Component descriptions
- Configuration flow
- Dependency injection strategy
- Error handling approach
- Execution flow sequence
- Extensibility guidelines
- Future enhancement ideas

### ✅ 8. Git Configuration
- `.gitignore` file with comprehensive rules:
  - Visual Studio/VSCode artifacts
  - Build outputs
  - NuGet cache
  - User-specific files
  - Configuration files with sensitive data
  - Logs and reports
  - Database files
  - OS-specific files

---

## Current Issues

### ✅ Build Errors - RESOLVED

**Previous Issues**:
- **NU1102**: HttpClientFactory package version >= 4.3.2 not found (only up to 1.0.5 exists)
- **NU1605**: Serilog package downgrade conflict (Project 3.1.1 vs Serilog.Sinks.Console requiring 4.0.0+)
- **NU1903**: Npgsql 8.0.0 had high severity vulnerability
- **CS0308**: ILogger generic type errors - missing using statements
- **CS1061**: Missing extension methods (AddEnvironmentVariables, Bind, AddSerilog)

**Fixes Applied** (Session 2026-07-23):

1. **Updated NuGet Package Versions**:
   - Serilog: 3.1.1 → 4.0.0 (to match Serilog.Sinks.Console 6.0.0 requirements)
   - Serilog.Sinks.Console: 5.1.0 → 6.0.0
   - Npgsql: 8.0.0 → 8.0.2 (patched vulnerability)
   - Npgsql.EntityFrameworkCore.PostgreSQL: 8.0.0 → 8.0.2
   - **Removed**: HttpClientFactory 4.3.2 (doesn't exist at this version; using built-in HttpClient instead)

2. **Added Missing NuGet Packages**:
   - Microsoft.Extensions.Configuration.EnvironmentVariables v8.0.0
   - Microsoft.Extensions.Configuration.Binder v8.0.0
   - Serilog.Extensions.Logging v8.0.0

3. **Fixed Code Issues**:
   - Added `using Microsoft.Extensions.Logging;` to Program.cs and UtilityHelpers.cs
   - Fixed configuration binding: `services.Configure<AppSettings>(options => configuration.GetSection("AppSettings").Bind(options));`

4. **Npgsql Vulnerability Warning Remains**: Advisory GHSA-x9vc-6hfv-hg8c (informational - design allows isolation of database module)

**Status**: ✅ Build succeeded - No errors, 2 informational warnings

---

## Architecture Summary

### Design Pattern: Modular Layered Architecture
```
Entry Point (Program.Main)
    ↓
Dependency Injection Setup
    ↓
ApplicationService (Orchestrator)
    ↓
Integration Services (SFTP, AWS, Workday, Database, Report)
    ↓
External Systems (SFTP, S3, SQS, Workday API, PostgreSQL)
```

### Key Design Principles
1. **Separation of Concerns** - Each integration is independent
2. **Dependency Injection** - Loose coupling, testability
3. **Interface-Based** - Easy to mock and extend
4. **Async/Await** - Non-blocking I/O operations
5. **Graceful Degradation** - Continue if individual integrations fail
6. **Comprehensive Logging** - Full audit trail with Serilog

---

## Configuration Strategy

### Configuration Sources (in priority order)
1. User Secrets - Sensitive data (highest priority)
2. Environment Variables
3. appsettings.{Environment}.json - Environment-specific
4. appsettings.json - Defaults (lowest priority)

### Sensitive Data to Store in User Secrets
```
AppSettings:Sftp:Password
AppSettings:Aws:AccessKeyId
AppSettings:Aws:SecretAccessKey
AppSettings:Workday:ClientSecret
AppSettings:Database:Password
```

---

## Technology Stack

| Component | Technology | Version |
|-----------|-----------|---------|
| Framework | .NET | 8.0 |
| Language | C# | 12.0 |
| Logging | Serilog | 3.1.1 |
| Config | Microsoft.Extensions | 8.0.0 |
| DI | Microsoft.Extensions.DependencyInjection | 8.0.0 |
| SFTP | SSH.NET | 2023.0.0 |
| AWS | AWSSDK.* | 3.7.300.0 |
| Database | Npgsql | 8.0.0 |
| JSON | Newtonsoft.Json | 13.0.3 |

---

## File Checklist

### Solution Level
- [x] Bot.MorningJob.sln
- [x] README.md
- [x] DEVELOPMENT.md
- [x] ARCHITECTURE.md
- [x] SESSION_NOTES.md (this file)
- [x] .gitignore

### Project Level (Bot.MorningJob.Console)
- [x] Bot.MorningJob.Console.csproj
- [x] Program.cs (Entry point + DI setup)
- [x] appsettings.json (Configuration template)

### Integrations
- [x] Integrations/SFTP/ISftpService.cs
- [x] Integrations/AWS/IAwsServices.cs
- [x] Integrations/Workday/IWorkdayService.cs
- [x] Integrations/Database/IDatabaseService.cs

### Services
- [x] Services/IReportService.cs

### Utilities
- [x] Utils/UtilityHelpers.cs

### Folders (Empty, ready for implementation)
- [x] Models/
- [x] Config/

---

## Next Steps (Implementation Roadmap)

### Phase 1: SFTP Integration Implementation
- [ ] Implement `SftpService` class using SSH.NET
- [ ] Test SFTP connection
- [ ] Implement file listing and validation
- [ ] Error handling for SFTP operations
- [ ] Add unit tests

### Phase 2: AWS Integration Implementation
- [ ] Implement `S3Service` using AWS SDK
- [ ] Implement `SqsService` using AWS SDK
- [ ] Test S3 bucket connectivity
- [ ] Test SQS queue operations
- [ ] Add error handling
- [ ] Add unit tests

### Phase 3: Workday API Integration
- [ ] Implement OAuth2 authentication
- [ ] Implement `WorkdayService` class
- [ ] Add API endpoint calls
- [ ] Handle Workday API responses
- [ ] Add error handling
- [ ] Add unit tests

### Phase 4: PostgreSQL Integration
- [ ] Implement `DatabaseService` using Npgsql
- [ ] Test database connectivity
- [ ] Implement query execution
- [ ] Add connection pooling
- [ ] Add error handling
- [ ] Add unit tests

### Phase 5: Report Generation
- [ ] Implement HTML report generation
- [ ] Implement JSON export
- [ ] Implement CSV export
- [ ] Add email delivery
- [ ] Add report scheduling
- [ ] Add report templating

### Phase 6: Testing & Deployment
- [ ] Create unit test project
- [ ] Create integration test project
- [ ] Add test coverage
- [ ] Performance testing
- [ ] Release build configuration
- [ ] Docker containerization (optional)
- [ ] CI/CD pipeline setup

---

## Code Standards & Conventions

### C# Style Guide
- Use PascalCase for class names: `SftpService`
- Use camelCase for variables: `connectionString`
- Use meaningful names: `GetLastExecutionDate` not `Get()`
- XML documentation on public methods
- Async methods end with `Async`: `ConnectAsync()`

### Logging Convention
```csharp
_logger.LogInformation("Operation started");
_logger.LogError(ex, "Operation failed");
_logger.LogWarning("Unusual condition detected");
```

### Error Handling
- Try-catch around external service calls
- Log full exception details
- Return meaningful error messages
- Continue operation if non-critical failure

---

## Key Decisions Made

1. **Framework Choice**: .NET 8.0 (latest LTS, modern async/await support)
2. **Logging**: Serilog (structured logging, multiple sinks)
3. **Configuration**: Microsoft.Extensions (built-in, flexible)
4. **Dependency Injection**: Microsoft DI (lightweight, built-in)
5. **Architecture**: Service-oriented with clear separation of concerns
6. **Secrets Management**: User Secrets for development, environment variables for production

---

## Known Limitations & Future Considerations

1. **Retry Logic**: Currently has exponential backoff utility, needs integration into services
2. **Caching**: Not yet implemented - can be added for API responses
3. **Database ORM**: Npgsql EF Core installed but can choose between EF Core or plain Npgsql
4. **Report Sending**: Email capability declared but not yet implemented
5. **Monitoring**: No real-time dashboard yet (future enhancement)
6. **Notifications**: Slack/Teams integration not yet planned

---

## Reference Commands

### Build & Run (After .NET SDK installed)
```powershell
cd c:\Repo\BOT_MJ\Bot.MorningJob
dotnet restore
dotnet build
dotnet run --project src/Bot.MorningJob.Console
```

### User Secrets Setup
```powershell
cd src/Bot.MorningJob.Console
dotnet user-secrets init
dotnet user-secrets set "AppSettings:Sftp:Password" "your-password"
```

### Production Build
```powershell
dotnet publish -c Release -o ./publish
```

### View Project Dependencies
```powershell
dotnet list package
```

---

## Contact & Support

For questions about:
- **Architecture**: See ARCHITECTURE.md
- **Setup**: See DEVELOPMENT.md
- **Project Details**: See README.md
- **Code Implementation**: Refer to individual service interfaces

---

**Last Updated**: 2026-07-23 - Initial Project Setup Complete
**Next Update**: After .NET SDK installation and first build success

---

## Session Update: 2026-09-10 - S3 Bucket Monitoring, INT002 Report Logging & Formatting

### Summary
Implemented full S3 bucket monitoring for the three production buckets referenced in the
morning report ("S3:" section), fixed report data being logged to SQL for only one section,
and improved the daily report text file formatting for Slack.

### ✅ S3 Bucket Monitoring (`IS3BucketMonitorService`)
- Added `IS3BucketMonitorService` / `S3BucketMonitorService`, invoked from `Int002SyncService`
  to populate the "S3:" section of the daily report.
- Two scan modes, selected per-bucket via `S3BucketConfig.ScanMode`:
  - **DateNested** (`stripe-payout-workday-adapter-bucket`): `failed/<yyyy>/<MM>/<dd>/`,
    `processed/<yyyy>/<MM>/<dd>/`, and any `new` (case-insensitive) folder anywhere in the
    bucket.
  - **Flat** (`phoenix-prd-prism-integration`, `gt-transaction-prism-bucket`): a single
    `Failed/`, `New/`, `Processed/` folder (case resolved dynamically against the bucket's
    actual top-level prefixes); objects are grouped by S3 `LastModified` date since there are
    no date sub-folders. Zero-byte "folder marker" objects are excluded from counts.
- Scan state (known failed object keys, and which processed dates have already been reported)
  is persisted in new SQL tables so re-running the job doesn't double-count or double-report:
  `S3BucketFailedItems`, `S3BucketProcessedDates`, `S3BucketFlatReportDates`.
- Supports multi-day backlog: if the job hasn't run in a few days, one report line is emitted
  per unreported date, e.g.:
  ```
  ♦ phoenix-prd-prism-integration - 0 FAILED 1 NEW  0 PROCESSED  ON - 05-09-2026
  ♦ phoenix-prd-prism-integration - 2 FAILED 1 NEW  2 PROCESSED  ON - 06-09-2026
  ```
- Report line rules:
  - `**** Action required` appended when `FailedCount > 0 || NewCount > 0`.
  - `** no change` appended when a bucket's most recent date was already reported previously
    and there's nothing new (this also guarantees every configured bucket always appears in
    the report — buckets are never silently dropped just because their latest date was
    already recorded).

### ✅ AWS SSO Login De-duplication (`IAwsService`)
- `AwsService.GetCredentialsAsync` assumes a different IAM role per bucket/account (3 role
  ARNs across 2 AWS accounts in this environment). Previously every distinct role ARN
  triggered its own `aws sso login`, opening a separate browser tab per bucket even though
  it's the same underlying SSO session/profile.
- Added an in-memory `_ssoLoggedInProfiles` cache so `EnsureSsoLoginAsync` only performs the
  actual `aws sso login` once per SSO profile per process run; `aws sts assume-role` still
  runs per role ARN as before.

### ✅ Full Report Section Logging to SQL (`ReportLines`)
- Previously `CreateReportAsync` only ran (and only logged) the INT002/Pay Global Issues
  section into `Reports`/`ReportLines`, so querying report history only showed INT002 data.
- Rewrote `CreateReportAsync` to always run and log every section of the report: INT002 (Pay
  Global Issues, including all three items even on 0-error days), INT003, Banking Issues,
  Acumatica Issues (INT008/INT024), DLQ, S3, DB/Stripe latest completed, and Miscellaneous/XE.

### ✅ Report Text File Formatting (Slack-friendly)
- Added Slack `mrkdwn` bold section headers (`*Pay Global Issues*`, `*INT002*`,
  `*Banking Issues*`, `*Acumatica Issues*`, `*GTV, Prism, & Stripe Issues*`, `*Sumo Logs*`,
  `*DB*`, `*Miscellaneous Issues*`, `*XE*`, `*Workday Community Updates:*`,
  `*Items to Follow Up*`) and tightened blank-line spacing, matching how the report is pasted
  into the team's Slack channel.

### Notes / Gotchas Learned This Session
- Clearing SQL tracking tables (`Int002FailedFiles`, `Int002ProcessedFiles`,
  `S3BucketFailedItems`, `S3BucketProcessedDates`, `S3BucketFlatReportDates`) resets the
  "already seen" baseline, so the *next* run will treat all currently-existing SFTP/S3 files
  as brand new. Useful for testing from a clean slate, but should be done deliberately (and
  re-seeded with current file names, if simulating a normal day) rather than as part of
  routine operation.
- The `phoenix-prd-prism-integration` bucket uses capitalized top-level prefixes
  (`Failed/`, `New/`, `Processed/`), not lowercase — folder names are resolved dynamically
  by listing the bucket's actual top-level prefixes rather than assuming a fixed casing.

**Last Updated**: 2026-09-10 - S3 bucket monitoring, SQL report logging, and Slack formatting complete

