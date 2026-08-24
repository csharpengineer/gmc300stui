$ErrorActionPreference = 'Stop'

Write-Host 'Restoring packages...'
dotnet restore

Write-Host 'Building Release...'
dotnet build -c Release

Write-Host 'Publishing self-contained Windows x64 single-file executable...'
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

Write-Host ''
Write-Host 'Done:'
Write-Host '  bin\Release\net8.0\win-x64\publish\gmc300s-tui.exe'
