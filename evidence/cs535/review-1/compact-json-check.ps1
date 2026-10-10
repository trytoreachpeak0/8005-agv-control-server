#Requires -Version 7
# control-server#535: feeds Find-/Get-ParallelEffectiveConfiguration* one event formatted by the Host's own
# Serilog CompactJsonFormatter (the formatter Install-ControlServerLocal.ps1 configures for the file sink).
param([string] $HostBin, [string] $ParallelDir)
$ErrorActionPreference = 'Stop'
foreach ($dll in 'Serilog.dll', 'Serilog.Formatting.Compact.dll') { Add-Type -Path (Join-Path $HostBin $dll) }
Import-Module (Join-Path $ParallelDir 'ParallelInstance.psm1') -Force
$writer = [IO.StringWriter]::new()
$formatter = [Serilog.Formatting.Compact.CompactJsonFormatter]::new()
$parser = [Serilog.Parsing.MessageTemplateParser]::new()
$template = $parser.Parse('EFFECTIVE_CONFIGURATION allowedWorkTypes={AllowedWorkTypes} allowedDispatchZones={AllowedDispatchZones} mesIngestBaseUrl={MesIngestBaseUrl}')
$props = [System.Collections.Generic.List[Serilog.Events.LogEventProperty]]::new()
$seq = { param($xs) [Serilog.Events.SequenceValue]::new([Serilog.Events.LogEventPropertyValue[]] @($xs | ForEach-Object { [Serilog.Events.ScalarValue]::new($_) })) }
$props.Add([Serilog.Events.LogEventProperty]::new('AllowedWorkTypes', (& $seq @('STAGING_TO_WIRE'))))
$props.Add([Serilog.Events.LogEventProperty]::new('AllowedDispatchZones', (& $seq @('WIRE'))))
$props.Add([Serilog.Events.LogEventProperty]::new('MesIngestBaseUrl', [Serilog.Events.ScalarValue]::new('http://127.0.0.1:5088')))
$at = [datetimeoffset]::UtcNow
$event = [Serilog.Events.LogEvent]::new($at, [Serilog.Events.LogEventLevel]::Information, $null, $template, $props)
$formatter.Format($event, $writer)
$line = $writer.ToString().Trim()
"compact line: $line"
$def = Read-ParallelInstanceDefinition -Path (Join-Path $ParallelDir 'instance-factory01-v2.production-mes.json')
$e = Find-ParallelEffectiveConfiguration -Lines @($line) -Since $at.AddSeconds(-5)
"found: $(ConvertTo-Json $e -Compress)"
"refusal: $(Get-ParallelEffectiveConfigurationRefusal -Definition $def -Effective $e)"
