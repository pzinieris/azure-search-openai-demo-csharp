param name string
param location string = resourceGroup().location
param tags object = {}
@description('The custom subdomain name used to access the API. Defaults to the value of the name parameter.')
param customSubDomainName string = name

param useManagedIdentity bool

@description('The name of the identity')
param identityName string = ''

param deployments array = []
param kind string = 'OpenAI'

@allowed([ 'Enabled', 'Disabled' ])
param publicNetworkAccess string = 'Enabled'

param skuName string = 'S0'

param allowedIpRules array = []
param networkAcls object = empty(allowedIpRules) ? {
    defaultAction: 'Allow'
  } : {
    ipRules: allowedIpRules
    defaultAction: 'Deny'
  }

var useUserAssignedIdentity = useManagedIdentity && !empty(identityName) && identityName != ''

resource cognitiveServicesUserAssignedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (useUserAssignedIdentity) {
  name: useUserAssignedIdentity ? identityName : 'ThisIdWillNotAppear'
  location: location
}

resource account 'Microsoft.CognitiveServices/accounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  kind: kind
  identity: useManagedIdentity 
    ? (useUserAssignedIdentity 
	  ? {
        type: 'UserAssigned'
	    userAssignedIdentities: { '${cognitiveServicesUserAssignedIdentity.id}': {} }
      } : {
        type: 'SystemAssigned'
      })
	: null
  properties: {
    customSubDomainName: customSubDomainName
    publicNetworkAccess: publicNetworkAccess
    networkAcls: networkAcls
  }
  sku: {
    name: skuName
  }
}

@batchSize(1)
resource deployment 'Microsoft.CognitiveServices/accounts/deployments@2023-05-01' = [for deployment in deployments: {
  parent: account
  name: deployment.name
  properties: {
    model: deployment.model
    raiPolicyName: contains(deployment, 'raiPolicyName') ? deployment.raiPolicyName : null
  }
  sku: contains(deployment, 'sku') ? deployment.sku : {
    name: 'Standard'
    capacity: 20
  }
}]

output endpoint string = account.properties.endpoint
output id string = account.id
output name string = account.name
output SERVICE_COGNITIVE_IDENTITY_NAME string = identityName
output SERVICE_COGNITIVE_IDENTITY_PRINCIPAL_ID string = useManagedIdentity ? (useUserAssignedIdentity ? cognitiveServicesUserAssignedIdentity.properties.principalId : account.identity.principalId) : ''