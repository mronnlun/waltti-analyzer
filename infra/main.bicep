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

@description('Monthly cost budget in the billing currency (e.g. EUR)')
param budgetAmount int = 30

@description('Current date (injected at deploy time via utcNow) — used to compute the budget start date')
param currentDate string = utcNow('yyyy-MM-dd')

var budgetStartDate = '${substring(currentDate, 0, 7)}-01'

@description('Email address for budget alert notifications (leave empty to skip budget alerts)')
param notificationEmail string = ''

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

// ADO.NET connection string for EF Core SQL Server provider
var sqlConnectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDatabase.name};User Id=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=yes;TrustServerCertificate=no;Connection Timeout=30;'

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
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'ApplicationInsightsAgent_EXTENSION_VERSION'
          value: '~3'
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
