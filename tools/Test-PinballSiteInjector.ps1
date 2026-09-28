param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$NamespaceName,
    [Parameter(Mandatory = $true)][string]$SiteUrl
)

$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $ExePath).Path)
$type = $assembly.GetType("$NamespaceName.PinballSiteInjector", $true)
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$extract = $type.GetMethod('ExtractWebSocketDebuggerUrl', $flags)
$response = $type.GetMethod('ResponseIsTrue', $flags)
$ready = $type.GetMethod('BuildReadyScript', $flags)
$inject = $type.GetMethod('BuildInjectionScript', $flags)
$verify = $type.GetMethod('BuildVerificationScript', $flags)

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -cne $Actual) { throw "$Message (expected=$Expected, actual=$Actual)" }
}

$unrelated = '[{"url":"chrome://newtab/","webSocketDebuggerUrl":"ws://unrelated"}]'
Assert-Equal '' ($extract.Invoke($null, [object[]]@([string]$unrelated))) 'Unrelated tab must not be selected'

$target = @(
    @{ url = 'chrome://newtab/'; webSocketDebuggerUrl = 'ws://unrelated' },
    @{ url = "$($SiteUrl.TrimEnd('/'))/?names=test"; webSocketDebuggerUrl = 'ws://target' }
) | ConvertTo-Json -Compress
Assert-Equal 'ws://target' ($extract.Invoke($null, [object[]]@([string]$target))) 'Exact site tab selection'

$lookalike = ConvertTo-Json -InputObject @(@{ url = "$($SiteUrl.TrimEnd('/')).invalid/?names=test"; webSocketDebuggerUrl = 'ws://lookalike' }) -Compress
Assert-Equal '' ($extract.Invoke($null, [object[]]@([string]$lookalike))) 'Lookalike site must not be selected'

Assert-Equal $true ($response.Invoke($null, [object[]]@('{"id":1,"result":{"result":{"type":"boolean","value":true}}}'))) 'CDP true response'
Assert-Equal $false ($response.Invoke($null, [object[]]@('{"id":1,"result":{"result":{"type":"boolean","value":false}}}'))) 'CDP false response'
Assert-Equal $false ($response.Invoke($null, [object[]]@('{"id":1,"error":{"message":"value:true"}}'))) 'CDP error response'

$readyScript = [string]$ready.Invoke($null, @())
$injectionScript = [string]$inject.Invoke($null, @('Audit*2'))
$verificationScript = [string]$verify.Invoke($null, @('Audit*2', [long]2))
Assert-Equal $true ($readyScript.Contains("#in_names") -and $readyScript.Contains("#sltMap")) 'Web readiness gate'
Assert-Equal $true ($injectionScript.Contains("querySelector('#in_names')") -and !$injectionScript.Contains('querySelectorAll')) 'Exact name field injection'
Assert-Equal $true ($verificationScript.Contains('getCount()===2') -and $verificationScript.Contains('Audit*2')) 'Name and coin verification'

Write-Output "PASS: $($assembly.GetName().Name) injector tab, readiness, target, response, verification"
