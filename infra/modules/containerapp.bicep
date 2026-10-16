param name string
param location string
param tags object
param environmentId string
param identityId string
param image string
param registryServer string
param external bool = true
param ingressEnabled bool = true
param targetPort int = 8080
param minReplicas int = 1
param maxReplicas int = 3
param stickySessions bool = false
param cpu string = '0.5'
param memory string = '1Gi'
@description('Container Apps secrets: [{ name, keyVaultUrl, identity }]')
param secrets array = []
@description('Environment variables: [{ name, value }] or [{ name, secretRef }]')
param env array = []

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    managedEnvironmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: ingressEnabled ? {
        external: external
        targetPort: targetPort
        transport: 'auto'            // HTTP/1.1 + WebSocket (SignalR) + HTTP/2
        allowInsecure: false
        stickySessions: { affinity: stickySessions ? 'sticky' : 'none' }
      } : null
      registries: [
        { server: registryServer, identity: identityId }
      ]
      secrets: secrets
    }
    template: {
      containers: [
        {
          name: 'app'
          image: image
          resources: { cpu: json(cpu), memory: memory }
          env: env
          probes: ingressEnabled ? [
            { type: 'Liveness', httpGet: { path: '/healthz', port: targetPort }, initialDelaySeconds: 10, periodSeconds: 30 }
            { type: 'Readiness', httpGet: { path: '/healthz', port: targetPort }, initialDelaySeconds: 5, periodSeconds: 10 }
          ] : []
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: ingressEnabled ? [
          { name: 'http', http: { metadata: { concurrentRequests: '200' } } }
        ] : []
      }
    }
  }
}

output fqdn string = ingressEnabled ? app.properties.configuration.ingress.fqdn : ''
output name string = app.name
