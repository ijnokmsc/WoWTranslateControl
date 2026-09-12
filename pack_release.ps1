
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

# 优先 7-Zip（若已安装），否则用系统 Compress-Archive
$seven = Get-Command 7z -ErrorAction SilentlyContinue
if (-not $seven) {
    $p7 = Join-Path $env:ProgramFiles '7-Zip\7z.exe'
    if (Test-Path $p7) { $seven = Get-Command $p7 }
}
if ($seven) {
    $listFile = Join-Path $env:TEMP ('wtc-pack-' + [guid]::NewGuid().ToString('N') + '.list')
    $files | ForEach-Object { $_.FullName } | Set-Content -LiteralPath $listFile -Encoding UTF8
    & $seven.Source a -tzip $outZip ("@" + $listFile) | Out-Null
    Remove-Item $listFile -Force -ErrorAction SilentlyContinue
} else {
    Compress-Archive -Path ($files | ForEach-Object { $_.FullName }) -DestinationPath $outZip -CompressionLevel Optimal
}

$mb = [math]::Round((Get-Item $outZip).Length / 1MB, 1)
Write-Output ("打包完成: {0}  ({1} 个文件, {2} MB)" -f $outZip, $files.Count, $mb)
