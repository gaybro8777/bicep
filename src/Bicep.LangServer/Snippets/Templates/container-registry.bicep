﻿resource containerRegistry1 'Microsoft.ContainerRegistry/registries@2019-05-01' = {
  name: '${1:containerRegistry1}'
  location: resourceGroup().location
  sku: {
    name: '${2|Classic,Basic,Standard,Premium|}'
  }
  properties: {
    adminUserEnabled: '${3|true,false|}'
  }
}