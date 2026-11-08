param(
    [string]$InventoryConnection = $env:ConnectionStrings__Inventory,
    [string]$CatalogConnection = $env:ConnectionStrings__Catalog,
    [string]$LegacyConnection = $env:ConnectionStrings__LegacyCommerce
)
$ErrorActionPreference = 'Stop'
if (-not $InventoryConnection -or -not $CatalogConnection -or -not $LegacyConnection) { throw 'Inventory, Catalog, and Legacy connection strings are required.' }
$env:ConnectionStrings__Inventory = $InventoryConnection
$env:ConnectionStrings__Catalog = $CatalogConnection
$env:ConnectionStrings__LegacyCommerce = $LegacyConnection
$env:RabbitMq__UseInMemory = 'true'
$env:Catalog__WritesEnabled = 'true'
$env:InventoryOwnership__Mode = 'Extracted'
$env:InventoryService__BaseUrl = 'http://127.0.0.1:5083/'
$env:CatalogOwnership__Mode = 'Extracted'
$env:CatalogService__BaseUrl = 'http://127.0.0.1:5082/'
foreach ($route in @('inventory','inventoryStock','reservationCreate','reservationById','reservationRelease','reservationConfirm')) { [Environment]::SetEnvironmentVariable("ReverseProxy__Routes__${route}__ClusterId", 'inventory', 'Process') }
$processes = @()
New-Item -ItemType Directory -Force -Path 'logs/phase05' | Out-Null
function Wait-Health([string]$url) {
    for ($attempt = 1; $attempt -le 30; $attempt++) {
        try { $response = Invoke-WebRequest -Uri $url -TimeoutSec 2 -SkipHttpErrorCheck; if ($response.StatusCode -eq 200) { return } } catch { }
        Start-Sleep -Milliseconds 500
    }
    throw "Health check did not become ready: $url"
}
function Send-Json([string]$url, [string]$method, $body, [int]$expected, [hashtable]$headers = @{}) {
    try { $response = Invoke-WebRequest -Uri $url -Method $method -ContentType 'application/json' -Body ($body | ConvertTo-Json) -Headers $headers -SkipHttpErrorCheck -TimeoutSec 30 } catch { throw ("{0} {1} failed: {2}" -f $method, $url, $_.Exception.Message) }
    if ($response.StatusCode -ne $expected) { throw "$method $url returned $($response.StatusCode): $($response.Content)" }
    return $response.Content | ConvertFrom-Json
}
try {
    foreach ($service in @(
        @('src/Services/Inventory/Inventory.Api',5083),
        @('src/Services/Catalog/Catalog.Api',5082),
        @('src/LegacyMonolith/LegacyCommerce.Api',5081),
        @('src/Gateway/StranglerForge.Gateway',5080))) {
        $process = Start-Process dotnet -ArgumentList @('run','--no-build','--project',$service[0],'--urls',"http://127.0.0.1:$($service[1])") -PassThru -WindowStyle Hidden -RedirectStandardOutput ("logs/phase05/{0}.out.log" -f $service[1]) -RedirectStandardError ("logs/phase05/{0}.err.log" -f $service[1])
        $processes += $process
        Wait-Health "http://127.0.0.1:$($service[1])/health"
    }
    $base = 'http://127.0.0.1:5080'
    $product = '00000000-0000-0000-0000-000000000201'
    $customer = '00000000-0000-0000-0000-000000000101'
    $customerReady = $false
    $lastCustomerFailure = "no response"
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try { $customerResponse = Invoke-WebRequest -Uri "$base/api/customers/$customer" -SkipHttpErrorCheck -TimeoutSec 3; if ($customerResponse.StatusCode -eq 200) { $customerReady = $true; break }; $lastCustomerFailure = "HTTP $($customerResponse.StatusCode): $($customerResponse.Content)" } catch { $lastCustomerFailure = $_.Exception.Message }
        Start-Sleep -Seconds 1
    }
    if (-not $customerReady) { throw "Customer readiness failed after 20 attempts: $lastCustomerFailure" }
    $catalogProduct = Invoke-RestMethod "$base/api/products/$product"
    if ($catalogProduct.id -ne $product) { throw 'Catalog product identifier changed.' }
    $null = Send-Json "$base/api/inventory/$product/stock" 'POST' @{ quantity = 3 } 200
    $orderBody = @{ customerId = $customer; productId = $product; quantity = 1 }
    $stableOrderKey = [guid]::NewGuid().ToString("D")
    $orderHeaders = @{ 'Idempotency-Key' = $stableOrderKey }
    $order = Send-Json "$base/api/orders" 'POST' $orderBody 201 $orderHeaders
    $stockAfterFirstOrder = (Invoke-RestMethod "$base/api/inventory/$product").quantity
    $retriedOrder = Send-Json "$base/api/orders" 'POST' $orderBody 201 $orderHeaders
    if ($retriedOrder.id -ne $order.id -or (Invoke-RestMethod "$base/api/inventory/$product").quantity -ne $stockAfterFirstOrder) { throw 'Order retry duplicated stock reservation.' }
    $null = Send-Json "$base/api/orders/$($order.id)/payment" 'POST' @{ succeed = $true } 200
    $paid = Invoke-RestMethod "$base/api/orders/$($order.id)"
    if ($paid.status -ne 'Paid') { throw "Expected Paid, got $($paid.status)" }
    $failedOrder = Send-Json "$base/api/orders" 'POST' $orderBody 201
    $null = Send-Json "$base/api/orders/$($failedOrder.id)/payment" 'POST' @{ succeed = $false } 200
    $failed = Invoke-RestMethod "$base/api/orders/$($failedOrder.id)"
    if ($failed.status -ne 'PaymentFailed') { throw "Expected PaymentFailed, got $($failed.status)" }
    $stock = Invoke-RestMethod "$base/api/inventory/$product"
    $insufficient = Invoke-WebRequest -Uri "$base/api/orders" -Method Post -ContentType 'application/json' -Body (@{ customerId = $customer; productId = $product; quantity = 999 } | ConvertTo-Json) -SkipHttpErrorCheck
    if ($insufficient.StatusCode -ne 409) { throw "Expected insufficient stock conflict, got $($insufficient.StatusCode)" }
    $pendingOrder = Send-Json "$base/api/orders" 'POST' $orderBody 201
    $availableBeforeCompensation = (Invoke-RestMethod "$base/api/inventory/$product").quantity
    Stop-Process -Id $processes[0].Id -Force
    $failedDuringOutage = Invoke-WebRequest -Uri "$base/api/orders/$($pendingOrder.id)/payment" -Method Post -ContentType 'application/json' -Body (@{ succeed = $false } | ConvertTo-Json) -SkipHttpErrorCheck -TimeoutSec 10
    if ($failedDuringOutage.StatusCode -ne 503) { throw "Expected payment compensation outage 503, got $($failedDuringOutage.StatusCode)" }
    $restart = Start-Process dotnet -ArgumentList @('run','--no-build','--project','src/Services/Inventory/Inventory.Api','--urls','http://127.0.0.1:5083') -PassThru -WindowStyle Hidden -RedirectStandardOutput 'logs/phase05/5083-restart.out.log' -RedirectStandardError 'logs/phase05/5083-restart.err.log'
    $processes += $restart
    Wait-Health 'http://127.0.0.1:5083/health'
    $retryPayment = Invoke-WebRequest -Uri "$base/api/orders/$($pendingOrder.id)/payment" -Method Post -ContentType 'application/json' -Body (@{ succeed = $false } | ConvertTo-Json) -SkipHttpErrorCheck -TimeoutSec 10
    if ($retryPayment.StatusCode -ne 409) { throw "Expected idempotent payment retry conflict, got $($retryPayment.StatusCode)" }
    $compensatedStock = (Invoke-RestMethod "$base/api/inventory/$product").quantity
    if ($compensatedStock -ne ($availableBeforeCompensation + 1)) { throw "Reservation was not released after retry: before=$availableBeforeCompensation after=$compensatedStock" }
    Stop-Process -Id $restart.Id -Force
    $unavailable = Invoke-WebRequest -Uri "$base/api/orders" -Method Post -ContentType 'application/json' -Body ($orderBody | ConvertTo-Json) -SkipHttpErrorCheck -TimeoutSec 10
    if ($unavailable.StatusCode -ne 503) { throw "Expected controlled Inventory outage 503, got $($unavailable.StatusCode): $($unavailable.Content)" }
    Write-Output "gateway=healthy catalogId=$product paidOrder=$($order.id) failedOrder=$($failedOrder.id) availableStock=$($stock.quantity) insufficientStatus=409 inventoryOutageStatus=503 compensationRetryStatus=409"
}
finally {
    foreach ($process in $processes) { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } }
}
