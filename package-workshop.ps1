[CmdletBinding()]
param(
    [string] = (Split-Path -Parent System.Management.Automation.InvocationInfo.MyCommand.Path),
    [string] = 'RangeCrafting.dll',
    [string] = (Join-Path (Split-Path -Parent System.Management.Automation.InvocationInfo.MyCommand.Path) 'rangecrafting-workshop.zip')
)

 = 'RangeCrafting'
 = Join-Path K:\temp ("{0}-{1}" -f , [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path  -Force | Out-Null

try {
    Copy-Item -Path (Join-Path  'ModInfo.xml') -Destination (Join-Path  'ModInfo.xml') -ErrorAction Stop
    Copy-Item -Path (Join-Path  'README.md') -Destination (Join-Path  'README.md')

     = Join-Path  
    if (-not (Test-Path )) {
        throw "Missing assembly ''. Build the project first and ensure the output DLL matches this name."
    }
    Copy-Item -Path  -Destination (Join-Path  )

     = Join-Path  'config.json'
    if (Test-Path ) {
        Copy-Item -Path  -Destination (Join-Path  'config.json')
    }

    if (Test-Path ) {
        Remove-Item  -Force
    }
    Compress-Archive -Path (Join-Path  '*') -DestinationPath  -Force
    Write-Host "Created workshop package: "
}
finally {
    Remove-Item -Recurse -Force 
}