$ErrorActionPreference = "Stop"

$binDir = "Source/PiDbg.VsCodeExtension/bin"
New-Item -ItemType Directory -Force -Path $binDir | Out-Null
Get-ChildItem $binDir -Force | Where-Object { $_.Name -ne ".gitkeep" } | Remove-Item -Recurse -Force

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
