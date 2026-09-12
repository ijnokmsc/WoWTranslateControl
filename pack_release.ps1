
# 打包发布脚本：把本目录打包成 zip（剔除用户运行数据与构建残留）
# 用法：powershell -ExecutionPolicy Bypass -File .\打包发布.ps1
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
if (-not $src) { $src = Split-Path -Parent $MyInvocation.MyCommand.Path }

# 版本号取自主程序的 ProductVersion（随 csproj <Version> 变化）
$ver = (Get-Item (Join-Path $src 'WoWTranslateControl.exe')).VersionInfo.ProductVersion
$ver = ($ver -split '\+')[0]   # 去掉 SourceRevisionId 追加的 git 哈希
if (-not $ver) { $ver = '2.1.1' }

# ---- 排除规则：用户运行数据 / 构建残留 / 旧压缩包 ----
$excludeDirs  = @('llama.cpp')
$excludeFiles = @('settings.json', 'cache.json', 'proxy_traffic.log', 'crash.log')
$excludeExt   = @('.tmp', '.pdb', '.zip', '.7z')

$files = Get-ChildItem -LiteralPath $src -Recurse -File | Where-Object {
    $rel = $_.FullName.Substring($src.Length).TrimStart('\')
    if ($excludeDirs -contains $rel.Split('\')[0]) { return $false }
    if ($excludeFiles -contains $_.Name) { return $false }
    if ($excludeExt -contains $_.Extension.ToLower()) { return $false }
    if ($_.Name -like 'WoWTranslateControl-v*.zip') { return $false }
    return $true
}

$outZip = Join-Path (Split-Path -Parent $src) ("WoWTranslateControl-v" + $ver + ".zip")
if (Test-Path $outZip) { Remove-Item $outZip -Force }

# 暂存目录保持相对目录结构（Compress-Archive 直接传文件清单会扁平化，
# 导致 assets\direct-dll\ 变成根目录散文件——Track B 资产检测会失效）
$stage = Join-Path $env:TEMP ('wtc-stage-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
foreach ($f in $files) {
    $rel = $f.FullName.Substring($src.Length).TrimStart('\')
    $dstDir = Split-Path -Parent (Join-Path $stage $rel)
    if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
    Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $stage $rel) -Force
}

# 用 .NET ZipFile 并强制 UTF-8 条目名编码（Compress-Archive 写 UTF-8 字节
# 却不设 UTF-8 标志，中文文件名在解压时会变乱码）
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $outZip,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false,   # 包含 staging 目录名本身？否——zip 根 = 发布根
    [System.Text.Encoding]::UTF8)
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

$mb = [math]::Round((Get-Item $outZip).Length / 1MB, 1)
Write-Output ("打包完成: {0}  ({1} 个文件, {2} MB)" -f $outZip, $files.Count, $mb)
