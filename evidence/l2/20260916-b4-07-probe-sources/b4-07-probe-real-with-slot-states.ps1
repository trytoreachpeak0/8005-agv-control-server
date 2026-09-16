#Requires -Version 7
param([Parameter(Mandatory)][object]$Context)
throw 'This probe must never reach its body: the orchestrator is expected to refuse its setup file.'
