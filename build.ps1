$ErrorActionPreference = "Stop"

$binDir = "Source/PiDbg.VsCodeExtension/bin"
New-Item -ItemType Directory -Force -Path $binDir | Out-Null
Get-ChildItem $binDir -Force | Where-Object { $_.Name -ne ".gitkeep" } | Remove-Item -Recurse -Force

# Meadow.Daemon is built via a nested MSBuild <Publish> call from PiDbg.DebugAdapter's
# PackageDaemon target, which does not perform an implicit restore - it must be restored
# up front for the same RID it will be published with.
dotnet restore Source/Meadow.Daemon/Meadow.Daemon.csproj -r linux-arm64

dotnet publish Source/PiDbg.DebugAdapter/PiDbg.DebugAdapter.csproj -c Release -r win-x64 --self-contained true -o $binDir

Push-Location Source/PiDbg.VsCodeExtension
try {
    npm install
    npm run compile

    $version = node -p "require('./package.json').version"
    New-Item -ItemType Directory -Force -Path ../../dist | Out-Null
    npx vsce package --target win32-x64 --out "../../dist/pidbg-vscode-$version.vsix"
}
finally {
    Pop-Location
}
