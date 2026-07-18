targetScope = 'resourceGroup'

// Naming: ProjectName-env-resourcetype
// ProjectName = PascalCase, no dashes; env and resourcetype = lowercase

@description('Project name in PascalCase (no dashes)')
param projectName string = 'WalttiAnalyzer'

@description('Environment name (e.g. dev, test, prod)')
param env string = 'prod'

@description('Azure region for resources')
param location string = resourceGroup().location

@description('Digitransit API key')
@secure()
param digitransitApiKey string

@description('Default stop GTFS ID')
param defaultStopId string = 'Vaasa:309392'

@description('SQL admin login name')
param sqlAdminLogin string = 'walttidbadmin'

@description('SQL admin password')
@secure()
param sqlAdminPassword string

@description('Monthly cost budget in the billing currency (e.g. EUR). Matches the currently configured budget — lowering it here overwrites manual changes made in the portal.')
param budgetAmount int = 43

@description('Budget period start date (first of a month). Must stay fixed once the budget exists: Azure rejects any update that changes a budget start date, so deriving this from the deployment date breaks every redeploy in a later month.')
param budgetStartDate string = '2026-04-01'

@description('Email address for budget alert notifications (leave empty to skip budget alerts)')
param notificationEmail string = ''

@description('Object ID of the service principal that AI agents use to authenticate to Azure. When provided, the principal is granted Monitoring Reader on the Log Analytics workspace.')
param agentPrincipalId string = ''

// SQL Server names must be globally unique and lowercase
var sqlServerName = toLower('${projectName}-${env}-sql')

// --- App Service Plan (Basic B1 — required for AlwaysOn) ---
resource appServicePlan 'Microsoft.Web/serverfarms@2025-03-01' = {
  name: '${projectName}-${env}-plan'
  location: location
  kind: 'linux'
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
  properties: {
    reserved: true // Required for Linux
  }
}

// --- Azure SQL Server ---
resource sqlServer 'Microsoft.Sql/servers@2025-02-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    version: '12.0'
    minimalTlsVersion: '1.2'
  }
}

// --- Allow Azure services to access SQL Server ---
resource sqlFirewallAllowAzure 'Microsoft.Sql/servers/firewallRules@2025-02-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// --- Azure SQL Database (Basic tier — 2 GB, ~$5/month) ---
resource sqlDatabase 'Microsoft.Sql/servers/databases@2025-02-01-preview' = {
  parent: sqlServer
  name: '${projectName}-${env}-sqldb'
  location: location
  sku: {
    name: 'Basic'
    tier: 'Basic'
  }
  properties: {
    maxSizeBytes: 2147483648 // 2 GB
  }
}

// --- Log Analytics Workspace (required by Application Insights) ---
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2025-07-01' = {
  name: '${projectName}-${env}-log'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

// --- Monitoring Reader role for AI agent service principal ---
// Grants read access to Log Analytics so that AI agents can query application logs and metrics.
resource agentMonitoringReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(agentPrincipalId)) {
  name: guid(logAnalytics.id, agentPrincipalId, 'Monitoring Reader')
  scope: logAnalytics
  properties: {
    // Monitoring Reader built-in role
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '43d0d8ad-25c7-4714-9337-8ba259a9fe05')
    principalId: agentPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// --- Application Insights ---
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${projectName}-${env}-appi'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// ADO.NET connection string for EF Core SQL Server provider.
// The password is wrapped in single quotes and any embedded single quotes are doubled so that
// special characters (;, &, <, \, etc.) in the password do not break the ADO.NET parser.
var escapedPassword = replace(sqlAdminPassword, '\'', '\'\'')
var sqlConnectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDatabase.name};User Id=${sqlAdminLogin};Password=\'${escapedPassword}\';Encrypt=yes;TrustServerCertificate=no;Connection Timeout=30;'

// --- ASP.NET Core Web App ---
resource webApp 'Microsoft.Web/sites@2025-03-01' = {
  name: '${projectName}-${env}-app'
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    reserved: true
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true // Required to keep BackgroundService running
      http20Enabled: true
      connectionStrings: [
        {
          name: 'DATABASE'
          connectionString: sqlConnectionString
          type: 'SQLAzure'
        }
      ]
      appSettings: [
        // Settings bound to WalttiSettings via "Waltti:" config section prefix
        {
          name: 'Waltti__DigitransitApiKey'
          value: digitransitApiKey
        }
        {
          name: 'Waltti__DefaultStopId'
          value: defaultStopId
        }
        {
          // Used by the in-code Azure Monitor OpenTelemetry distro (UseAzureMonitor).
          // Note: do NOT set ApplicationInsightsAgent_EXTENSION_VERSION here — the
          // codeless App Service agent would duplicate all telemetry exported by OTel.
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
      ]
    }
  }
}

// --- Diagnostic settings: send Web App logs to Log Analytics ---
resource webAppDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: '${projectName}-${env}-app-diag'
  scope: webApp
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      {
        category: 'AppServiceHTTPLogs'
        enabled: true
      }
      {
        category: 'AppServiceConsoleLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

// --- Action group for operational alerts ---
resource alertActionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = if (!empty(notificationEmail)) {
  name: '${projectName}-${env}-ag'
  location: 'Global'
  properties: {
    groupShortName: 'WalttiAlert'
    enabled: true
    emailReceivers: [
      {
        name: 'primaryEmail'
        emailAddress: notificationEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

// --- Alert: no successful realtime poll in 2 hours ---
// The sync loop logs "Sliding window poll result: [status, ok], ..." on success.
// Realtime data is unrecoverable once lost, so silence here means data loss in
// progress. This fires on constant failure, which the Application Insights
// "Failure Anomalies" smart detector does not (steady failure is not an anomaly).
resource pollFailureAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = if (!empty(notificationEmail)) {
  name: '${projectName}-${env}-alert-pollstopped'
  location: location
  properties: {
    displayName: 'Waltti realtime data collection stopped'
    description: 'No successful sliding-window poll was logged in the last 2 hours. Realtime delay data is being lost.'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT30M'
    windowSize: 'PT2H'
    scopes: [
      logAnalytics.id
    ]
    criteria: {
      allOf: [
        {
          query: 'AppTraces | where Message startswith "Sliding window poll result" | where Message contains "[status, ok]"'
          timeAggregation: 'Count'
          operator: 'LessThanOrEqual'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    autoMitigate: true
    actions: {
      actionGroups: [
        alertActionGroup.id
      ]
    }
  }
}

// --- Cost budget (monthly, scoped to resource group) ---
resource budget 'Microsoft.Consumption/budgets@2023-11-01' = if (!empty(notificationEmail)) {
  name: '${projectName}-${env}-budget'
  properties: {
    timePeriod: {
      startDate: budgetStartDate
    }
    timeGrain: 'Monthly'
    amount: budgetAmount
    category: 'Cost'
    notifications: {
      actual80: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 80
        contactEmails: [
          notificationEmail
        ]
      }
      actual100: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        contactEmails: [
          notificationEmail
        ]
      }
    }
  }
}

// --- Outputs ---
output webAppName string = webApp.name
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output resourceGroupName string = resourceGroup().name
output logAnalyticsWorkspaceId string = logAnalytics.properties.customerId
