param(
    [string]$GatewayUrl = 'http://127.0.0.1:5080',
    [string]$MonolithUrl = 'http://127.0.0.1:5081'
)
$ErrorActionPreference = 'Stop'
$gatewayHealth = Invoke-WebRequest -Uri "$GatewayUrl/health"
$monolithHealth = Invoke-WebRequest -Uri "$MonolithUrl/health"
$products = Invoke-WebRequest -Uri "$GatewayUrl/api/products"
Write-Output "Gateway health: $($gatewayHealth.StatusCode)"
Write-Output "Monolith health: $($monolithHealth.StatusCode)"
Write-Output "Catalog through gateway: $($products.StatusCode)"
