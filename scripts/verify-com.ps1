#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$clsid = [Guid]'{87F8A6BB-6B13-4A41-9D54-EEB39DBD1D6E}'
$type = [Type]::GetTypeFromCLSID($clsid, $true)
$instance = [Activator]::CreateInstance($type)
if ($null -eq $instance) { throw 'COM activation returned null.' }
Write-Host "COM activation succeeded: $($instance.GetType().FullName)" -ForegroundColor Green
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($instance)
