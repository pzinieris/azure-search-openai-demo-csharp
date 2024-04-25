param name string
param location string = resourceGroup().location
param tags object = {}

param principalId string = ''

resource keyVault 'Microsoft.KeyVault/vaults@2022-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
  }
}

module webKeyVaultAccess 'keyvault-access.bicep' = if(!empty(principalId)) {
  name: 'principalId-keyvault-access'
  scope: resourceGroup(resourceGroup().name)
  params: {
    principalId: principalId
    keyVaultName: name
  }
}

output endpoint string = keyVault.properties.vaultUri
output name string = keyVault.name
