# CellPort 本地打包脚本
# 用法: .\scripts\publish.ps1 [-Version 1.0.1] [-SkipSelfContained]
# 产物输出到仓库外 ..\CellPort-v<Version>-win-x64*.zip（与仓库同级目录）
param(
    [string]$Version = "1.0.1",
    [switch]$SkipSelfContained
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$OutDir = Split-Path -Parent $RepoRoot
$Dotnet = "dotnet"

Write-Host "==> CellPort v$Version 打包" -ForegroundColor Cyan
Write-Host "仓库: $RepoRoot"

Push-Location $RepoRoot
try {
    # 1) framework-dependent（需要 .NET 9 Desktop Runtime，体积小）
    Write-Host "`n==> [1/2] framework-dependent" -ForegroundColor Yellow
    & $Dotnet publish src/CellPort.App/CellPort.App.csproj -c Release -r win-x64 --self-contained false -o dist/framework-dependent
    if ($LASTEXITCODE -ne 0) { throw "publish framework-dependent 失败" }

    $fdZip = Join-Path $OutDir "CellPort-v$Version-win-x64.zip"
    if (Test-Path $fdZip) { Remove-Item $fdZip -Force }
    Compress-Archive -Path "dist/framework-dependent/*" -DestinationPath $fdZip -Force
    Write-Host "已生成: $fdZip" -ForegroundColor Green

    # 2) self-contained（免装运行时，体积大）
    if (-not $SkipSelfContained) {
        Write-Host "`n==> [2/2] self-contained" -ForegroundColor Yellow
        & $Dotnet publish src/CellPort.App/CellPort.App.csproj -c Release -r win-x64 --self-contained true -o dist/self-contained
        if ($LASTEXITCODE -ne 0) { throw "publish self-contained 失败" }

        $scZip = Join-Path $OutDir "CellPort-v$Version-win-x64-selfcontained.zip"
        if (Test-Path $scZip) { Remove-Item $scZip -Force }
        Compress-Archive -Path "dist/self-contained/*" -DestinationPath $scZip -Force
        Write-Host "已生成: $scZip" -ForegroundColor Green
    }

    Write-Host "`n==> 完成" -ForegroundColor Cyan
    Get-Item (Join-Path $OutDir "CellPort-v$Version*.zip") | Format-Table Name, @{L="Size(MB)";E={[math]::Round($_.Length/1MB,1)}} -AutoSize
}
finally {
    Pop-Location
}
