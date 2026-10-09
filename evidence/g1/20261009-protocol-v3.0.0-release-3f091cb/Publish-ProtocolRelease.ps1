#Requires -Version 7
<#
.SYNOPSIS
    发布 protocol-v3.0.0：写入批准 attestation、跑发布 G1、打注释 tag、发 GitHub Release。

.DESCRIPTION
    由 protocol-v2.0.0 的同名脚本（control-server evidence/g1/20260916-protocol-v2.0.0-release-8657545/）复制，
    逐项改了发布身份（Version、Commit、三个哈希、tag 消息里的 protocolVersion: 4、Release 说明）；
    安全步骤全部保留，另有一处改动：
    - 批准说明从文件读入（-StatementPath），不经命令行传参。v2.0.0 那份的撇号在命令行传参时被引号吃掉，
      写成了 "product owners behalf"，而 attestation 一发布就不可变。

    v2.0.0 版沿用至今的改动：decidedAt 取 -DecidedAt（授权给出的时刻）；工作克隆放在短路径 -WorkParent 下
    （协议仓负例路径会撞 MAX_PATH，finalize／G1 也不能在 git worktree 里跑）；推送后核远端 tag 解引用到 $Commit，
    Release 附件下载回来核 SHA-256。

    协议治理（docs/release-governance.md，协议仓 3f091cb）允许由产品负责人授权的 AI agent 给出那一份批准。
    attestation 如实记录批准人是谁：approverKind 为 AI_AGENT 时，authorizedBy 必须写明授权人。

    顺序照治理文档：内容 commit 已冻结并推送 → 生成外置 attestation → 带 PROTOCOL_APPROVAL_ATTESTATION 跑 G1
    → 打指向内容 commit、消息里带两个哈希的注释 tag → 把同一份 attestation 作为 Release Asset 发布。任一步
    失败即停止；tag 只在 G1 PASS 之后创建，推送 tag 与创建 Release 之前各自再核一次远端状态。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('PRODUCT_OWNER', 'AI_AGENT')][string]$ApproverKind,
    [Parameter(Mandatory)][string]$OwnerId,
    [string]$AuthorizedBy,
    [Parameter(Mandatory)][string]$DecidedAt,
    [Parameter(Mandatory)][string]$StatementPath,
    [string]$WorkParent = 'C:\s\p3r'
)

$ErrorActionPreference = 'Stop'
if ($ApproverKind -eq 'AI_AGENT' -and [string]::IsNullOrWhiteSpace($AuthorizedBy)) {
    throw 'An AI_AGENT approval must name who authorized it (-AuthorizedBy).'
}
# The statement is read from a file so that no shell quoting can rewrite it on the way in.
if (-not (Test-Path -LiteralPath $StatementPath -PathType Leaf)) { throw "-StatementPath '$StatementPath' is not a file." }
$Statement = ([IO.File]::ReadAllText((Resolve-Path -LiteralPath $StatementPath).ProviderPath, [Text.UTF8Encoding]::new($false))).Trim()
if ([string]::IsNullOrWhiteSpace($Statement) -or $Statement -match '[\r\n]') { throw 'The statement file must hold exactly one non-empty line.' }
if ($DecidedAt -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$') { throw "-DecidedAt must be a UTC timestamp like 2026-09-16T12:19:21Z, got '$DecidedAt'." }
$Repository = 'trytoreachpeak0/8005-agv-protocol'
$RepositoryUrl = 'https://github.com/trytoreachpeak0/8005-agv-protocol.git'
$Version = '3.0.0'
$Tag = "protocol-v$Version"
$Commit = '3f091cb2eae7c58cec54a95dd9389c9180bc7b4c'
$ManifestSha256 = 'd5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e'
$SchemaBundleSha256 = 'e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43'
$VectorsSha256 = 'be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e'
$ReleaseRoot = $PSScriptRoot
$runStamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$workRoot = Join-Path $WorkParent "work-$runStamp"
$clone = Join-Path $workRoot 'protocol'
$attestationPath = Join-Path $ReleaseRoot 'release-approval.json'
$logPath = Join-Path $ReleaseRoot "publish-$runStamp.log"

function Write-Step([string]$Text) { $line = "[$([DateTimeOffset]::UtcNow.ToString('HH:mm:ss'))] $Text"; Write-Host $line; Add-Content -LiteralPath $logPath -Value $line }
function Invoke-Checked([string]$FilePath, [string[]]$Arguments) {
    $output = & $FilePath @Arguments 2>&1
    $output | Add-Content -LiteralPath $logPath
    if ($LASTEXITCODE -ne 0) { throw "$FilePath $($Arguments -join ' ') exited $LASTEXITCODE" }
    return $output
}

New-Item -ItemType Directory -Force -Path $ReleaseRoot, $workRoot | Out-Null
if (Test-Path -LiteralPath $attestationPath) { throw "An attestation already exists at $attestationPath. A second approval is a new decision; move the old file away deliberately first." }
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { $env:PATH = "C:\Program Files\nodejs;$env:PATH" }
foreach ($tool in 'git', 'gh', 'node', 'corepack') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool is not available" }
}

Write-Step "1/7 核对远端：fp/v2-candidate 的顶是 $Commit，$Tag 还不存在"
$remoteHead = ((Invoke-Checked git @('ls-remote', $RepositoryUrl, 'refs/heads/fp/v2-candidate')) -split '\s+')[0]
if ($remoteHead -ne $Commit) { throw "origin/fp/v2-candidate is $remoteHead, not $Commit" }
if (Invoke-Checked git @('ls-remote', '--tags', $RepositoryUrl, "refs/tags/$Tag")) { throw "$Tag already exists on the remote" }

Write-Step "2/7 干净克隆并检出 $Commit，核 manifest 哈希"
Invoke-Checked git @('clone', '--quiet', '--branch', 'fp/v2-candidate', $RepositoryUrl, $clone) | Out-Null
Invoke-Checked git @('-C', $clone, 'checkout', '--quiet', $Commit) | Out-Null
$actualManifest = (Get-FileHash -LiteralPath (Join-Path $clone 'manifest\release.json') -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualManifest -ne $ManifestSha256) { throw "manifest/release.json hashes to $actualManifest, not $ManifestSha256" }
Push-Location -LiteralPath $clone
try { Invoke-Checked corepack @('pnpm', 'install', '--frozen-lockfile') | Out-Null } finally { Pop-Location }

Write-Step "3/7 写入外置 attestation（不进 git），批准人 $OwnerId（$ApproverKind）"
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
[IO.File]::WriteAllText($attestationPath, ($attestation | ConvertTo-Json -Depth 5) + "`n", [Text.UTF8Encoding]::new($false))
$attestationSha256 = (Get-FileHash -LiteralPath $attestationPath -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Step '4/7 带 PROTOCOL_APPROVAL_ATTESTATION 跑发布 G1'
$env:PROTOCOL_APPROVAL_ATTESTATION = $attestationPath
Push-Location -LiteralPath $clone
try { $g1Text = (Invoke-Checked corepack @('pnpm', 'g1')) -join "`n" } finally { Pop-Location; Remove-Item Env:PROTOCOL_APPROVAL_ATTESTATION }
$g1Result = Get-Content -Raw -LiteralPath (Join-Path $clone 'evidence\g1-result.json') | ConvertFrom-Json
Copy-Item -LiteralPath (Join-Path $clone 'evidence\g1-result.json') -Destination (Join-Path $ReleaseRoot 'release-g1-result.json')
if ($g1Result.status -ne 'PASS' -or $g1Result.failures.Count -ne 0 -or
    $g1Result.approvalAttestationStatus -ne 'APPROVED' -or
    $g1Result.approvalAttestationSource -ne 'EXTERNAL_RELEASE_ASSET' -or
    $g1Result.candidateManifestSha256 -ne $ManifestSha256 -or
    $g1Result.approvalAttestationSha256 -ne $attestationSha256) {
    throw "Release G1 did not pass with this attestation: $g1Text"
}

$approvedByLine = if ($ApproverKind -eq 'AI_AGENT') { "$OwnerId (AI_AGENT, authorized by $AuthorizedBy)" } else { "$OwnerId (PRODUCT_OWNER)" }

Write-Step "5/7 打注释 tag $Tag"
$tagMessage = @"
$Tag

repository: 8005-agv-protocol
releaseVersion: $Version
protocolVersion: 4
profileId: AGV_FULL_PRODUCT
commit: $Commit
contentManifestSha256: $ManifestSha256
approvalAttestationSha256: $attestationSha256
schemaBundleSha256: $SchemaBundleSha256
vectorsSha256: $VectorsSha256
approvedBy: $approvedByLine
"@
$tagMessagePath = Join-Path $workRoot 'tag-message.txt'
[IO.File]::WriteAllText($tagMessagePath, $tagMessage, [Text.UTF8Encoding]::new($false))
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
$notesPath = Join-Path $workRoot 'release-notes.md'
[IO.File]::WriteAllText($notesPath, (@"
全产品协议 3.0.0 正式发布（``AGV_FULL_PRODUCT``，protocolVersion 4）。取代 ``protocol-v2.0.0``，是破坏性主版本：绑定 ``protocol-v2.0.0`` 的门禁与 L2 证据自本次发布起不再是现行证据。

| 项 | 值 |
| --- | --- |
| commit | ``$Commit`` |
| content manifest | ``$ManifestSha256`` |
| approval attestation（本 Release 的 ``release-approval.json``） | ``$attestationSha256`` |
| schema bundle | ``$SchemaBundleSha256`` |
| vectors | ``$VectorsSha256`` |
| 批准 | $approvedByLine |

发布 G1 使用本 Release 附带的 attestation 运行，``status: PASS``，``failures`` 为空。
"@), [Text.UTF8Encoding]::new($false))
Invoke-Checked gh @('release', 'create', $Tag, $attestationPath, '--repo', $Repository, '--title', $Tag, '--notes-file', $notesPath, '--verify-tag') | Out-Null

$asset = gh release view $Tag --repo $Repository --json assets --jq '.assets[] | select(.name == "release-approval.json") | .name'
if ($asset -ne 'release-approval.json') { throw 'The release was created but the attestation asset is not on it; check the release page.' }
$downloadRoot = Join-Path $workRoot 'asset-readback'
Invoke-Checked gh @('release', 'download', $Tag, '--repo', $Repository, '--pattern', 'release-approval.json', '--dir', $downloadRoot) | Out-Null
$downloadedSha256 = (Get-FileHash -LiteralPath (Join-Path $downloadRoot 'release-approval.json') -Algorithm SHA256).Hash.ToLowerInvariant()
if ($downloadedSha256 -ne $attestationSha256) { throw "The release asset hashes to $downloadedSha256, not $attestationSha256; check the release page." }
Write-Step "完成：$Tag -> $Commit；attestation $attestationSha256；日志 $logPath"
