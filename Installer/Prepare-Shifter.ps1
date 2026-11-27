[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cache = (New-Item -ItemType Directory -Path (Join-Path $root 'obj\shifter-assets') -Force).FullName
$bundle = (New-Item -ItemType Directory -Path $Destination -Force).FullName
$weights = 'https://huggingface.co/Qwen/Qwen3-VL-2B-Instruct-GGUF/resolve/52d6c8ffea26cc873ac5ad116f8631268d7eb503'
$assets = @(
    @{
        Name = 'llama.zip'
        Url = 'https://github.com/ggml-org/llama.cpp/releases/download/b12078/llama-b12078-bin-win-cpu-x64.zip'
        Sha = '923baa2c55b45c7b312daf340e923d80f66faab36721aaf53313bb21c21baadd'
    },
    @{
        Name = 'model.gguf'
        Url = "$weights/Qwen3VL-2B-Instruct-Q4_K_M.gguf"
        Sha = '089d75c52f4b7ffc56ba998ffc50aae89fcafc755f9e7208aacca281dca6c2ae'
    },
    @{
        Name = 'vision.gguf'
        Url = "$weights/mmproj-Qwen3VL-2B-Instruct-Q8_0.gguf"
        Sha = 'f9a68fabba69c3b81e153367b2c7521030b0fa8bb0de400c9599c8e6725f9c82'
    },
    @{
        Name = 'llama-LICENSE.txt'
        Url = 'https://raw.githubusercontent.com/ggml-org/llama.cpp/b12078/LICENSE'
        Sha = '94f29bbed6a22c35b992c5c6ebf0e7c92f13b836b90f36f461c9cf2f0f1d010d'
    },
    @{
        Name = 'qwen-LICENSE.txt'
        Url = 'https://www.apache.org/licenses/LICENSE-2.0.txt'
        Sha = 'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30'
    },
    @{
        Name = 'vc_redist.x64.exe'
        Url = 'https://download.visualstudio.microsoft.com/download/pr/78d061c5-9ef3-42ab-98ed-3309d1095dc4/9C02C9ED120F671B900E6B9471D890E3B2414117A7E53F4AE2E855466FA2B7C9/VC_redist.x64.exe'
        Sha = '9c02c9ed120f671b900e6b9471d890e3b2414117a7e53f4ae2e855466fa2b7c9'
    }
)

foreach ($asset in $assets) {
    $cached = Join-Path $cache $asset.Name
    if (-not (Test-Path -LiteralPath $cached -PathType Leaf) -or
        (Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash -ne $asset.Sha) {
        $partial = "$cached.$([Guid]::NewGuid().ToString('N')).partial"
        try {
            Write-Host "Downloading Shifter asset $($asset.Name)"
            & curl.exe -fsSL --retry 2 --max-time 1800 --proto '=https' --proto-redir '=https' --output $partial $asset.Url
            if ($LASTEXITCODE -ne 0) {
                throw "Could not download $($asset.Name) (curl exit $LASTEXITCODE)."
            }
            if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $asset.Sha) {
                throw "SHA-256 mismatch for $($asset.Name)."
            }
            Move-Item -LiteralPath $partial -Destination $cached -Force
        } finally {
            if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
        }
    }
    if ($asset.Name -ne 'llama.zip') {
        Copy-Item -LiteralPath $cached -Destination $bundle -Force
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $cache 'llama.zip'))
try {
    foreach ($entry in $archive.Entries) {
        if ($entry.Name -match '^(ggml(?:-[\w-]+)?|llama|llama-common|llama-server-impl|libomp|mtmd)\.dll$' -or
            $entry.Name -in 'llama-server.exe', 'LICENSE-LLVM-OpenMP') {
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $bundle $entry.Name), $true)
        }
    }
} finally {
    $archive.Dispose()
}

if (-not (Test-Path -LiteralPath (Join-Path $bundle 'llama-server.exe') -PathType Leaf)) {
    throw 'The verified archive did not contain llama-server.exe.'
}
