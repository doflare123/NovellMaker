function Get-VerifiedArchive {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Destination,
          [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$Sha256,
          [Parameter(Mandatory)][uri[]]$Sources)

    $ErrorActionPreference = 'Stop'
    $Destination = [IO.Path]::GetFullPath($Destination)
    if ((Test-Path -LiteralPath $Destination -PathType Leaf) -and
        (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -eq $Sha256) { return }
    $parent = [IO.Path]::GetDirectoryName($Destination)
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $failures = [Collections.Generic.List[string]]::new()
    foreach ($source in $Sources) {
        if ($source.Scheme -ne 'https') { throw 'Archive downloads require HTTPS.' }
        $temporary = Join-Path $parent ('archive-download-' + [guid]::NewGuid().ToString('N') + '.tmp')
        try {
            Write-Host "Downloading verified archive from $source"
            # Let SourceForge issue fresh signed mirror redirects; don't hardcode their transient target.
            $response = Invoke-WebRequest -Uri $source -OutFile $temporary -PassThru -TimeoutSec 30 `
                -UserAgent 'Wget/1.21.4' -MaximumRedirection 10
            $actual = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash
            if ($actual -ne $Sha256) {
                $length = (Get-Item -LiteralPath $temporary).Length
                $contentType = [string]$response.Headers['Content-Type']
                throw "SHA-256 mismatch: expected $Sha256, received $actual ($length bytes, $contentType)."
            }
            Move-Item -LiteralPath $temporary -Destination $Destination -Force
            return
        }
        catch {
            $detail = "${source}: $($_.Exception.Message)"
            $failures.Add($detail)
            Write-Warning "Archive source rejected; trying the next source. $detail"
        }
        finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
    }
    throw "All archive sources failed; no unverified archive was accepted.`n$($failures -join "`n")"
}
