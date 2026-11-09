using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace P2PCall.Client.Update;

/// <summary>Release publicada no GitHub com o P2PCall.exe.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, Uri DownloadUrl, long Size, string? Sha256);

/// <summary>
/// Atualização automática: compara a versão local (guardada em <c>latest.json</c>, ao lado do exe)
/// com a última release do GitHub, baixa o novo exe e o instala depois que este processo termina.
/// </summary>
public static class UpdateService
{
    private const string ReleasesApi = "https://api.github.com/repos/ericoamico/p2p-call/releases/latest";
    private const string AssetName = "P2PCall.exe";
    private const string VersionFileName = "latest.json";

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Só o exe publicado (arquivo único) se atualiza. Builds de desenvolvimento (dotnet run/Visual
    /// Studio) têm o P2PCall.dll ao lado do exe. Não dá para usar Assembly.Location: com
    /// IncludeAllContentForSelfExtract o exe único extrai as DLLs e ela deixa de ser vazia.
    /// </summary>
    public static bool IsSupported =>
        ExePath is not null && !File.Exists(Path.ChangeExtension(ExePath, ".dll"));

    private static string? ExePath => Environment.ProcessPath;

    private static string ExeDirectory => Path.GetDirectoryName(ExePath)!;

    /// <summary>Versão embutida no exe pela build (<c>-p:Version=1.0.N</c> no GitHub Actions).</summary>
    public static Version AssemblyVersion
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    /// <summary>
    /// Lê a versão local do latest.json e o mantém em dia: se o exe é mais novo que o registrado
    /// (acabou de ser atualizado ou foi baixado à mão), grava a versão do exe.
    /// </summary>
    public static Version SyncLocalVersion()
    {
        var recorded = ReadVersionFile();
        var current = AssemblyVersion;
        if (recorded is not null && recorded >= current) return recorded;

        WriteVersionFile(current);
        return current;
    }

    /// <summary>Retorna a release mais nova que a local, ou null se já está atualizado.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(Version localVersion, CancellationToken token)
    {
        var url = Environment.GetEnvironmentVariable("P2PCALL_RELEASES_API") ?? ReleasesApi; // permite testar com um servidor local
        using var response = await Http.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(token)
            ?? throw new InvalidDataException("Resposta vazia do GitHub.");

        if (release.Draft || release.Prerelease) return null;
        if (!Version.TryParse(release.TagName.TrimStart('v', 'V'), out var version)) return null;
        version = new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        if (version <= localVersion) return null;

        var asset = release.Assets.FirstOrDefault(a => a.Name.Equals(AssetName, StringComparison.OrdinalIgnoreCase));
        if (asset is null) return null;

        // O GitHub informa o SHA-256 de cada arquivo ("sha256:<hex>") para conferir o download.
        var sha = asset.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? asset.Digest[7..] : null;
        return new ReleaseInfo(version, release.TagName, new Uri(asset.BrowserDownloadUrl), asset.Size, sha);
    }

    /// <summary>
    /// Baixa o novo exe para a pasta do atual (P2PCall.exe.new), conferindo tamanho e SHA-256.
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double> progress, CancellationToken token)
    {
        var target = ExePath! + ".new";
        EnsureDirectoryWritable();

        try
        {
            using var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.Size;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    if (total > 0) progress.Report((double)done / total);
                }
            }

            var size = new FileInfo(target).Length;
            if (release.Size > 0 && size != release.Size)
                throw new InvalidDataException($"Arquivo incompleto ({size} de {release.Size} bytes).");

            var sha = Convert.ToHexString(hash.GetHashAndReset());
            if (release.Sha256 is not null && !sha.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("O arquivo baixado não confere com o publicado (SHA-256 diferente).");

            return target;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>
    /// Agenda a instalação: um script espera este processo terminar (e o encerra à força se demorar),
    /// substitui o exe pelo baixado e abre a nova versão. Quem chama deve fechar o aplicativo em seguida.
    /// </summary>
    public static void ApplyAndRestart(string downloadedFile)
    {
        var script = Path.Combine(Path.GetTempPath(), $"P2PCall-atualizar-{Environment.ProcessId}.ps1");
        // Com BOM: o Windows PowerShell 5.1 só lê caminhos acentuados corretamente assim.
        File.WriteAllText(script, ApplyScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var arg in new[]
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
            "-ProcessId", Environment.ProcessId.ToString(),
            "-Source", downloadedFile,
            "-Target", ExePath!,
        })
        {
            psi.ArgumentList.Add(arg);
        }
        Process.Start(psi);
    }

    /// <summary>Remove um download que sobrou de uma atualização que não chegou a ser instalada.</summary>
    public static void CleanupLeftovers()
    {
        if (ExePath is not null) TryDelete(ExePath + ".new");
    }

    // Executado depois que o aplicativo fecha. Registra o andamento em %TEMP%\P2PCall-atualizacao.log.
    private const string ApplyScript = """
        param([int]$ProcessId, [string]$Source, [string]$Target)
        $log = Join-Path $env:TEMP 'P2PCall-atualizacao.log'
        function Log($msg) { Add-Content -LiteralPath $log -Value ("{0:s} {1}" -f (Get-Date), $msg) }
        Log "Aguardando o processo $ProcessId encerrar"

        # O aplicativo fecha sozinho; se não fechar em 30 s, é encerrado à força.
        $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($proc -and -not $proc.WaitForExit(30000)) {
            Log 'Processo não encerrou; forçando'
            Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
            $proc.WaitForExit(10000) | Out-Null
        }

        # O antivírus ou o próprio Windows podem segurar o arquivo por alguns instantes.
        $ok = $false
        for ($i = 0; $i -lt 40 -and -not $ok; $i++) {
            try { Move-Item -LiteralPath $Source -Destination $Target -Force -ErrorAction Stop; $ok = $true }
            catch { Start-Sleep -Milliseconds 500 }
        }

        if ($ok) { Log "Instalado: $Target"; $flag = '--atualizado' }
        else { Log "Falha ao substituir $Target"; $flag = '--falha-atualizacao' }
        Start-Process -FilePath $Target -ArgumentList $flag -WorkingDirectory (Split-Path -LiteralPath $Target)
        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("P2PCall-Updater", AssemblyVersion.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    // ---------- latest.json ----------

    // Ao lado do exe; se a pasta não permitir escrita (ex.: Arquivos de Programas), em %LOCALAPPDATA%\P2PCall.
    private static IEnumerable<string> VersionFileCandidates()
    {
        if (ExePath is not null) yield return Path.Combine(ExeDirectory, VersionFileName);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "P2PCall", VersionFileName);
    }

    private static Version? ReadVersionFile()
    {
        foreach (var path in VersionFileCandidates())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var info = JsonSerializer.Deserialize<LocalVersionInfo>(File.ReadAllText(path));
                if (info is not null && Version.TryParse(info.Version, out var v)) return v;
            }
            catch
            {
                // arquivo corrompido: tenta o próximo e depois recria
            }
        }
        return null;
    }

    private static void WriteVersionFile(Version version)
    {
        var json = JsonSerializer.Serialize(new LocalVersionInfo(version.ToString(), $"v{version}", DateTimeOffset.Now), JsonOptions);
        foreach (var path in VersionFileCandidates())
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
                return;
            }
            catch
            {
                // sem permissão: tenta o próximo local
            }
        }
    }

    private static void EnsureDirectoryWritable()
    {
        var probe = Path.Combine(ExeDirectory, $".p2pcall-teste-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException(
                $"Sem permissão para gravar em {ExeDirectory}. Mova o P2PCall.exe para uma pasta sua (ex.: Downloads ou Documentos).", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* em uso ou inexistente */ }
    }

    private sealed record LocalVersionInfo(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("tag")] string Tag,
        [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    }
}
