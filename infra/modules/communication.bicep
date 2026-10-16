param name string
param tags object
param dataLocation string = 'United States'

// Azure-managed sender domain: no DNS setup, addresses look like DoNotReply@<guid>.azurecomm.net.
// Swap for a custom domain resource once DNS records can be added.
resource email 'Microsoft.Communication/emailServices@2023-04-01' = {
  name: 'email-${name}'
  location: 'global'
  tags: tags
  properties: { dataLocation: dataLocation }
}

resource domain 'Microsoft.Communication/emailServices/domains@2023-04-01' = {
  parent: email
  name: 'AzureManagedDomain'
  location: 'global'
  tags: tags
  properties: {
    domainManagement: 'AzureManaged'
    userEngagementTracking: 'Disabled'
  }
}

resource acs 'Microsoft.Communication/communicationServices@2023-04-01' = {
  name: 'acs-${name}'
  location: 'global'
  tags: tags
  properties: {
    dataLocation: dataLocation
    linkedDomains: [ domain.id ]
  }
}

#disable-next-line outputs-should-not-contain-secrets // written straight into Key Vault by the parent template
output connectionString string = acs.listKeys().primaryConnectionString
output mailFromDomain string = domain.properties.mailFromSenderDomain
