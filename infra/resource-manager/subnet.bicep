targetScope = 'resourceGroup'

@description('Name of the existing VNet in this resource group.')
param existingVnetName string

@description('Name of the new subnet.')
param newSubnetName string

@description('CIDR prefix for the new subnet.')
param newSubnetPrefix string

resource vnet 'Microsoft.Network/virtualNetworks@2025-01-01' existing = {
  name: existingVnetName
}

resource peSubnet 'Microsoft.Network/virtualNetworks/subnets@2025-01-01' = {
  name: newSubnetName
  parent: vnet
  properties: {
    addressPrefix: newSubnetPrefix
    privateEndpointNetworkPolicies: 'Disabled'
  }
}

output subnetId string = peSubnet.id
