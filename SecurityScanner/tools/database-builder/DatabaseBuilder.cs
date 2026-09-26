#:property LangVersion=latest
#:property Nullable=enable
#:package System.IO.Compression.ZipFile@4.3.0

// DatabaseBuilder — сборка подписанных offline-пакетов базы сигнатур
// (документ п. 12/30/33). Приватный ключ живёт ТОЛЬКО здесь (tools/),
// в приложении и ScannerCore — только публичный.
//
// Команды:
//   dotnet run DatabaseBuilder.cs -- genkeys <output-dir>
//   dotnet run DatabaseBuilder.cs -- build --key <private.pem> --hashes <hashes.txt>
//                                   --version 2026.09.25 --out <package.zip>
//
// Формат пакета (ZIP): hashes.txt (sha256<TAB>verdict<TAB>name), hashes.txt.sig
// (ECDSA P-256 подпись SHA-256(hashes.txt), r||s 64 байта), db-version.json.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: genkeys <dir> | build --key <pem> --hashes <txt> --version <v> --out <zip>");
    return 2;
}

switch (args[0])
{
    case "genkeys": return RunGenKeys(args);
    case "build": return RunBuild(args);
    default:
        Console.Error.WriteLine($"unknown command {args[0]}");
        return 2;
}

static int RunGenKeys(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("genkeys: missing output dir");
        return 2;
    }

    using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var parameters = ecdsa.ExportExplicitParameters(includePrivateParameters: true);
    var x = Convert.ToHexString(parameters.Q.X).ToLowerInvariant();
    var y = Convert.ToHexString(parameters.Q.Y).ToLowerInvariant();

    Directory.CreateDirectory(args[1]);
    File.WriteAllText(Path.Combine(args[1], "db-signing-private.pem"), ecdsa.ExportECPrivateKeyPem());
    File.WriteAllText(Path.Combine(args[1], "db-signing-public.pem"), ecdsa.ExportSubjectPublicKeyInfoPem());

    Console.WriteLine("PUBLIC_XY=" + x + y);
    Console.WriteLine("keys written to " + args[1]);
    return 0;
}

static int RunBuild(string[] args)
{
    string? keyPath = null, hashesPath = null, version = null, outputPath = null;
    for (var i = 1; i < args.Length - 1; i += 2)
    {
        switch (args[i])
        {
            case "--key": keyPath = args[i + 1]; break;
            case "--hashes": hashesPath = args[i + 1]; break;
            case "--version": version = args[i + 1]; break;
            case "--out": outputPath = args[i + 1]; break;
        }
    }

    if (keyPath is null || hashesPath is null || version is null || outputPath is null)
    {
        Console.Error.WriteLine("build: --key, --hashes, --version and --out are required");
        return 2;
    }

    var hashesBytes = File.ReadAllBytes(hashesPath);
    var entries = CountEntries(hashesBytes);
    if (entries == 0)
    {
        Console.Error.WriteLine("build: hashes file is empty");
        return 2;
    }

    using var ecdsa = ECDsa.Create();
    ecdsa.ImportFromPem(File.ReadAllText(keyPath));

    var signature = ecdsa.SignData(hashesBytes, HashAlgorithmName.SHA256);
    if (signature.Length != 64)
    {
        Console.Error.WriteLine($"build: unexpected signature length {signature.Length}");
        return 2;
    }

    var dbVersionJson = new StringBuilder()
        .Append("{\n")
        .Append("  \"version\": \"").Append(version).Append("\",\n")
        .Append("  \"date\": \"").Append(DateTime.UtcNow.ToString("yyyy-MM-dd")).Append("\",\n")
        .Append("  \"entries\": ").Append(entries).Append("\n")
        .Append("}\n")
        .ToString();

    using var stream = File.Create(outputPath);
    using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
    WriteEntry(zip, "hashes.txt", hashesBytes);
    WriteEntry(zip, "hashes.txt.sig", signature);
    WriteEntry(zip, "db-version.json", Encoding.UTF8.GetBytes(dbVersionJson));

    Console.WriteLine($"package written: {outputPath} ({entries} entries, version {version})");
    return 0;
}

static void WriteEntry(ZipArchive zip, string name, byte[] content)
{
    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
    using var entryStream = entry.Open();
    entryStream.Write(content);
}

static int CountEntries(byte[] bytes)
{
    var count = 0;
    foreach (var b in bytes)
    {
        if (b == (byte)'\n')
        {
            count++;
        }
    }

    return count;
}
