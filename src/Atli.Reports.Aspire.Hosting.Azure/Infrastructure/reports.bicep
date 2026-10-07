// Mirrors the tested workspaces deployment: renderers have no workload identity or network route
// into the applications' VNet. The service also applies a deny-all sandbox egress policy.
param location string = resourceGroup().location
param name string
param gatewayPrincipalId string
param provisionerPrincipalId string
param userPrincipalId string
param principalType string

var suffix = uniqueString(resourceGroup().id, name)
var sandboxOwnerRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'c24cf47c-5077-412d-a19c-45202126392c')

resource rendererNsg 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: '${name}-renderer-nsg'
  location: location
  properties: {
    securityRules: [{
      name: 'deny-azure-platform-dns'
      properties: {
        priority: 100
        direction: 'Outbound'
        access: 'Deny'
        protocol: '*'
        sourceAddressPrefix: '*'
        sourcePortRange: '*'
        destinationAddressPrefix: 'AzurePlatformDNS'
        destinationPortRange: '*'
      }
    }]
  }
}
resource rendererNetwork 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${name}-renderers'
  location: location
  properties: {
    addressSpace: { addressPrefixes: ['10.42.0.0/16'] }
    dhcpOptions: { dnsServers: ['10.42.0.4'] }
    subnets: [{
      name: 'renderers'
      properties: {
        addressPrefix: '10.42.1.0/24'
        networkSecurityGroup: { id: rendererNsg.id }
        delegations: [{
          name: 'sandbox-delegation'
          properties: { serviceName: 'Microsoft.App/environments' }
        }]
      }
    }]
  }
}
resource egressIp 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
  name: '${name}-egress'
  location: location
  sku: { name: 'Standard' }
  properties: {
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
  }
}
resource nat 'Microsoft.Network/natGateways@2024-05-01' = {
  name: '${name}-nat'
  location: location
  sku: { name: 'Standard' }
  properties: {
    idleTimeoutInMinutes: 4
    publicIpAddresses: [{ id: egressIp.id }]
  }
}
resource applicationNetwork 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${name}-applications'
  location: location
  properties: {
    addressSpace: { addressPrefixes: ['10.60.0.0/16'] }
    subnets: [{
      name: 'applications'
      properties: {
        addressPrefix: '10.60.0.0/27'
        natGateway: { id: nat.id }
        delegations: [{
          name: 'apps-delegation'
          properties: { serviceName: 'Microsoft.App/environments' }
        }]
      }
    }]
  }
}
resource sandboxGroup 'Microsoft.App/sandboxGroups@2026-02-01-preview' = {
  name: '${name}-renderers-${suffix}'
  location: location
  // ARM preflight rejects an omitted properties object, even when using service defaults.
  properties: {}
}
// ARM child resource is equivalent to aca sandboxgroup network create.
resource rendererConnection 'Microsoft.App/sandboxGroups/vnetConnections@2026-02-01-preview' = {
  parent: sandboxGroup
  name: 'renderers'
  location: location
  properties: { subnetId: rendererNetwork.properties.subnets[0].id }
}
resource records 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'reports-${suffix}'
  location: location
  properties: {
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 90
    accessPolicies: []
  }
}
resource gatewayRecordsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(records.id, gatewayPrincipalId, 'secrets-user')
  scope: records
  properties: {
    principalId: gatewayPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}
resource provisionerRecordsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(records.id, provisionerPrincipalId, 'secrets-officer')
  scope: records
  properties: {
    principalId: provisionerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
  }
}
resource provisionerSandboxRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(sandboxGroup.id, provisionerPrincipalId, sandboxOwnerRole)
  scope: sandboxGroup
  properties: {
    principalId: provisionerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: sandboxOwnerRole
  }
}
// Deploy-time disk-image creation uses the deploying identity, never the gateway identity.
resource deployerSandboxRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(sandboxGroup.id, userPrincipalId, sandboxOwnerRole)
  scope: sandboxGroup
  properties: {
    principalId: userPrincipalId
    principalType: principalType
    roleDefinitionId: sandboxOwnerRole
  }
}
output subscriptionId string = subscription().subscriptionId
output resourceGroupName string = resourceGroup().name
output location string = location
output sandboxGroupName string = sandboxGroup.name
output sandboxGroupId string = sandboxGroup.id
output applicationSubnetId string = applicationNetwork.properties.subnets[0].id
output vaultUri string = records.properties.vaultUri
output natAddress string = egressIp.properties.ipAddress
