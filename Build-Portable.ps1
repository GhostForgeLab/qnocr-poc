[CmdletBinding()]
param([string]$Version = ('20261024-test-' + (Get-Date -Format 'HHmmss')))
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^[a-zA-Z0-9][a-zA-Z0-9.-]{0,70}$') { throw 'Invalid version.' }
$projectRoot = $PSScriptRoot
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$env:DOTNET_ROOT = Split-Path $dotnet
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli-home'
$work = Join-Path $projectRoot "Output\PortableBuild\$Version"
$distribution = Join-Path $projectRoot 'Output\Distributions'
$packageName = "QNAgent-Portable-$Version-win-x64"
$stage = Join-Path $work $packageName
$archive = Join-Path $distribution "$packageName.zip"
if ((Test-Path -LiteralPath $work) -or (Test-Path -LiteralPath $archive)) { throw 'Use a new version; existing builds are never overwritten.' }
New-Item -ItemType Directory -Path $stage,$distribution -Force | Out-Null
$artifacts = Join-Path $work 'artifacts'
$desktop = Join-Path $work 'desktop-publish'
$worker = Join-Path $work 'worker-publish'
$common = @('-c','Release','-r','win-x64','--self-contained','true',
    '--artifacts-path',$artifacts,'--source',(Join-Path $projectRoot '.tools\nuget'),
    '-p:PortableRelease=true','-p:PublishSingleFile=false','-p:PublishTrimmed=false',
    '-p:DebugType=none','-p:DebugSymbols=false','-p:NuGetAudit=false','-v','minimal')
& $dotnet publish (Join-Path $projectRoot 'qnocr-poc.csproj') @common -o $worker
if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }
& $dotnet publish (Join-Path $projectRoot 'DesktopClient\DesktopClient.csproj') @common -o $desktop
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }

# Only compiler publish output enters the package. Never copy a project or Output data tree.
Get-ChildItem -LiteralPath $desktop -Force | Copy-Item -Destination $stage -Recurse
foreach ($name in @('qnocr-poc.exe','qnocr-poc.dll','qnocr-poc.deps.json','qnocr-poc.runtimeconfig.json')) {
    Copy-Item -LiteralPath (Join-Path $worker $name) -Destination (Join-Path $stage $name) -Force
}

# App-local, signed release CRTs required by Paddle/OpenCV. Do not copy Windows system DLLs.
$vcNames = @('msvcp140.dll','msvcp140_1.dll','msvcp140_2.dll','msvcp140_atomic_wait.dll',
    'msvcp140_codecvt_ids.dll','vcruntime140.dll','vcruntime140_1.dll','concrt140.dll')
foreach ($name in $vcNames) {
    $source = Join-Path ([Environment]::GetFolderPath('System')) $name
    $signature = Get-AuthenticodeSignature -LiteralPath $source
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft') {
        throw "Unsigned or untrusted VC runtime: $name"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $stage $name)
}

$utf8 = New-Object System.Text.UTF8Encoding($true)
[IO.File]::WriteAllText((Join-Path $stage '使用说明.txt'), [IO.File]::ReadAllText((Join-Path $projectRoot 'docs\PORTABLE_QUICK_START.txt')), $utf8)
$licenses = Join-Path $stage 'Licenses'
New-Item -ItemType Directory -Path $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot '.tools\dotnet\ThirdPartyNotices.txt') -Destination (Join-Path $licenses 'DotNet-ThirdPartyNotices.txt')
$assets = Get-Content -LiteralPath (Join-Path $artifacts 'obj\DesktopClient\project.assets.json') -Raw | ConvertFrom-Json
$packageCache = Join-Path $projectRoot '.tools\nuget'
$components = @()
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $folder = Join-Path $packageCache $library.Value.path
    $nuspec = Get-ChildItem -LiteralPath $folder -Filter '*.nuspec' | Select-Object -First 1
    if (!$nuspec) { throw "Missing dependency metadata: $($library.Name)" }
    $safeName = $library.Name.Replace('/','-')
    Copy-Item -LiteralPath $nuspec.FullName -Destination (Join-Path $licenses "$safeName.nuspec")
    [xml]$metadata = Get-Content -LiteralPath $nuspec.FullName -Raw
    $license = $metadata.package.metadata.license
    $components += "$($library.Name) | $($license.InnerText) | $($metadata.package.metadata.licenseUrl)"
    if ($license.type -eq 'file') {
        $licenseFile = Join-Path $folder $license.InnerText
        if (!(Test-Path -LiteralPath $licenseFile)) { throw "Missing license: $licenseFile" }
        Copy-Item -LiteralPath $licenseFile -Destination (Join-Path $licenses "$safeName-license.txt")
    }
    foreach ($notice in Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Name -match '(LICENSE|NOTICE)' }) {
        Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenses "$safeName-$($notice.Name)") -Force
    }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\portable-licenses\Apache-2.0.txt') -Destination $licenses
$runtimeConfig = Get-Content -LiteralPath (Join-Path $stage 'QNAgent.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
    $id = ($framework.name + '.Runtime.win-x64').ToLowerInvariant()
    $folder = Join-Path $packageCache "$id\$($framework.version)"
    foreach ($notice in Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Name -match '(LICENSE|NOTICE|\.nuspec$)' }) {
        Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenses "$id-$($framework.version)-$($notice.Name)")
    }
    $components += "$id/$($framework.version) | See accompanying runtime license and notices."
}
$components += 'Visual C++ release runtime: Microsoft Corporation; app-local redistribution.'
$components += 'https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files'
[IO.File]::WriteAllLines((Join-Path $licenses 'Components.txt'), $components, $utf8)
[IO.File]::WriteAllText((Join-Path $stage '版本信息.txt'), "QN Agent portable test $Version`r`nWindows x64 / self-contained .NET 8`r`nIncludes current conversation recognition fixes, stable shop IDs, business backup/restore and human-approved learning.`r`nNo API keys, user settings, product data, chat, screenshots, learning samples or learned rules included.`r`nData directory: Data (created on first launch).`r`n", $utf8)

# Fail closed if a publish rule accidentally adds private files or development sources.
foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {
    $relative = $file.FullName.Substring($stage.Length + 1)
    if ($relative -match '(^|\\)(Data|Output|SmokeData|windows|captures|reviews|reply-index|knowledge-advice|business-backups|learning|\.git|\.tools)(\\|$)' -or
        $file.Extension -in @('.pdb','.cs','.db','.sqlite','.sqlite3','.log','.jsonl','.png','.jpg','.env','.ps1','.vbs','.qnbak','.bak','.zip','.7z','.rar')) {
        throw "Disallowed package content: $relative"
    }
    if ($file.Extension -eq '.json' -and $file.Name -notmatch '\.(deps|runtimeconfig)\.json$') { throw "Unexpected JSON: $relative" }
}
$manifest = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ Path = $_.FullName.Substring($stage.Length + 1).Replace('\','/'); Bytes = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
[IO.File]::WriteAllText((Join-Path $stage '文件校验清单.txt'), ($manifest | ConvertTo-Json -Depth 3), $utf8)
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
[IO.File]::WriteAllText("$archive.sha256", "$hash  $packageName.zip`r`n", $utf8)
Write-Output "PACKAGE=$archive"
Write-Output "STAGE=$stage"
Write-Output "SHA256=$hash"
Write-Output "SIZE_MB=$([math]::Round((Get-Item -LiteralPath $archive).Length / 1MB, 1))"
