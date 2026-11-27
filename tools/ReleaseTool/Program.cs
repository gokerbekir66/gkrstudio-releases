// GKR Studio release tool
//   keygen  <keyDir>                                  → private.pem (keep secret!) + public.txt (embed in Updater.PublicKey)
//   publish <version> <notes> <key.pem> <outDir> <platform=zip>...
//           platform: windows-x64 | macos-arm64 | macos-x64
//           → copies packages to outDir, writes update.json (sha256 + relative urls) and update.json.sig
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length == 0) return Usage();
switch (args[0])
{
    case "keygen":
    {
        if (args.Length < 2) return Usage();
        Directory.CreateDirectory(args[1]);
        string priv = Path.Combine(args[1], "private.pem");
        if (File.Exists(priv)) { Console.Error.WriteLine("private.pem already exists — refusing to overwrite."); return 1; }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(priv, key.ExportPkcs8PrivateKeyPem());
        string pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        File.WriteAllText(Path.Combine(args[1], "public.txt"), pub);
        Console.WriteLine(pub);
        return 0;
    }
    case "publish":
    {
        if (args.Length < 6) return Usage();
        string version = args[1], notes = args[2], keyFile = args[3], outDir = args[4];
        Directory.CreateDirectory(outDir);
        var platforms = new Dictionary<string, object>();
        foreach (var spec in args.Skip(5))
        {
            var (platform, zip) = (spec[..spec.IndexOf('=')], spec[(spec.IndexOf('=') + 1)..]);
            string name = Path.GetFileName(zip);
            string target = Path.Combine(outDir, name);
            if (!Path.GetFullPath(zip).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) File.Copy(zip, target, overwrite: true);
            using var fs = File.OpenRead(target);
            platforms[platform] = new { url = name, sha256 = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant() };
            Console.WriteLine($"{platform}: {name}");
        }
        var manifest = new { version, notes, published = DateTime.UtcNow.ToString("u"), platforms };
        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(keyFile));
        string sig = Convert.ToBase64String(key.SignData(json, HashAlgorithmName.SHA256));
        File.WriteAllBytes(Path.Combine(outDir, "update.json"), json);
        File.WriteAllText(Path.Combine(outDir, "update.json.sig"), sig);
        Console.WriteLine($"update.json + update.json.sig → {outDir}");
        return 0;
    }
    case "zip-mac":
    {
        // zip-mac <folder> <out.zip>: zips a folder keeping Unix permissions (0755 for executables in */MacOS/ and *.command / *.sh)
        if (args.Length < 3) return Usage();
        string root = Path.GetFullPath(args[1]);
        if (File.Exists(args[2])) File.Delete(args[2]);
        using var zip = System.IO.Compression.ZipFile.Open(args[2], System.IO.Compression.ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var entry = zip.CreateEntryFromFile(file, rel, System.IO.Compression.CompressionLevel.Optimal);
            bool exec = rel.Contains("/Contents/MacOS/") || rel.EndsWith(".command") || rel.EndsWith(".sh") || rel.EndsWith(".dylib");
            int mode = exec ? 0b111_101_101 : 0b110_100_100; // 0755 / 0644
            entry.ExternalAttributes = (0x8000 | mode) << 16;  // S_IFREG | mode
        }
        Console.WriteLine("zip → " + args[2]);
        return 0;
    }
    default:
        return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("ReleaseTool keygen <keyDir> | publish <version> <notes> <key.pem> <outDir> <platform=zip>...");
    return 2;
}
