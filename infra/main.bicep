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

@description('Default stop GTFS ID shown in the dashboard')
param defaultStopId string = 'Vaasa:309392'

@description('App Service Plan SKU')
@allowed(['F1', 'B1', 'B2', 'S1'])
param skuName string = 'B1'

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
      appCommandLine: 'antenv/bin/gunicorn --config gunicorn.conf.py "app:create_app()"'
      alwaysOn: skuName != 'F1'
      appSettings: [
        {
          name: 'DIGITRANSIT_API_KEY'
          value: digitransitApiKey
        }
        {
          name: 'DEFAULT_STOP_ID'
          value: defaultStopId
        }
        {
          name: 'SCM_DO_BUILD_DURING_DEPLOYMENT'
          value: 'false'
        }
        {
          name: 'DISABLE_ORYX_BUILD'
          value: 'true'
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
output resourceGroupName string = resourceGroup().name
