using System.IO.Compression;
using System.Text;

namespace PrevisaoPrecosImoveis.Core.DataCollection;

/// <summary>
/// Detecta downloads incompletos, páginas de erro e arquivos corrompidos
/// retornados por APIs e portais públicos.
/// </summary>
internal static class ArquivoFontePublica
{
    private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

    public static bool EhXlsxUtilizavel(string caminho)
    {
        try
        {
            using var stream = new FileStream(
                caminho,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            return EhXlsxUtilizavel(stream);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool EhXlsxUtilizavel(Stream stream)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("O stream precisa permitir busca para validar o XLSX.", nameof(stream));
        }

        // ZIP mínimo tem 22 bytes (EOCD sem comentário).
        if (stream.Length < 22)
        {
            return false;
        }

        stream.Position = 0;
        var assinatura = new byte[2];
        if (stream.Read(assinatura, 0, 2) != 2 ||
            assinatura[0] != (byte)'P' ||
            assinatura[1] != (byte)'K')
        {
            return false;
        }

        if (!PossuiFimDeDiretorioCentralZip(stream))
        {
            return false;
        }

        stream.Position = 0;
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            return zip.Entries.Any(entrada =>
                entrada.FullName.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public static bool EhJsonUtilizavel(string conteudo)
    {
        if (string.IsNullOrWhiteSpace(conteudo) || PareceHtml(conteudo))
        {
            return false;
        }

        var inicio = RemoverBom(conteudo.AsSpan()).TrimStart();
        return inicio.Length > 0 && (inicio[0] is '[' or '{');
    }

    public static bool EhCsvUtilizavel(string caminho)
    {
        try
        {
            using var stream = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == 0)
            {
                return false;
            }

            var buffer = new byte[(int)Math.Min(stream.Length, 512)];
            var lidos = stream.Read(buffer, 0, buffer.Length);
            var texto = Latin1.GetString(buffer, 0, lidos);
            return !PareceHtml(texto);
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static bool PareceHtml(string conteudo)
    {
        var inicio = RemoverBom(conteudo.AsSpan()).TrimStart();

        return inicio.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
               inicio.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
               inicio.StartsWith("<head", StringComparison.OrdinalIgnoreCase) ||
               inicio.StartsWith("<body", StringComparison.OrdinalIgnoreCase) ||
               inicio.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) &&
               inicio.Contains("<html", StringComparison.OrdinalIgnoreCase);
    }

    public static void TentarExcluir(string caminho)
    {
        try
        {
            if (File.Exists(caminho))
            {
                File.Delete(caminho);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static async Task SubstituirAtomicamenteAsync(
        string destino,
        string conteudo,
        CancellationToken cancellationToken)
    {
        var temporario = $"{destino}.{Guid.NewGuid():N}.tmp";
        try
        {
            var pasta = Path.GetDirectoryName(destino);
            if (!string.IsNullOrEmpty(pasta))
            {
                Directory.CreateDirectory(pasta);
            }

            await File.WriteAllTextAsync(temporario, conteudo, cancellationToken);
            File.Move(temporario, destino, overwrite: true);
        }
        finally
        {
            TentarExcluir(temporario);
        }
    }

    private static ReadOnlySpan<char> RemoverBom(ReadOnlySpan<char> texto)
    {
        return texto.StartsWith("\uFEFF") ? texto[1..] : texto;
    }

    private static bool PossuiFimDeDiretorioCentralZip(Stream stream)
    {
        // O comentário do ZIP pode ter até 65535 bytes; o EOCD fica no fim do arquivo.
        var tamanhoScan = (int)Math.Min(stream.Length, 65557);
        stream.Position = stream.Length - tamanhoScan;
        var buffer = new byte[tamanhoScan];
        var lidos = stream.Read(buffer, 0, tamanhoScan);

        for (var i = lidos - 22; i >= 0; i--)
        {
            if (buffer[i] == (byte)'P' &&
                buffer[i + 1] == (byte)'K' &&
                buffer[i + 2] == 5 &&
                buffer[i + 3] == 6)
            {
                return true;
            }
        }

        return false;
    }
}
