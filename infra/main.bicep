targetScope = 'resourceGroup'

@description('Base name for all resources')
param appName string = 'walttianalyzer'

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

// --- App Service Plan ---
resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${appName}-plan'
  location: location
  kind: 'linux'
  sku: {
    name: skuName
  }
  properties: {
    reserved: true // Required for Linux
  }
}

// --- App Service ---
resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: appName
  location: location
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'PYTHON|3.12'
      appCommandLine: 'gunicorn --config gunicorn.conf.py "app:create_app()"'
      alwaysOn: skuName != 'F1' // Always On not available on Free tier
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
          name: 'DATABASE_PATH'
          value: '/home/data/waltti.db'
        }
        {
          name: 'SCM_DO_BUILD_DURING_DEPLOYMENT'
          value: 'true'
        }
        {
          name: 'WEBSITE_STARTUP_FILE'
          value: 'gunicorn --config gunicorn.conf.py "app:create_app()"'
        }
      ]
    }
  }
}

// --- Storage Account (for SQLite persistence via Azure Files) ---
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: replace('${appName}stor', '-', '')
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
  }
}

resource fileService 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource fileShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: fileService
  name: 'waltti-data'
  properties: {
    shareQuota: 1 // 1 GB — plenty for SQLite
  }
}

// --- Mount Azure Files to App Service ---
resource webAppStorageMount 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: webApp
  name: 'azurestorageaccounts'
  properties: {
    walttidata: {
      type: 'AzureFiles'
      accountName: storageAccount.name
      shareName: fileShare.name
      mountPath: '/home/data'
      accessKey: storageAccount.listKeys().keys[0].value
    }
  }
}

// --- Outputs ---
output appUrl string = 'https://${webApp.properties.defaultHostName}'
output appName string = webApp.name
output resourceGroupName string = resourceGroup().name
