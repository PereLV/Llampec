param([Parameter(Mandatory)][ValidateSet('win-arm64', 'win-x64')][string]$RuntimeIdentifier)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
$name = "Llampec-$version-$RuntimeIdentifier"
$output = Join-Path $root "publish/$name"

# Never package on top of an older build: stale files could leak into the release.
if (Test-Path -LiteralPath $output) { throw "Output already exists: $output. Choose a clean publication directory." }
dotnet publish (Join-Path $root 'src/Llampec.App') -c Release -r $RuntimeIdentifier --self-contained false -p:DebugType=None -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publication failed.' }
& (Join-Path $PSScriptRoot 'test-publish.ps1') -PublishDirectory $output
Copy-Item -LiteralPath (Join-Path $root 'LICENSE'), (Join-Path $root 'README.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $output -Recurse

# Preserve the original license and notice files from restored dependencies.
$assets = Get-Content -Raw -LiteralPath (Join-Path $root 'src/Llampec.App/obj/project.assets.json') | ConvertFrom-Json
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    foreach ($packageRoot in $assets.packageFolders.PSObject.Properties.Name) {
        $package = Join-Path $packageRoot $library.Value.path
        if (!(Test-Path -LiteralPath $package)) { continue }
        foreach ($file in $library.Value.files) {
            if ($file -notmatch '(?i)(^|/)(licen[cs]e[^/]*|notice[^/]*|third[-_. ]?party[^/]*)(\.(txt|md|rtf))?$') { continue }
            $source = Join-Path $package $file
            if (!(Test-Path -LiteralPath $source -PathType Leaf)) { continue }
            $destination = Join-Path $output "licenses/$($library.Value.path)/$file"
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination
        }
        break
    }
}
$archive = "$output.zip"
Compress-Archive -Path "$output/*" -DestinationPath $archive -CompressionLevel Optimal
Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
