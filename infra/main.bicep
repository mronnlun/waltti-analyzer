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

// SQL Server names must be globally unique and lowercase
var sqlServerName = toLower('${projectName}-${env}-sql')
var storageName = toLower(replace('${projectName}${env}st', '-', ''))

// --- Storage Account (required for Function App) ---
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
  }
}

// --- Consumption App Service Plan (serverless) ---
resource functionPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${projectName}-${env}-plan'
  location: location
  kind: 'functionapp'
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  properties: {
    reserved: true // Required for Linux
  }
}

// --- Azure SQL Server ---
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
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
resource sqlFirewallAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// --- Azure SQL Database (Basic tier — 2 GB, ~$5/month) ---
resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
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

// --- SQL Database Automatic Tuning ---
resource sqlAutoTuning 'Microsoft.Sql/servers/databases/advisors@2014-04-01' = {
  parent: sqlDatabase
  name: 'ForceLastGoodPlan'
  properties: {
    autoExecuteValue: 'Enabled'
  }
}

resource sqlAutoTuningCreateIndex 'Microsoft.Sql/servers/databases/advisors@2014-04-01' = {
  parent: sqlDatabase
  name: 'CreateIndex'
  properties: {
    autoExecuteValue: 'Enabled'
  }
}

resource sqlAutoTuningDropIndex 'Microsoft.Sql/servers/databases/advisors@2014-04-01' = {
  parent: sqlDatabase
  name: 'DropIndex'
  properties: {
    autoExecuteValue: 'Enabled'
  }
}

// --- Log Analytics Workspace (required by Application Insights) ---
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
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

// --- Function App (Consumption plan) ---
resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: '${projectName}-${env}-func'
  location: location
  kind: 'functionapp,linux'
  properties: {
    serverFarmId: functionPlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      connectionStrings: [
        {
          name: 'DATABASE'
          connectionString: 'Driver={ODBC Driver 18 for SQL Server};Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDatabase.name};Uid=${sqlAdminLogin};Pwd=${sqlAdminPassword};Encrypt=yes;TrustServerCertificate=no;Connection Timeout=30;'
          type: 'SQLAzure'
        }
      ]
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${az.environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}'
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'DIGITRANSIT_API_KEY'
          value: digitransitApiKey
        }
        {
          name: 'DEFAULT_STOP_ID'
          value: defaultStopId
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'SCM_DO_BUILD_DURING_DEPLOYMENT'
          value: 'true'
        }
      ]
    }
  }
}

// --- Static Web App for SPA frontend ---
resource staticWebApp 'Microsoft.Web/staticSites@2023-12-01' = {
  name: '${projectName}-${env}-swa'
  location: location
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    buildProperties: {
      skipGithubActionWorkflowGeneration: true
    }
  }
}

// --- Outputs ---
output functionAppName string = functionApp.name
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'
output staticWebAppName string = staticWebApp.name
output staticWebAppUrl string = 'https://${staticWebApp.properties.defaultHostname}'
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output resourceGroupName string = resourceGroup().name
