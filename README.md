This system continuously monitors 7 major natural gas pipeline Electronic Bulletin Boards (EBBs) for curtailments, outages, force majeure events, and other notices that create trading opportunities. When relevant signals are detected, it sends immediate Slack notifications and periodic email digests to energy traders.

## Quick Start

### Prerequisites
- .NET 8.0 SDK
- Docker (optional)
- Slack webhook URL (for notifications)
- Email SMTP credentials (for digest emails)

### Option 1: Run with .NET
```bash
# Clone the repository
git clone <your-repo-url>
cd trading-alert-system

# Restore dependencies
dotnet restore

# Configure notifications (see Configuration section)
# Edit appsettings.json with your Slack and email settings

# Run the application
dotnet run --project TradingAlertSystem.Console

### Option 2: Run with Docker
# Build the Docker image
docker build -t trading-alert-system .

# Run with environment variables
docker run -d --name trading-alerts \
  -e Slack__WebhookUrl="https://hooks.slack.com/services/YOUR/SLACK/WEBHOOK" \
  -e Email__SmtpHost="smtp.gmail.com" \
  -e Email__Username="your-email@gmail.com" \
  -e Email__Password="your-app-password" \
  -e Email__FromAddress="your-email@gmail.com" \
  -e Email__DefaultRecipients__0="trader1@company.com" \
  --restart unless-stopped \
  trading-alert-system

# View logs
docker logs -f trading-alerts
```

## Pipeline Coverage

The system monitors these major interstate natural gas pipelines:

| Pipeline | Method | Key Trading Signals |
|----------|--------|-------------------|
| **ANR Pipeline** | HTML Scraping | SE Mainline outages, Henry Hub area |
| **TETCO** | HTML + Detail Pages | Operational Flow Orders, Capacity Constraints |
| **Columbia Gulf** | CSV Export | TIM notices, Louisiana capacity |
| **Gulf South** | REST API | Compressor outages, Carthage Junction |
| **Creole Trail & Corpus Christi** | JSON API | LNG terminal maintenance, export capacity |
| **Sabine Pipeline** | HTML Scraping | Sabine Hub restrictions, capacity limits |
| **NGPL** | HTML + PDF Parsing | Planned outages, capacity restrictions |

## 🔧 Configuration

### Notification Setup

#### Slack Notifications (Immediate)
1. Create a Slack webhook:
   - Go to https://api.slack.com/incoming-webhooks
   - Create a new webhook for your channel
   - Copy the webhook URL

2. Update `appsettings.json`:
```json
{
  "Slack": {
    "WebhookUrl": "https://hooks.slack.com/services/YOUR/SLACK/WEBHOOK"
  }
}
```

#### Email Notifications (Gmail)
1. Set up app-specific password for Gmail:
   - Enable 2FA on your Gmail account
   - Generate an app password
   - Use the app password (not your regular password)

2. Update `appsettings.json`:
```json
{
  "Email": {
    "SmtpHost": "smtp.gmail.com",
    "SmtpPort": 587,
    "EnableSsl": true,
    "Username": "your-email@gmail.com",
    "Password": "your-app-password",
    "FromAddress": "your-email@gmail.com",
    "DefaultRecipients": [
      "trader1@company.com",
      "trader2@company.com"
    ]
  }
}
```

### Detection Rules
Customize trading signal detection in `appsettings.json`:

```json
{
  "Detection": {
    "RecentNoticeDays": 3,
    "MaxNoticeAgeForTradingDays": 30,
    "HighVolumeThresholdMmbtu": 1000,
    "MediumVolumeThresholdMmbtu": 500,
    "CriticalKeywords": [
      "force majeure",
      "emergency", 
      "unplanned",
      "critical"
    ],
    "HenryHubKeywords": [
      "louisiana",
      "henry hub", 
      "gulf coast",
      "la"
    ]
  }
}
```

### Monitoring Settings
```json
{
  "Monitoring": {
    "MonitoringIntervalMinutes": 5,
    "NotificationRetentionHours": 24,
    "EnableConsoleOutput": true,
    "EnableDetailedLogging": true
  }
}
```

## Taking It Further

This proof-of-concept can be taken further, here are some enhancements that would make it significantly better:

### Additional Pipeline Coverage

The current architecture supports 6 provider types that can accommodate most major pipelines:

**HTML Scraping Providers:** Williams Transco, Tennessee Gas Pipeline, Energy Transfer pipelines
**JSON API Providers:** Additional LNG terminals (Freeport, Golden Pass, Calcasieu Pass)
**CSV Export Providers:** Kinder Morgan Interstate systems, El Paso Natural Gas
**PDF Parsing Providers:** Southern Natural Gas planned outage reports

Adding new pipelines requires implementing the appropriate base provider interface - no architectural changes needed.

### Data Persistence

Replace in-memory tracking with database storage:

```csharp
services.AddDbContext<TradingSignalContext>(options =>
    options.UseSqlServer(connectionString));

services.AddScoped<ISignalAnalyticsService, SignalAnalyticsService>();
services.AddScoped<IHistoricalTrendService, HistoricalTrendService>();
```

This enables historical analysis, trend detection, and signal performance tracking.

### Cloud Deployment

**Azure Container Apps:**
```yaml
apiVersion: v1
kind: Deployment
metadata:
  name: trading-alert-system
spec:
  containers:
  - name: trading-alerts
    image: your-registry/trading-alert-system:latest
    env:
    - name: ConnectionStrings__Database
      valueFrom:
        secretKeyRef:
          name: database-secret
```

**Kubernetes with auto-scaling:**
```yaml
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: trading-alerts-hpa
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: trading-alert-system
  minReplicas: 2
  maxReplicas: 10
```

### NLP and AI Enhancement

Add intelligent content analysis for better signal detection:

```csharp
services.AddSingleton<IAzureOpenAIService>();
services.AddScoped<INoticeContentAnalyzer, OpenAINoticeAnalyzer>();

public class OpenAINoticeAnalyzer
{
    public async Task<TradingImpactAssessment> AnalyzeNoticeAsync(PipelineNotice notice)
    {
        // Use GPT-4 to analyze notice text for:
        // - Severity classification beyond keyword matching
        // - Volume impact estimation from unstructured text
        // - Duration predictions for outages
        // - Geographic impact radius assessment
    }
}
```

This would significantly improve signal quality by understanding context and nuance that simple keyword detection misses.