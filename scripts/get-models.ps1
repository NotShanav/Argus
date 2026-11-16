# Downloads the two face models used by Argus and verifies their SHA-256.
# Called automatically by the build when the models are missing.
param(
  [string]$Destination = (Join-Path $PSScriptRoot "..\src\Argus\models")
)
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

# Plain .NET hashing: works even when the PowerShell utility module cannot be loaded (e.g. CI runners)
function Get-Sha256([string]$Path) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $fs = [System.IO.File]::OpenRead($Path)
  try { return ([BitConverter]::ToString($sha.ComputeHash($fs)) -replace '-', '').ToUpper() }
  finally { $fs.Dispose(); $sha.Dispose() }
}

$models = @(
  @{ Name = "face_detection_yunet_2023mar.onnx"
     Url  = "https://huggingface.co/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx"
     Sha256 = "8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4" },
  @{ Name = "face_recognition_sface_2021dec.onnx"
     Url  = "https://huggingface.co/opencv/face_recognition_sface/resolve/main/face_recognition_sface_2021dec.onnx"
     Sha256 = "0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79" }
)

foreach ($m in $models) {
  $dest = Join-Path $Destination $m.Name
  if (Test-Path $dest) {
    if ((Get-Sha256 $dest) -eq $m.Sha256.ToUpper()) { Write-Host "OK (cached) $($m.Name)"; continue }
    Remove-Item $dest -Force
  }
  Write-Host "Downloading $($m.Name) ..."
  $ProgressPreference = "SilentlyContinue"
  Invoke-WebRequest -Uri $m.Url -OutFile $dest -UseBasicParsing
  $h = Get-Sha256 $dest
  if ($h -ne $m.Sha256.ToUpper()) { Remove-Item $dest -Force; throw "Checksum mismatch for $($m.Name): $h" }
  Write-Host ("  OK {0:N0} bytes" -f ([System.IO.FileInfo]$dest).Length)
}
