param keyVaultName string
@secure()
@description('Map of secret name -> value.')
param secrets object

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource items 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = [for name in objectKeys(secrets): {
  parent: kv
  name: name
  properties: { value: secrets[name] }
}]
