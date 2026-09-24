#Requires -Version 5.1
<#
.SYNOPSIS
    Alias for quarry.ps1 - kept so old habits keep working.

.EXAMPLE
    .\download.ps1 "https://www.erome.com/a/abc123"
#>
# Every argument (named flags included) is forwarded straight to quarry.ps1.
& "$PSScriptRoot\quarry.ps1" @args
exit $LASTEXITCODE
