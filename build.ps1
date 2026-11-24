# Thin wrapper over dotnet and Tools/Weft.Build. See build.sh for the commands.
param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Args)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (Test-Path .env) {
    Get-Content .env | Where-Object { $_ -match '^\s*[^#\s][^=]*=' } | ForEach-Object {
        $k, $v = $_ -split '=', 2
        [Environment]::SetEnvironmentVariable($k.Trim(), $v.Trim())
    }
}

$command = if ($Args.Count) { $Args[0] } else { 'build' }
switch ($command) {
    'build' { dotnet build Weft.slnx -c Debug }
    'test'  { dotnet test --solution Weft.slnx -c Debug }
    'native' { cmake -S Native -B Bin/native-build; cmake --build Bin/native-build --config Release }
    'aot'   {
        dotnet publish Source/Weft.Runtime -c Release -p:PublishAot=true -o Bin/publish/runtime
    }
    # The Viewer is a sample, run by hand: CI never publishes it.
    'aot-viewer' { dotnet publish Samples/Viewer -c Release -p:PublishAot=true -o Bin/publish/viewer }
    'pack'  { dotnet run --project Source/Weft.Runtime -c Release -- --pack @($Args | Select-Object -Skip 1) }
    default { dotnet run --project Tools/Weft.Build -- @Args }
}
if ($LASTEXITCODE) { exit $LASTEXITCODE }
