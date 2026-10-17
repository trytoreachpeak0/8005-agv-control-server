#Requires -Version 7
# READ-ONLY, runs ON a vehicle PC: one GET of the server's vehicle-safety projection with the vehicle's own credential
# (the same request the onboard client makes every second). The credential is never printed.
$ProgressPreference = 'SilentlyContinue'
$c = Get-Content -Raw C:/8005/OnboardHmi/appsettings.json | ConvertFrom-Json
$var = $c.vehicleSafety.credentialEnvironmentVariable
$cred = [Environment]::GetEnvironmentVariable($var, 'Machine') ?? [Environment]::GetEnvironmentVariable($var, 'User')
"credentialPresent=$([bool]$cred) var=$var"
$r = Invoke-WebRequest $c.vehicleSafety.endpoint -Headers @{ Authorization = "Bearer $cred" } -NoProxy -TimeoutSec 10 -SkipHttpErrorCheck
"status=$($r.StatusCode) at $(Get-Date -Format o)"
$r.Content
