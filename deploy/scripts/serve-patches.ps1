# serve-patches.ps1 - Simple HTTP file server for Mimir patch files.
# Serves everything under C:\patches\ on port 80.

$port = 80
$root = 'C:\patches'

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://+:$port/")
$listener.Start()

Write-Host "Patch server listening on port $port, serving $root"

while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    $req = $ctx.Request
    $resp = $ctx.Response

    $relPath = $req.Url.AbsolutePath.TrimStart('/').Replace('/', '\')
    $filePath = Join-Path $root $relPath

    if (Test-Path $filePath -PathType Leaf) {
        $bytes = [System.IO.File]::ReadAllBytes($filePath)
        $resp.ContentLength64 = $bytes.Length
        $resp.ContentType = switch ([System.IO.Path]::GetExtension($filePath).ToLower()) {
            '.json' { 'application/json' }
            '.zip'  { 'application/zip' }
            default { 'application/octet-stream' }
        }
        $resp.StatusCode = 200
        $resp.OutputStream.Write($bytes, 0, $bytes.Length)
        Write-Host "200 $($req.Url.AbsolutePath) ($($bytes.Length) bytes)"
    }
    else {
        $resp.StatusCode = 404
        $body = [System.Text.Encoding]::UTF8.GetBytes("Not found: $($req.Url.AbsolutePath)")
        $resp.ContentLength64 = $body.Length
        $resp.OutputStream.Write($body, 0, $body.Length)
        Write-Host "404 $($req.Url.AbsolutePath)"
    }

    $resp.OutputStream.Close()
}
