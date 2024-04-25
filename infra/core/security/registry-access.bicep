param containerRegistryName string
param principalId string

var abbrs = loadJsonContent('../../abbreviations.json')

var acrPullRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '${abbrs.roleAcrPull')

resource aksAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: containerRegistry // Use when specifying a scope that is different than the deployment scope
  name: guid(subscription().id, resourceGroup().id, principalId, acrPullRole)
  properties: {
    roleDefinitionId: acrPullRole
    principalType: 'ServicePrincipal'
    principalId: principalId
  }
}

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2022-02-01-preview' existing = {
  name: containerRegistryName
}
