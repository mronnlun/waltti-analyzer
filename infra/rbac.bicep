targetScope = 'resourceGroup'

@description('Object ID of the service principal that AI agents use to authenticate to Azure.')
param agentPrincipalId string

@description('Name of the Log Analytics workspace to grant access to.')
param logAnalyticsWorkspaceName string

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2025-07-01' existing = {
  name: logAnalyticsWorkspaceName
}

// Grant Monitoring Reader to the AI agent service principal so it can query logs and metrics.
resource agentMonitoringReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(logAnalytics.id, agentPrincipalId, 'Monitoring Reader')
  scope: logAnalytics
  properties: {
    // Monitoring Reader built-in role
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '43d0d8ad-25c7-4714-9337-8ba259a9fe05')
    principalId: agentPrincipalId
    principalType: 'ServicePrincipal'
  }
}
