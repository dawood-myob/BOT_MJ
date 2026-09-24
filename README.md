# Bot MorningJob - Automation Console Application

## Overview

**Bot MorningJob** (or **Bot.MJ**) is a comprehensive .NET Core console application designed to automate morning job workflows and generate integration execution reports. The application integrates with multiple enterprise systems to validate and monitor data flow across the organization.

## Purpose

This automation bot performs the following tasks each morning:

1. **SFTP Integration** - Monitors and checks files in SFTP folders
2. **AWS S3 Integration** - Validates files in AWS S3 buckets
3. **AWS SQS Integration** - Checks the status of message queues
4. **Workday API Integration** - Monitors the status of last executed integrations
5. **PostgreSQL Database** - Retrieves and aggregates execution logs
6. **Report Generation** - Compiles all results into a comprehensive report

## Dependencies & Minimum Requirements

To build and run this application, the following are required:

### Runtime
- **.NET 8 SDK/Runtime** (`net8.0`)
- **Windows** recommended — some helper behavior (e.g. auto-opening the report in Notepad++) is Windows-specific;
  the rest of the app is cross-platform .NET.

### External services (all required for a full successful run)

| Dependency | Purpose | Config section |
|---|---|---|
| **SFTP server** (`mft.myob.com`) | Source of INT002 processed/failed files; requires a private key (`.ppk`) | `AppSettings:Sftp` |
| **SQL Server** | Stores job run state, processed file records, S3 scan state, and Workday integration log history | `AppSettings:Int002Sync:SqlConnectionString` |
| **AWS** (S3, SQS, STS) | Monitors integration buckets (failed/new/processed counts) and payout queues; may require an assumable cross-account role | `AppSettings:Aws` |
| **Workday API** | Fetches integration run status/logs via OAuth2 + custom report API | `AppSettings:Workday` |
| **PostgreSQL** (via local pgweb proxy) | Reads latest Stripe payout event date | `AppSettings:Database` |
| **Notepad++** (optional) | Auto-opens the generated report after each run; skipped gracefully if missing | `AppSettings:Int002Sync:NotepadPlusPlusPath` |

### NuGet packages
See `src/Bot.MorningJob.Console/Bot.MorningJob.Console.csproj` for exact versions:
`SSH.NET`, `AWSSDK.S3`/`SQS`/`Core`, `Npgsql`, `Npgsql.EntityFrameworkCore.PostgreSQL`,
`Microsoft.Data.SqlClient`, `Serilog` (+ Console/File sinks), `Microsoft.Extensions.Configuration.*` /
`DependencyInjection` / `Options`, `Newtonsoft.Json`.

### Minimum setup to run on a new machine
1. Install the .NET 8 SDK.
2. `dotnet restore`.
3. Copy `src/Bot.MorningJob.Console/appsettings.template.json` to `appsettings.json` (git-ignored) and fill in
   real values for SFTP, AWS, Workday, and `Int002Sync:SqlConnectionString`.
4. Place the SFTP private key (`.ppk`) at the path referenced by `Sftp:PrivateKeyPath` /
   `PrivateKeyWorkingCopyPath` — these key files are git-ignored and must be transferred securely, not via git.
5. Ensure the target SQL Server instance is reachable and has the expected tables.
6. (Optional) Start the Postgres proxy: `myob-auth kube db-proxy -d stripe-db -n billing-platform`.
7. `dotnet run --project src/Bot.MorningJob.Console`.

> **Never commit** `appsettings.json`, `appsettings.Development.json`, or `*.ppk` files — they are excluded via
> `.gitignore`. Only `appsettings.template.json` (with blank placeholders) is tracked in source control.

## Project Structure

```
Bot.MorningJob/
├── Bot.MorningJob.sln              # Visual Studio Solution file
├── src/
│   └── Bot.MorningJob.Console/     # Main console application
│       ├── Integrations/            # Integration implementations
│       │   ├── SFTP/               # SFTP integration service
│       │   ├── AWS/                # AWS (S3, SQS) integration services
│       │   ├── Workday/            # Workday API integration service
│       │   └── Database/           # PostgreSQL integration service
│       ├── Services/               # Business logic services
│       ├── Models/                 # Data models and DTOs
│       ├── Config/                 # Configuration classes
│       ├── Utils/                  # Utility classes and helpers
│       ├── Program.cs              # Application entry point
│       ├── appsettings.json        # Configuration file
│       ├── appsettings.Development.json  # Development overrides
│       └── Bot.MorningJob.Console.csproj # Project file
├── .gitignore                      # Git ignore rules
└── README.md                       # This file
```

## Technology Stack

- **Framework**: .NET 8.0
- **Language**: C# 12.0
- **Logging**: Serilog
- **Configuration**: Microsoft.Extensions.Configuration
- **Dependency Injection**: Microsoft.Extensions.DependencyInjection
- **SFTP**: SSH.NET
- **AWS SDK**: AWS SDK for .NET (S3, SQS)
- **Database**: Npgsql (PostgreSQL)
- **JSON Serialization**: Newtonsoft.Json

## Prerequisites

- .NET 8.0 SDK or later
- Visual Studio 2022 or VS Code
- PostgreSQL client (for database operations)
- AWS account credentials (for AWS S3/SQS access)
- Workday tenant access with API credentials
- SFTP server credentials

## Installation & Setup

### 1. Clone the Repository
```bash
git clone <repository-url>
cd Bot.MorningJob
```

### 2. Restore NuGet Packages
```bash
dotnet restore
```

### 3. Configure Application Settings

Edit `appsettings.json` and `appsettings.Development.json` with your credentials:

```json
{
  "AppSettings": {
    "Sftp": {
      "Host": "your-sftp-host",
      "Port": 22,
      "Username": "your-username",
      "PrivateKeyPath": "/path/to/private/key"
    },
    "Aws": {
      "Region": "us-east-1",
      "S3": { "BucketName": "your-bucket" },
      "Sqs": { "QueueUrl": "your-queue-url" }
    },
    "Workday": {
      "TenantName": "your-tenant",
      "ClientId": "your-client-id"
    },
    "Database": {
      "Host": "your-postgres-host",
      "Database": "your-database",
      "Username": "postgres"
    }
  }
}
```

### 4. Use User Secrets for Sensitive Data
```bash
dotnet user-secrets init
dotnet user-secrets set "AppSettings:Sftp:Password" "your-password"
dotnet user-secrets set "AppSettings:Aws:SecretAccessKey" "your-secret-key"
dotnet user-secrets set "AppSettings:Workday:ClientSecret" "your-secret"
dotnet user-secrets set "AppSettings:Database:Password" "your-password"
```

## Building

### Debug Build
```bash
dotnet build
```

### Release Build
```bash
dotnet build -c Release
```

## Running the Application

### Development
```bash
dotnet run --project src/Bot.MorningJob.Console
```

### Production
```bash
dotnet publish -c Release -o ./publish
./publish/Bot.MorningJob.Console.exe
```

### With Environment Variables
```bash
set ASPNETCORE_ENVIRONMENT=Production
dotnet run --project src/Bot.MorningJob.Console
```

## Configuration Files

### appsettings.json
Contains default configuration for all integrations and services. This file should be committed to version control with non-sensitive defaults.

### appsettings.Development.json
Development-specific configuration overrides. Can be excluded from version control using `.gitignore`.

### User Secrets
Sensitive information (passwords, API keys, secrets) should be stored using .NET User Secrets during development:
```bash
dotnet user-secrets set "key" "value"
```

## Integration Components

### SFTP Integration (`Integrations/SFTP/`)
- Connects to SFTP server using SSH.NET
- Monitors specified directories for files
- Validates file presence and modification times
- Reports on file processing status

### AWS S3 Integration (`Integrations/AWS/`)
- Lists and validates S3 bucket contents
- Checks for expected files in monitored prefixes
- Reports on object metadata and timestamps

### AWS SQS Integration (`Integrations/AWS/`)
- Retrieves messages from SQS queue
- Analyzes message count and age
- Reports on queue health and processing status

### Workday API Integration (`Integrations/Workday/`)
- Authenticates with Workday OAuth2
- Queries integration execution status
- Retrieves last run timestamps and status
- Reports on integration health

### PostgreSQL Integration (`Integrations/Database/`)
- Connects to PostgreSQL database
- Executes queries to retrieve execution logs
- Aggregates last execution dates and statuses
- Supports custom queries for different scenarios

## Report Generation

The application generates comprehensive reports containing:
- SFTP file validation results
- S3 bucket file status
- SQS queue metrics
- Workday integration execution status
- Database query results
- Summary of all morning jobs

Reports can be generated in:
- HTML format (default)
- JSON format
- CSV format (TBD)

## Logging

Application logging is handled by Serilog and configured in `Program.cs`:
- Console output for real-time monitoring
- File-based rolling logs in the `logs/` directory
- Configurable log levels per environment

Log files are rotated daily and stored with timestamp suffixes:
```
logs/bot-morningjob-20231215.txt
logs/bot-morningjob-20231216.txt
```

## Error Handling

The application implements comprehensive error handling:
- Try-catch blocks around each integration
- Graceful degradation if specific integrations fail
- Detailed error logging with stack traces
- Application continues if individual jobs fail
- Exit code 1 on critical failures

## Scheduling

To run this application on a schedule:

### Windows Task Scheduler
1. Create a new task in Task Scheduler
2. Set trigger for desired time (e.g., 6:00 AM daily)
3. Set action to execute PowerShell running `Run-MorningJob.ps1` (see below), **not** the
   `.exe` directly, so the Stripe database pgweb proxy is started/stopped automatically:
   ```
   powershell.exe -ExecutionPolicy Bypass -File "C:\path\to\Bot.MorningJob.Console\Run-MorningJob.ps1"
   ```
4. Configure working directory as needed

#### Run-MorningJob.ps1

`Run-MorningJob.ps1` wraps the full daily run so no manual database access is required:
1. Authenticates via `myob-auth kube-login`.
2. Starts `myob-auth kube db-proxy` in the background (forwards to a local pgweb API).
3. Waits for the proxy to become reachable.
4. Runs `Bot.MorningJob.Console.exe`.
5. Stops the proxy process afterward (even on failure).

If the proxy fails to start/authenticate (e.g., expired credentials), the job still completes;
the report's "Stripe Latest completed" line will show `Unavailable` instead of failing the run.

### Linux Cron
```bash
0 6 * * * /opt/bot-morningjob/Bot.MorningJob.Console
```

## Development Roadmap

- [ ] Implement SFTP integration service
- [ ] Implement AWS S3 integration service
- [ ] Implement AWS SQS integration service
- [ ] Implement Workday API integration service
- [ ] Implement PostgreSQL integration service
- [ ] Develop report generation engine
- [ ] Add unit tests
- [ ] Add integration tests
- [ ] Implement retry logic
- [ ] Add Slack/Email notifications
- [ ] Create web dashboard for monitoring

## Contributing

1. Create a feature branch (`git checkout -b feature/your-feature`)
2. Make your changes and commit them (`git commit -m 'Add feature'`)
3. Push to the branch (`git push origin feature/your-feature`)
4. Create a Pull Request

## License

[Add your license here]

## Support

For issues, questions, or suggestions, please contact the development team.

## Change Log

### Version 0.1.0 (Initial Setup)
- Created project structure
- Configured solution and console application
- Added configuration framework
- Integrated Serilog logging
- Created placeholder for all integration services
- Added comprehensive README documentation

---

**Last Updated**: 2026-07-23
