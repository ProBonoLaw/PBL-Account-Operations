targetScope = 'resourceGroup'

param location string = resourceGroup().location
param searchServiceName string
param skuName string = 'basic'
param replicaCount int = 1
param partitionCount int = 1

param existingLaVnetResourceGroupName string
param existingLaVnetName string

param newSubnetName string = '${searchServiceName}-pe-subnet'
param newLaSubnetPrefix string

@description('Object (principal) ID for service principal that can query index content (read-only).')
param indexDataReaderPrincipalIds string[]

@description('Object (principal) ID for service principal that can manage index definitions + read/write index content.')
param indexDataReaderWriterPrincipalIds string[]

@description('Object (principal) ID for service principal that can manage search service and index definitions')
param indexAdminPrincipalIds string[]

@description('Resource group containing the existing Private DNS zone privatelink.search.windows.net that is linked to the client VNet.')
param privateDnsZoneLaResourceGroupName string

@description('Private DNS zone name for Search private endpoints.')
param privateDnsZoneName string = 'privatelink.search.windows.net'

@description('Location of the Data Ingestion Logic Apps VNet and Private Endpoint')
param laLocation string

@allowed([
  'ServicePrincipal'
  'User'
  'Group'
])
param principalType string = 'ServicePrincipal'

module subnetLaMod './subnet.bicep' = {
  name: 'createSearchPeSubnetLa'
  scope: resourceGroup(existingLaVnetResourceGroupName)
  params: {
    existingVnetName: existingLaVnetName
    newSubnetName: newSubnetName
    newSubnetPrefix: newLaSubnetPrefix
  }
}

resource privateDnsZoneLa 'Microsoft.Network/privateDnsZones@2024-06-01' existing = {
  name: privateDnsZoneName
  scope: resourceGroup(privateDnsZoneLaResourceGroupName)
}

resource search 'Microsoft.Search/searchServices@2025-05-01' = {
  name: searchServiceName
  location: location
  sku: { name: skuName }
  properties: {
    replicaCount: replicaCount
    partitionCount: partitionCount
    publicNetworkAccess: 'Disabled'
    disableLocalAuth: true
  }
}

resource peLa 'Microsoft.Network/privateEndpoints@2025-01-01' = {
  name: '${searchServiceName}-pe-la'
  location: laLocation
  tags: {
    Environment: 'Development'
  }
  properties: {
    subnet: {
      id: subnetLaMod.outputs.subnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${searchServiceName}-pe-la/default'
        properties: {
          privateLinkServiceId: search.id
          groupIds: ['searchService']
          privateLinkServiceConnectionState: {
            status: 'Approved'
            description: 'Auto-approved'
            actionsRequired: 'None'
          }
        }
      }
    ]
    customNetworkInterfaceName: '${searchServiceName}-la-nic'
    manualPrivateLinkServiceConnections: []
    ipConfigurations: []
    customDnsConfigs: []
  }
}

resource peZoneGroupLa 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-01-01' = {
  name: 'default-${uniqueString(peLa.id)}'
  parent: peLa
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'privatelink-search-windows-net'
        properties: {
          privateDnsZoneId: privateDnsZoneLa.id
        }
      }
    ]
  }
}

var roleDefSearchServiceContributor = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
)
var roleDefSearchIndexDataContributor = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
)
var roleDefSearchIndexDataReader = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '1407120a-92aa-4202-b7e9-c0e197c71c8f'
)

resource raIndexDataReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in indexDataReaderPrincipalIds: {
  name: guid(search.id, principalId, roleDefSearchIndexDataReader)
  scope: search
  properties: {
    roleDefinitionId: roleDefSearchIndexDataReader
    principalId: principalId
    principalType: principalType
  }
}]

resource raIndexAdminManage 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in indexAdminPrincipalIds: {
  name: guid(search.id, principalId, roleDefSearchServiceContributor)
  scope: search
  properties: {
    roleDefinitionId: roleDefSearchServiceContributor
    principalId: principalId
    principalType: principalType
  }
}]

resource raIndexAdminData 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in indexDataReaderWriterPrincipalIds: {
  name: guid(search.id, principalId, roleDefSearchIndexDataContributor)
  scope: search
  properties: {
    roleDefinitionId: roleDefSearchIndexDataContributor
    principalId: principalId
    principalType: principalType
  }
}]
