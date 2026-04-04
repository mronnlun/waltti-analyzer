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

@description('Target stop GTFS ID')
param targetStopId string = 'Vaasa:309392'

@description('App Service Plan SKU')
@allowed(['F1', 'B1', 'B2', 'S1'])
param skuName string = 'B1'

@description('SQL admin login name')
param sqlAdminLogin string = 'walttidbadmin'

@description('SQL admin password')
@secure()
param sqlAdminPassword string

// SQL Server names must be globally unique and lowercase
var sqlServerName = toLower('${projectName}-${env}-sql')

// --- App Service Plan ---
resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${projectName}-${env}-plan'
  location: location
  kind: 'linux'
  sku: {
    name: skuName
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

// --- App Service ---
resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: '${projectName}-${env}-app'
  location: location
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'PYTHON|3.12'
      appCommandLine: 'gunicorn --config gunicorn.conf.py "app:create_app()"'
      alwaysOn: skuName != 'F1'
      connectionStrings: [
        {
          name: 'DATABASE'
          connectionString: 'Driver={ODBC Driver 18 for SQL Server};Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDatabase.name};Uid=${sqlAdminLogin};Pwd=${sqlAdminPassword};Encrypt=yes;TrustServerCertificate=no;Connection Timeout=30;'
          type: 'SQLAzure'
        }
      ]
      appSettings: [
        {
          name: 'DIGITRANSIT_API_KEY'
          value: digitransitApiKey
        }
        {
          name: 'TARGET_STOP_ID'
          value: targetStopId
        }
        {
          name: 'DATABASE_URL'
          value: 'mssql+pyodbc://${sqlAdminLogin}:${sqlAdminPassword}@${sqlServer.properties.fullyQualifiedDomainName}:1433/${sqlDatabase.name}?driver=ODBC+Driver+18+for+SQL+Server&Encrypt=yes&TrustServerCertificate=no&Connection+Timeout=30'
        }
        {
          name: 'SCM_DO_BUILD_DURING_DEPLOYMENT'
          value: 'true'
        }
        {
          name: 'WEBSITE_STARTUP_FILE'
          value: 'gunicorn --config gunicorn.conf.py "app:create_app()"'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
      ]
    }
  }
}

// --- App Service Logging (console logs to filesystem) ---
resource webAppLogs 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: webApp
  name: 'logs'
  properties: {
    applicationLogs: {
      fileSystem: {
        level: 'Information'
      }
    }
    httpLogs: {
      fileSystem: {
        retentionInMb: 35
        retentionInDays: 3
        enabled: true
      }
    }
  }
}

// --- Outputs ---
output appUrl string = 'https://${webApp.properties.defaultHostName}'
output appName string = webApp.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output resourceGroupName string = resourceGroup().name
