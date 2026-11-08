param(
    [Parameter(Mandatory)][string]$OrdersConnection,
    [Parameter(Mandatory)][string]$InventoryConnection,
    [Parameter(Mandatory)][string]$CatalogConnection,
    [Parameter(Mandatory)][string]$LegacyConnection
)
$ErrorActionPreference = 'Stop'
$env:ConnectionStrings__Orders = $OrdersConnection
$env:ConnectionStrings__Inventory = $InventoryConnection
$env:ConnectionStrings__Catalog = $CatalogConnection
$env:ConnectionStrings__LegacyCommerce = $LegacyConnection
$env:OrdersInternal__Token = [guid]::NewGuid().ToString('N')
$env:RabbitMq__UseInMemory = 'true'
$env:Catalog__WritesEnabled = 'false'
$env:CatalogOwnership__Mode = 'Extracted'
$env:InventoryOwnership__Mode = 'Extracted'
$env:OrdersOwnership__Mode = 'Extracted'
$env:OrdersService__BaseUrl = 'http://127.0.0.1:5084/'
$env:LegacyService__BaseUrl = 'http://127.0.0.1:5081/'
$env:CatalogService__BaseUrl = 'http://127.0.0.1:5082/'
$env:InventoryService__BaseUrl = 'http://127.0.0.1:5083/'
$env:ReverseProxy__Routes__orders__ClusterId = 'orders'
$env:ReverseProxy__Routes__orderById__ClusterId = 'orders'
$processes = @()
New-Item -ItemType Directory -Force logs/phase06 | Out-Null
function Wait-Health([int]$port) {
    $last = 'no response'
    for ($i=0; $i -lt 40; $i++) {
        try { $r=Invoke-WebRequest "http://127.0.0.1:$port/health" -SkipHttpErrorCheck -TimeoutSec 2; if($r.StatusCode -eq 200){return}; $last="HTTP $($r.StatusCode)" } catch { $last=$_.Exception.Message }
        Start-Sleep -Milliseconds 500
    }
    throw "Service $port failed readiness: $last"
}
function Send-Json([string]$path, $body, [int]$expected, [hashtable]$headers=@{}) {
    $r=Invoke-WebRequest "http://127.0.0.1:5080$path" -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json) -Headers $headers -SkipHttpErrorCheck -TimeoutSec 15
    if($r.StatusCode -ne $expected){throw "$path returned $($r.StatusCode): $($r.Content)"}
    return $r.Content | ConvertFrom-Json
}
function Wait-Status([guid]$id, [string]$status) {
    $last='missing'
    for($i=0;$i -lt 80;$i++) {
        try { $o=Invoke-RestMethod "http://127.0.0.1:5080/api/orders/$id" -TimeoutSec 3; $last=$o.status; if($last -eq $status){return $o} } catch {$last=$_.Exception.Message}
        Start-Sleep -Milliseconds 500
    }
    throw "Order $id expected $status; last=$last"
}
try {
    foreach($service in @(@('src/Services/Catalog/Catalog.Api',5082),@('src/Services/Inventory/Inventory.Api',5083),@('src/Services/Orders/Orders.Api',5084),@('src/LegacyMonolith/LegacyCommerce.Api',5081),@('src/Gateway/StranglerForge.Gateway',5080))) {
        $p=Start-Process dotnet -ArgumentList @('run','--no-build','--project',$service[0],'--urls',"http://127.0.0.1:$($service[1])") -PassThru -WindowStyle Hidden -RedirectStandardOutput "logs/phase06/$($service[1]).out.log" -RedirectStandardError "logs/phase06/$($service[1]).err.log"
        $processes += $p
        Wait-Health $service[1]
    }
    $customer='00000000-0000-0000-0000-000000000101'
    $product='00000000-0000-0000-0000-000000000201'
    $null=Send-Json "/api/inventory/$product/stock" @{quantity=4} 200
    $body=@{customerId=$customer;productId=$product;quantity=1}
    $key=[guid]::NewGuid()
    $correlation=[guid]::NewGuid()
    $created=Send-Json '/api/orders' $body 201 @{'Idempotency-Key'=$key.ToString('D');'X-Correlation-ID'=$correlation.ToString('D')}
    if($created.id -ne $key.ToString('D')){throw 'Order idempotency key was not preserved'}
    $null=Wait-Status $key 'PaymentPending'
    $null=Send-Json "/api/orders/$key/payment" @{succeed=$true} 202
    $paid=Wait-Status $key 'Confirmed'
    $failure=Send-Json '/api/orders' $body 201
    $null=Wait-Status ([guid]$failure.id) 'PaymentPending'
    $null=Send-Json "/api/orders/$($failure.id)/payment" @{succeed=$false} 202
    $cancelled=Wait-Status ([guid]$failure.id) 'Cancelled'
    $tooMany=Send-Json '/api/orders' @{customerId=$customer;productId=$product;quantity=999} 201
    $failed=Wait-Status ([guid]$tooMany.id) 'Failed'
    $notifications=Invoke-RestMethod "http://127.0.0.1:5080/api/notifications/$customer"
    if(-not ($notifications | Where-Object { $_.id -eq $paid.id }) -or -not ($notifications | Where-Object { $_.id -eq $cancelled.id })){throw 'Payment notifications missing'}
    Write-Output "ordersThroughYarp=passed paid=$($paid.id) cancelled=$($cancelled.id) insufficient=$($failed.id) paymentStatus=202"
}
finally { foreach($p in $processes){if(-not $p.HasExited){Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue}} }
