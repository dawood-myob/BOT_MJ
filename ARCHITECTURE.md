# Architecture Overview

## High-Level Design

Bot.MorningJob is built using a **modular, layered architecture** with clear separation of concerns:

```
┌─────────────────────────────────────────────────────────────┐
│                    Program.Main (Entry Point)               │
├─────────────────────────────────────────────────────────────┤
│                   ApplicationService (Orchestrator)          │
├──────────┬────────────┬──────────┬──────────┬───────────────┤
│  SFTP    │   AWS      │  Workday │Database  │  ReportService│
│  Service │  Services  │  Service │ Service  │               │
├──────────┼────────────┼──────────┼──────────┼───────────────┤
│ External Resources: SFTP, S3, SQS, Workday API, PostgreSQL │
└─────────────────────────────────────────────────────────────┘
```

## Component Descriptions

### 1. Program.cs (Entry Point)
- Configures dependency injection
- Sets up logging with Serilog
- Initializes configuration from multiple sources
- Launches the ApplicationService

### 2. ApplicationService (Orchestrator)
- Main business logic orchestrator
- Coordinates execution of all integration checks
- Handles errors gracefully
- Compiles results for reporting

### 3. Integration Services

#### SFTP Service
- Manages SSH/SFTP connections
- Monitors specified directories
- Validates file presence and metadata
- Returns file information and status

#### AWS Services
- **S3Service**: Lists and validates S3 bucket contents
- **SqsService**: Monitors SQS queue attributes and messages

#### Workday Service
- OAuth2 authentication with Workday
- Queries integration execution status via API
- Retrieves execution timestamps and details

#### Database Service
- PostgreSQL connectivity
- Executes queries for integration logs
- Retrieves execution history and status

### 4. ReportService
- Aggregates results from all services
- Generates comprehensive report
- Exports to multiple formats (HTML, JSON, CSV)
- Sends reports via email

## Configuration Flow

```
Program.Main
    ├─> appsettings.json (default)
    ├─> appsettings.{Environment}.json (environment-specific)
    ├─> Environment Variables
    └─> User Secrets (sensitive data)
```

## Dependency Injection

Services are registered and injected using Microsoft.Extensions.DependencyInjection:

```csharp
services.AddScoped<ISftpService, SftpService>();
services.AddScoped<IS3Service, S3Service>();
services.AddScoped<IWorkdayService, WorkdayService>();
services.AddScoped<IDatabaseService, DatabaseService>();
services.AddScoped<IReportService, ReportService>();
```

## Error Handling Strategy

1. **Graceful Degradation**: If one integration fails, others continue
2. **Detailed Logging**: All errors logged with full context
3. **Result Aggregation**: Failed checks are included in report
4. **Exit Codes**: Non-zero exit code on critical failures

## Execution Flow

```
1. ApplicationService.ExecuteAsync() starts
2. Parallel/Sequential execution of integration checks:
   - Connect to SFTP and check files
   - Connect to AWS S3 and validate objects
   - Check AWS SQS queue status
   - Query Workday API for integration status
   - Query PostgreSQL for execution logs
3. Aggregate results
4. Generate report
5. Export/Send report
6. Log completion and exit
```

## Extensibility

The modular design allows easy addition of new integrations:

1. Create new folder in `Integrations/`
2. Define service interface (e.g., `INewService.cs`)
3. Implement service class
4. Register in `Program.cs`
5. Call from `ApplicationService`

## Future Enhancements

- **Caching Layer**: Cache API responses to reduce redundant calls
- **Retry Logic**: Built-in retry with exponential backoff
- **Event System**: Event-driven notifications on failures
- **Web Dashboard**: Real-time monitoring interface
- **Database Persistence**: Store historical execution data
- **Notification System**: Slack, Teams, Email integrations

---

For detailed component documentation, see individual service files with XML comments.
