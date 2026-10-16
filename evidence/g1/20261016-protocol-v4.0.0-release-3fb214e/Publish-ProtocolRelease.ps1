#Requires -Version 7
<#
.SYNOPSIS
    发布 protocol-v4.0.0，分两段：Prepare 写入批准 attestation、跑发布 G1、写出 tag 消息与 Release 说明；
    Publish 在调度核过这两份文字之后打注释 tag、推送、发 GitHub Release。

.DESCRIPTION
    由 protocol-v3.0.0 的同名脚本（control-server evidence/g1/20261009-protocol-v3.0.0-release-3f091cb/）复制，
    逐项改了发布身份（Version、Commit、三个哈希、tag 消息里的 protocolVersion: 5、Release 说明）；
    安全步骤全部保留，另有一处结构改动：
    - 原来一次跑完的七步拆成 -Phase Prepare（1–4 步）与 -Phase Publish（5–7 步）。tag 一推就撤不回来，
      这一次调度要求在打 tag 之前核一遍 tag 消息全文与 Release 说明。Prepare 把两份文字写进本目录
      （tag-message.txt、release-notes.md），Publish 原样使用这两个文件，并在动手前核：
      attestation 的哈希仍等于发布 G1 报告的哈希、两份文字里写的就是这个哈希、远端 fp/v2-candidate 顶端
      仍是 $Commit、远端还没有同名 tag。两段之间任何一样变了就停。

    沿用的做法：批准说明从文件读入（-StatementPath），不经命令行传参；decidedAt 取 -DecidedAt（授权给出的时刻）；
    工作克隆放在短路径 -WorkParent 下，不在 git worktree 里跑 G1；推送后核远端 tag 解引用到 $Commit；
    Release 附件下载回来核 SHA-256。

    协议治理（docs/release-governance.md，协议仓 3fb214e）允许由产品负责人授权的 AI agent 给出那一份批准。
    attestation 如实记录批准人是谁：approverKind 为 AI_AGENT 时，authorizedBy 必须写明授权人。

    顺序照治理文档：内容 commit 已冻结并推送 → 生成外置 attestation → 带 PROTOCOL_APPROVAL_ATTESTATION 跑 G1
    → 打指向内容 commit、消息里带两个哈希的注释 tag → 把同一份 attestation 作为 Release Asset 发布。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Prepare', 'Publish')][string]$Phase,
    [ValidateSet('PRODUCT_OWNER', 'AI_AGENT')][string]$ApproverKind,
    [string]$OwnerId,
    [string]$AuthorizedBy,
    [string]$DecidedAt,
    [string]$StatementPath,
    [string]$WorkParent = 'F:\8005-scratch\p4r'
)

$ErrorActionPreference = 'Stop'
$Repository = 'trytoreachpeak0/8005-agv-protocol'
$RepositoryUrl = 'https://github.com/trytoreachpeak0/8005-agv-protocol.git'
$Version = '4.0.0'
$ProtocolVersion = 5
$Tag = "protocol-v$Version"
$Commit = '3fb214e57d861a909d872511940d26133eb32dca'
$ManifestSha256 = 'beca46a7fe98003ce1f21efb88f2c1406f3040883d0b372a51505a843e306f3a'
$SchemaBundleSha256 = 'dad44a5e93203eb0b7ade50b23d12f2aaffc0e2abb3c8e5dcdd3de62d931d628'
$VectorsSha256 = '24cc3a5e5023bc7ead70f609456136208a40c04c1ecd0982a7182880d8eaa0c7'
$ReleaseRoot = $PSScriptRoot
$runStamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$workRoot = Join-Path $WorkParent "work-$runStamp"
$clone = Join-Path $workRoot 'protocol'
$attestationPath = Join-Path $ReleaseRoot 'release-approval.json'
$releaseG1Path = Join-Path $ReleaseRoot 'release-g1-result.json'
$tagMessagePath = Join-Path $ReleaseRoot 'tag-message.txt'
$notesPath = Join-Path $ReleaseRoot 'release-notes.md'
$logPath = Join-Path $ReleaseRoot "publish-$($Phase.ToLowerInvariant())-$runStamp.log"
$utf8 = [Text.UTF8Encoding]::new($false)

function Write-Step([string]$Text) { $line = "[$([DateTimeOffset]::UtcNow.ToString('HH:mm:ss'))] $Text"; Write-Host $line; Add-Content -LiteralPath $logPath -Value $line }
function Invoke-Checked([string]$FilePath, [string[]]$Arguments) {
    $output = & $FilePath @Arguments 2>&1
    $output | Add-Content -LiteralPath $logPath
    if ($LASTEXITCODE -ne 0) { throw "$FilePath $($Arguments -join ' ') exited $LASTEXITCODE" }
    return $output
}
function Assert-RemoteUnchanged {
    $remoteHead = ((Invoke-Checked git @('ls-remote', $RepositoryUrl, 'refs/heads/fp/v2-candidate')) -split '\s+')[0]
    if ($remoteHead -ne $Commit) { throw "origin/fp/v2-candidate is $remoteHead, not $Commit" }
    if (Invoke-Checked git @('ls-remote', '--tags', $RepositoryUrl, "refs/tags/$Tag")) { throw "$Tag already exists on the remote" }
}
function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

New-Item -ItemType Directory -Force -Path $ReleaseRoot, $workRoot | Out-Null
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { $env:PATH = "C:\Program Files\nodejs;$env:PATH" }
foreach ($tool in 'git', 'gh', 'node', 'corepack') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool is not available" }
}
# pnpm v11 otherwise creates .pnpm-store inside a clone on drive F, and G1 then fails on it with EISDIR.
$env:pnpm_config_store_dir = "$env:LOCALAPPDATA\pnpm-store"
$env:npm_config_store_dir = "$env:LOCALAPPDATA\pnpm-store"

if ($Phase -eq 'Prepare') {
    if (-not $ApproverKind -or -not $OwnerId -or -not $DecidedAt -or -not $StatementPath) {
        throw 'Prepare needs -ApproverKind, -OwnerId, -DecidedAt and -StatementPath.'
    }
    if ($ApproverKind -eq 'AI_AGENT' -and [string]::IsNullOrWhiteSpace($AuthorizedBy)) {
        throw 'An AI_AGENT approval must name who authorized it (-AuthorizedBy).'
    }
    # The statement is read from a file so that no shell quoting can rewrite it on the way in.
    if (-not (Test-Path -LiteralPath $StatementPath -PathType Leaf)) { throw "-StatementPath '$StatementPath' is not a file." }
    $Statement = ([IO.File]::ReadAllText((Resolve-Path -LiteralPath $StatementPath).ProviderPath, $utf8)).Trim()
    if ([string]::IsNullOrWhiteSpace($Statement) -or $Statement -match '[\r\n]') { throw 'The statement file must hold exactly one non-empty line.' }
    if ($DecidedAt -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$') { throw "-DecidedAt must be a UTC timestamp like 2026-10-16T01:15:00Z, got '$DecidedAt'." }
    if (Test-Path -LiteralPath $attestationPath) { throw "An attestation already exists at $attestationPath. A second approval is a new decision; move the old file away deliberately first." }

    Write-Step "1/4 核对远端：fp/v2-candidate 的顶是 $Commit，$Tag 还不存在"
    Assert-RemoteUnchanged

    Write-Step "2/4 干净克隆并检出 $Commit，核 manifest 哈希"
    Invoke-Checked git @('clone', '--quiet', '--branch', 'fp/v2-candidate', $RepositoryUrl, $clone) | Out-Null
    Invoke-Checked git @('-C', $clone, 'checkout', '--quiet', $Commit) | Out-Null
    $actualManifest = Get-Sha256 (Join-Path $clone 'manifest\release.json')
    if ($actualManifest -ne $ManifestSha256) { throw "manifest/release.json hashes to $actualManifest, not $ManifestSha256" }
    Push-Location -LiteralPath $clone
    try { Invoke-Checked corepack @('pnpm', 'install', '--frozen-lockfile') | Out-Null } finally { Pop-Location }

    Write-Step "3/4 写入外置 attestation（不进 git），批准人 $OwnerId（$ApproverKind）"
    $approval = [ordered]@{
        ownerId = $OwnerId
        approverKind = $ApproverKind
    }
    if ($ApproverKind -eq 'AI_AGENT') { $approval.authorizedBy = $AuthorizedBy }
    $approval.decidedAt = $DecidedAt
    $approval.decision = 'APPROVED'
    $approval.statement = $Statement
    $attestation = [ordered]@{
        schemaVersion = '1.0.0'
        candidateVersion = $Version
        status = 'APPROVED'
        protocolCommit = $Commit
        contentManifestSha256 = $ManifestSha256
        approvals = @($approval)
        statement = "Approval of $Tag at commit $Commit and content manifest $ManifestSha256. Kept outside Git and published as a GitHub Release Asset, so it changes neither the manifest hash nor the commit it approves."
    }
    [IO.File]::WriteAllText($attestationPath, ($attestation | ConvertTo-Json -Depth 5) + "`n", $utf8)
    $attestationSha256 = Get-Sha256 $attestationPath

    Write-Step '4/4 带 PROTOCOL_APPROVAL_ATTESTATION 跑发布 G1'
    $env:PROTOCOL_APPROVAL_ATTESTATION = $attestationPath
    Push-Location -LiteralPath $clone
    try { $g1Text = (Invoke-Checked corepack @('pnpm', 'g1')) -join "`n" } finally { Pop-Location; Remove-Item Env:PROTOCOL_APPROVAL_ATTESTATION }
    $g1Result = Get-Content -Raw -LiteralPath (Join-Path $clone 'evidence\g1-result.json') | ConvertFrom-Json
    Copy-Item -LiteralPath (Join-Path $clone 'evidence\g1-result.json') -Destination $releaseG1Path
    if ($g1Result.status -ne 'PASS' -or $g1Result.failures.Count -ne 0 -or
        $g1Result.approvalAttestationStatus -ne 'APPROVED' -or
        $g1Result.approvalAttestationSource -ne 'EXTERNAL_RELEASE_ASSET' -or
        $g1Result.candidateManifestSha256 -ne $ManifestSha256 -or
        $g1Result.approvalAttestationSha256 -ne $attestationSha256) {
        throw "Release G1 did not pass with this attestation: $g1Text"
    }

    $approvedByLine = if ($ApproverKind -eq 'AI_AGENT') { "$OwnerId (AI_AGENT, authorized by $AuthorizedBy)" } else { "$OwnerId (PRODUCT_OWNER)" }
    $tagMessage = @"
$Tag

repository: 8005-agv-protocol
releaseVersion: $Version
protocolVersion: $ProtocolVersion
profileId: AGV_FULL_PRODUCT
commit: $Commit
contentManifestSha256: $ManifestSha256
approvalAttestationSha256: $attestationSha256
schemaBundleSha256: $SchemaBundleSha256
vectorsSha256: $VectorsSha256
approvedBy: $approvedByLine
"@
    [IO.File]::WriteAllText($tagMessagePath, $tagMessage, $utf8)
    [IO.File]::WriteAllText($notesPath, (@"
全产品协议 4.0.0 正式发布（``AGV_FULL_PRODUCT``，protocolVersion 5）。取代 ``protocol-v3.0.0``，是破坏性主版本：绑定 ``protocol-v3.0.0`` 的门禁与 L2 证据自本次发布起不再是现行证据。

| 项 | 值 |
| --- | --- |
| commit | ``$Commit`` |
| content manifest | ``$ManifestSha256`` |
| approval attestation（本 Release 的 ``release-approval.json``） | ``$attestationSha256`` |
| schema bundle | ``$SchemaBundleSha256`` |
| vectors | ``$VectorsSha256`` |
| 批准 | $approvedByLine |

发布 G1 使用本 Release 附带的 attestation 运行，``status: PASS``，``failures`` 为空。
"@), $utf8)
    Write-Step "准备完成：attestation $attestationSha256；tag 消息 $tagMessagePath；Release 说明 $notesPath。核过之后用 -Phase Publish 发布。"
    return
}

# Phase Publish: use exactly the files Prepare wrote and the coordinator reviewed.
foreach ($path in $attestationPath, $releaseG1Path, $tagMessagePath, $notesPath) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$path is missing; run -Phase Prepare first." }
}
$attestationSha256 = Get-Sha256 $attestationPath
$g1Result = Get-Content -Raw -LiteralPath $releaseG1Path | ConvertFrom-Json
if ($g1Result.status -ne 'PASS' -or $g1Result.approvalAttestationSha256 -ne $attestationSha256 -or $g1Result.candidateManifestSha256 -ne $ManifestSha256) {
    throw "release-g1-result.json does not certify the attestation now on disk ($attestationSha256)."
}
$tagMessageText = [IO.File]::ReadAllText($tagMessagePath, $utf8)
$notesText = [IO.File]::ReadAllText($notesPath, $utf8)
foreach ($needle in "approvalAttestationSha256: $attestationSha256", "contentManifestSha256: $ManifestSha256", "commit: $Commit", "protocolVersion: $ProtocolVersion") {
    if (-not $tagMessageText.Contains($needle)) { throw "tag-message.txt lacks '$needle'." }
}
foreach ($needle in $attestationSha256, $ManifestSha256, $Commit) {
    if (-not $notesText.Contains($needle)) { throw "release-notes.md lacks '$needle'." }
}

Write-Step "5/7 核远端未变，打注释 tag $Tag"
Assert-RemoteUnchanged
Invoke-Checked git @('clone', '--quiet', '--branch', 'fp/v2-candidate', $RepositoryUrl, $clone) | Out-Null
if ((Invoke-Checked git @('-C', $clone, 'rev-parse', 'HEAD')) -ne $Commit) { throw "Fresh clone of fp/v2-candidate is not at $Commit" }
# A fresh clone carries no user.name/user.email, and an annotated tag records a tagger. Name the
# identity every commit on this line uses rather than inheriting whatever the machine happens to have.
Invoke-Checked git @('-C', $clone, '-c', 'user.name=Zhengyu Shao', '-c', 'user.email=trytoreachpeak0@gmail.com',
    'tag', '-a', $Tag, $Commit, '-F', $tagMessagePath) | Out-Null

Write-Step '6/7 推送 tag（推送前再核一次远端没有同名 tag）'
if (Invoke-Checked git @('ls-remote', '--tags', $RepositoryUrl, "refs/tags/$Tag")) { throw "$Tag appeared on the remote meanwhile" }
Invoke-Checked git @('-C', $clone, 'push', 'origin', "refs/tags/$Tag") | Out-Null
$peeled = ((Invoke-Checked git @('ls-remote', '--tags', $RepositoryUrl, "refs/tags/$Tag^{}")) -split '\s+')[0]
if ($peeled -ne $Commit) { throw "The pushed $Tag dereferences to '$peeled', not $Commit; stop before creating the release." }

Write-Step '7/7 创建 GitHub Release 并上传同一份 attestation'
Invoke-Checked gh @('release', 'create', $Tag, $attestationPath, '--repo', $Repository, '--title', $Tag, '--notes-file', $notesPath, '--verify-tag') | Out-Null

$asset = gh release view $Tag --repo $Repository --json assets --jq '.assets[] | select(.name == "release-approval.json") | .name'
if ($asset -ne 'release-approval.json') { throw 'The release was created but the attestation asset is not on it; check the release page.' }
$downloadRoot = Join-Path $workRoot 'asset-readback'
Invoke-Checked gh @('release', 'download', $Tag, '--repo', $Repository, '--pattern', 'release-approval.json', '--dir', $downloadRoot) | Out-Null
$downloadedSha256 = Get-Sha256 (Join-Path $downloadRoot 'release-approval.json')
if ($downloadedSha256 -ne $attestationSha256) { throw "The release asset hashes to $downloadedSha256, not $attestationSha256; check the release page." }
Write-Step "完成：$Tag -> $Commit；attestation $attestationSha256；日志 $logPath"
