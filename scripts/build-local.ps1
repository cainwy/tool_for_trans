$ErrorActionPreference = 'Stop'

$Project = Join-Path $PSScriptRoot '..\src\ClipTranslateOnce\ClipTranslateOnce.csproj'
$OutRoot = Join-Path $PSScriptRoot '..\artifacts'

Remove-Item $OutRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $OutRoot | Out-Null

dotnet restore $Project
dotnet build $Project -c Release --no-restore

dotnet publish $Project `
  -c Release `
  -r win-x64 `
  --self-contained false `
  /p:PublishSingleFile=true `
  /p:DebugType=None `
  /p:DebugSymbols=false `
  -o (Join-Path $OutRoot 'ClipTranslateOnce-win-x64-framework-dependent')

dotnet publish $Project `
  -c Release `
  -r win-x64 `
  --self-contained true `
  /p:PublishSingleFile=true `
  /p:DebugType=None `
  /p:DebugSymbols=false `
  -o (Join-Path $OutRoot 'ClipTranslateOnce-win-x64-self-contained')
