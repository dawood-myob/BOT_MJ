# Development Setup Guide

## Prerequisites
- .NET 8.0 SDK or later
- Visual Studio 2022, VS Code, or Rider
- Git
- PostgreSQL client (optional for development)
- SSH/SFTP client (optional for testing SFTP)

## Getting Started

### 1. Clone the Repository
```bash
git clone <repository-url>
cd Bot.MorningJob
```

### 2. Restore Dependencies
```bash
dotnet restore
```

### 3. Build the Solution
```bash
dotnet build
```

### 4. First Run Configuration
Create a development configuration file at:
```
src/Bot.MorningJob.Console/appsettings.Development.json
```

Add your local settings (passwords will be managed with user-secrets):
```json
{
  "AppSettings": {
    "Sftp": {
      "Host": "localhost",
      "Port": 22
    },
    "Aws": {
      "Region": "us-east-1"
    },
    "Database": {
      "Host": "localhost",
      "Port": 5432
    }
  }
}
```

### 5. Configure User Secrets
```bash
cd src/Bot.MorningJob.Console
dotnet user-secrets init
dotnet user-secrets set "AppSettings:Sftp:Password" "your-password"
dotnet user-secrets set "AppSettings:Aws:AccessKeyId" "your-access-key"
dotnet user-secrets set "AppSettings:Aws:SecretAccessKey" "your-secret-key"
dotnet user-secrets set "AppSettings:Workday:ClientSecret" "your-secret"
dotnet user-secrets set "AppSettings:Database:Password" "your-password"
```

## Running the Application

### Development Mode
```bash
dotnet run --project src/Bot.MorningJob.Console
```

### With Specific Environment
```bash
set ASPNETCORE_ENVIRONMENT=Development
dotnet run --project src/Bot.MorningJob.Console
```

## Project Structure

### Integrations Folder
Each integration has its own subfolder:
- **SFTP/**: SFTP connection and file monitoring logic
- **AWS/**: S3 and SQS integration code
- **Workday/**: Workday API authentication and queries
- **Database/**: PostgreSQL connections and queries

### Services Folder
Business logic and orchestration services:
- **IReportService.cs**: Report generation and export

### Models Folder
Data transfer objects and domain models (to be created as needed)

### Config Folder
Configuration classes and constants (to be created as needed)

### Utils Folder
Helper utilities and extensions:
- **UtilityHelpers.cs**: Common utility methods

## Building for Production

```bash
dotnet publish -c Release -o ./publish
```

Output will be in the `publish` folder.

## Testing

As we add tests, they will be structured in a separate test project:
```
tests/
  Bot.MorningJob.Tests/
  Bot.MorningJob.Integration.Tests/
```

Run tests with:
```bash
dotnet test
```

## Code Style & Standards

- Use C# conventions and naming standards
- Follow Microsoft C# coding guidelines
- Use meaningful variable and method names
- Add XML documentation comments to public methods
- Use async/await for all I/O operations

## Debugging

### Visual Studio
1. Open `Bot.MorningJob.sln` in Visual Studio
2. Set breakpoints as needed
3. Press F5 to run with debugger

### VS Code
1. Install C# extension
2. Add launch configuration
3. Press F5 to debug

### VS Debugger in Console
```bash
dotnet run --project src/Bot.MorningJob.Console --debug
```

## Logging

Logs are written to:
- **Console**: Real-time output during development
- **File**: `logs/bot-morningjob-YYYYMMDD.txt` (daily rotation)

Adjust log levels in `Program.cs` for debugging.

## Common Issues

### Connection Timeouts
- Verify credentials in user-secrets
- Check firewall rules
- Ensure services are accessible from your network

### Missing Configuration
- Run `dotnet user-secrets list` to verify secrets are set
- Check appsettings.json for required settings

### Build Errors
- Run `dotnet clean` and then `dotnet restore`
- Ensure correct .NET version: `dotnet --version`

## Next Steps

As we develop each integration:
1. Implement service classes in their respective folders
2. Register services in `Program.cs` ConfigureServices
3. Add integration tests
4. Update documentation

---

For issues or questions, refer to the main README.md or contact the development team.
