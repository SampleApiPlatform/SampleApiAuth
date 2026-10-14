// =============================================
// Parameters
// =============================================
@description('Name of the container app')
param containerAppName string = 'sampleauthapi'

param location string = resourceGroup().location

@description('Resource ID of the Container Apps managed environment')
param managedEnvironmentId string

param containerImage string = 'docker.io/popdemar/${containerAppName}:latest'
param managedIdentityId string = '/subscriptions/c13b401c-dde4-4298-ad6c-3ef836f847d5/resourcegroups/sampleApiPlatform_rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/SampleDataAccessApi_UserKey'
param minReplicas int = 1
param maxReplicas int = 5
param targetPort int = 8080
param cpu string = '0.5'
param memory string = '1Gi'

@description('Name of the Key Vault holding the app secrets')
param keyVaultName string = 'sampleapiplatform-kv'

@secure()
@description('URI of the Key Vault passed to the container as a secret (leave empty to derive it from keyVaultName)')
param keyVaultUri string = ''

// =============================================
// Variables
// =============================================
var resolvedKeyVaultUri = keyVaultUri != '' ? keyVaultUri : 'https://${keyVaultName}.${environment().suffixes.keyvaultDns}'

// =============================================
// Resources
// =============================================
resource containerApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: containerAppName
  location: location

  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${managedIdentityId}': {}
    }
  }

  properties: {
    managedEnvironmentId: managedEnvironmentId
    workloadProfileName: 'Consumption'

    configuration: {
      secrets: [
        {
          name: 'keyvaulturi'
          value: resolvedKeyVaultUri
        }
      ]

      activeRevisionsMode: 'Single'

      ingress: {
        external: true
        targetPort: targetPort
        exposedPort: 0
        transport: 'Auto'

        traffic: [
          {
            weight: 100
            latestRevision: true
          }
        ]

        allowInsecure: false
        ipSecurityRestrictions: []
        clientCertificateMode: 'Ignore'

        stickySessions: {
          affinity: 'none'
        }
      }

      registries: []

      dapr: {
        enabled: true
        appId: containerAppName
        appProtocol: 'http'
        appPort: targetPort
        httpReadBufferSize: 65
        httpMaxRequestSize: 4
        logLevel: 'info'
        enableApiLogging: true
      }

      maxInactiveRevisions: 100
    }

    template: {
      containers: [
        {
          name: containerAppName
          image: containerImage

          env: [
            {
              name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
              value: 'true'
            }
            {
              name: 'KeyVaultUri'
              secretRef: 'keyvaulturi'
            }
          ]

          resources: {
            cpu: json(cpu)
            memory: memory
          }

          probes: [
            {
              type: 'Liveness'
              failureThreshold: 3
              initialDelaySeconds: 10
              periodSeconds: 10
              successThreshold: 1
              tcpSocket: {
                port: targetPort
              }
              timeoutSeconds: 5
            }
            {
              type: 'Readiness'
              failureThreshold: 3
              initialDelaySeconds: 5
              periodSeconds: 5
              successThreshold: 1
              tcpSocket: {
                port: targetPort
              }
              timeoutSeconds: 5
            }
          ]
        }
      ]

      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

// =============================================
// Outputs
// =============================================
output fqdn string = containerApp.properties.configuration.ingress.fqdn
output containerAppId string = containerApp.id
