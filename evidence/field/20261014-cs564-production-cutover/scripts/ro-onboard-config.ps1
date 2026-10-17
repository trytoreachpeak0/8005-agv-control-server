#Requires -Version 7
# READ-ONLY on a vehicle PC: installed build commit, server endpoint, IO target.
$c = Get-Content -Raw C:/8005/OnboardHmi/appsettings.json | ConvertFrom-Json
"build=$($c.wireToGate.onboardBuildCommit) wtg=$($c.wireToGate.host):$($c.wireToGate.port) io=$($c.ioModule.host):$($c.ioModule.port)"
