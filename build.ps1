# 建置單一執行檔：publish\AutoDaily.exe
$ErrorActionPreference = 'Stop'
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe' }

& $dotnet publish "$PSScriptRoot\AutoDaily\AutoDaily.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o "$PSScriptRoot\publish"
