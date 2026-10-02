// Deploys into an existing Container Apps environment. Private registry authentication and
// environment-level egress restrictions belong to the operator's surrounding infrastructure.
param location string = resourceGroup().location
param name string = 'reports'
param environmentId string
@description('An immutable, tested image accessible to this environment, preferably pinned by digest.')
param image string
param keyId string
param callerId string = 'application'
@secure()
@description('Base64 SHA-256 of the complete id.secret credential. The raw client key is never provisioned here.')
param apiKeyHash string

resource reports 'Microsoft.App/containerApps@2025-07-01' = {
  name: name
  location: location
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      secrets: [
        {
          name: 'api-key-hash'
          value: apiKeyHash
        }
      ]
    }
    template: {
      terminationGracePeriodSeconds: 90
      containers: [
        {
          name: 'reports'
          image: image
          resources: {
            cpu: 2
            memory: '4Gi'
          }
          env: [
            { name: 'ReportsServer__Authentication__Mode', value: 'ApiKey' }
            { name: 'ReportsServer__Authentication__ApiKeys__0__Id', value: keyId }
            { name: 'ReportsServer__Authentication__ApiKeys__0__Hash', secretRef: 'api-key-hash' }
            { name: 'ReportsServer__Authentication__ApiKeys__0__CallerId', value: callerId }
            { name: 'ReportsServer__Authentication__ApiKeys__0__Permissions__0', value: 'reports.convert' }
            { name: 'ReportsEngine__Network__Mode', value: 'Disabled' }
            { name: 'ReportsEngine__Concurrency__MaxConcurrentConversions', value: '2' }
            { name: 'ReportsEngine__Concurrency__MaxQueueLength', value: '8' }
            { name: 'ReportsEngine__Browser__ShutdownTimeout', value: '00:01:10' }
          ]
          probes: [
            {
              type: 'Startup'
              httpGet: { path: '/health/live', port: 8080, scheme: 'HTTP' }
              periodSeconds: 10
              timeoutSeconds: 3
              failureThreshold: 10
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080, scheme: 'HTTP' }
              periodSeconds: 5
              timeoutSeconds: 3
              failureThreshold: 3
            }
            {
              type: 'Liveness'
              httpGet: { path: '/health/live', port: 8080, scheme: 'HTTP' }
              periodSeconds: 10
              timeoutSeconds: 5
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        minReplicas: 2
        maxReplicas: 10
        rules: [
          {
            name: 'http'
            http: {
              metadata: { concurrentRequests: '4' }
            }
          }
        ]
      }
    }
  }
}

output endpoint string = 'https://${reports.properties.configuration.ingress.fqdn}'
