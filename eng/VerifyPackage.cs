using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

const string ExpectedPackageId = "TemporalCommunity.Aspire.Hosting";
const string ExpectedRepositoryUrl = "https://github.com/temporal-community/temporal-aspire";
const string ExpectedSourcePrefix = "https://raw.githubusercontent.com/temporal-community/temporal-aspire/";
const string ExpectedAspireVersion = "13.6.1";
const string ExpectedHostingVersion = "10.0.12";
var sourceLinkKind = new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A");
var frameworks = new[] { "net10.0", "net9.0", "net8.0" };

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: dotnet run eng/VerifyPackage.cs -- <nupkg> <snupkg> <commit>");
    return 2;
}

var packagePath = Path.GetFullPath(args[0]);
var symbolsPath = Path.GetFullPath(args[1]);
var expectedCommit = args[2];

Require(File.Exists(packagePath), $"Package not found: {packagePath}");
Require(File.Exists(symbolsPath), $"Symbol package not found: {symbolsPath}");

using (var package = ZipFile.OpenRead(packagePath))
{
    var nuspecEntry = package.Entries.SingleOrDefault(entry =>
        entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)) ??
        throw new InvalidOperationException("The package has no .nuspec file.");

    using var nuspecStream = nuspecEntry.Open();
    var nuspec = XDocument.Load(nuspecStream);
    var metadata = nuspec.Descendants().Single(element => element.Name.LocalName == "metadata");
    var packageId = metadata.Elements().Single(element => element.Name.LocalName == "id").Value;
    var projectUrl = metadata.Elements().SingleOrDefault(element => element.Name.LocalName == "projectUrl")?.Value;
    var repository = metadata.Elements().Single(element => element.Name.LocalName == "repository");
    var repositoryUrl = repository.Attribute("url")?.Value;
    var repositoryCommit = repository.Attribute("commit")?.Value;

    Require(packageId == ExpectedPackageId, $"Unexpected package id: {packageId}");
    Require(projectUrl == ExpectedRepositoryUrl, $"Unexpected project URL: {projectUrl ?? "<missing>"}");
    Require(repositoryUrl == ExpectedRepositoryUrl, $"Unexpected repository URL: {repositoryUrl ?? "<missing>"}");
    Require(repositoryCommit == expectedCommit, $"Unexpected repository commit: {repositoryCommit ?? "<missing>"}");

    VerifyDependency(metadata, "Aspire.Hosting.AppHost", ExpectedAspireVersion);
    VerifyDependency(metadata, "Microsoft.Extensions.Hosting", ExpectedHostingVersion);

    foreach (var framework in frameworks)
        RequireEntry(package, $"lib/{framework}/{ExpectedPackageId}.dll");
    RequireEntry(package, "icon.png");
}

using (var symbols = ZipFile.OpenRead(symbolsPath))
{
    foreach (var framework in frameworks)
    {
        var pdbPath = $"lib/{framework}/{ExpectedPackageId}.pdb";
        var pdbEntry = symbols.GetEntry(pdbPath) ??
            throw new InvalidOperationException($"Missing symbol package entry: {pdbPath}");

        using var pdbStream = pdbEntry.Open();
        using var pdbBuffer = new MemoryStream();
        pdbStream.CopyTo(pdbBuffer);
        pdbBuffer.Position = 0;
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbBuffer);
        var reader = provider.GetMetadataReader();
        var sourceLinkJson = reader
            .GetCustomDebugInformation(MetadataTokens.EntityHandle(TableIndex.Module, 1))
            .Select(handle => reader.GetCustomDebugInformation(handle))
            .Where(info => reader.GetGuid(info.Kind) == sourceLinkKind)
            .Select(info => Encoding.UTF8.GetString(reader.GetBlobBytes(info.Value)))
            .SingleOrDefault();

        if (sourceLinkJson is null)
            throw new InvalidOperationException($"Missing Source Link record in {pdbPath}");
        using var sourceLink = JsonDocument.Parse(sourceLinkJson);
        var documentUrls = sourceLink.RootElement
            .GetProperty("documents")
            .EnumerateObject()
            .Select(property => property.Value.GetString())
            .ToList();

        Require(
            documentUrls.Any(url =>
                url is not null &&
                url.StartsWith(ExpectedSourcePrefix, StringComparison.Ordinal) &&
                url.Contains($"/{expectedCommit}/", StringComparison.Ordinal)),
            $"Source Link in {pdbPath} does not target {ExpectedRepositoryUrl} at {expectedCommit}.");
        Require(
            documentUrls.All(url => url is null || !url.Contains("temporal-dotnet-extensions", StringComparison.Ordinal)),
            $"Source Link in {pdbPath} still targets temporal-dotnet-extensions.");
    }
}

Console.WriteLine($"Verified package metadata, assets, and Source Link for {Path.GetFileName(packagePath)}.");
return 0;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void RequireEntry(ZipArchive archive, string path) =>
    Require(archive.GetEntry(path) is not null, $"Missing package entry: {path}");

static void VerifyDependency(XElement metadata, string packageId, string expectedVersion)
{
    var versions = metadata
        .Descendants()
        .Where(element =>
            element.Name.LocalName == "dependency" &&
            string.Equals(element.Attribute("id")?.Value, packageId, StringComparison.Ordinal))
        .Select(element => element.Attribute("version")?.Value)
        .ToList();

    Require(versions.Count > 0, $"Missing dependency: {packageId}");
    Require(
        versions.All(version => version == expectedVersion),
        $"Unexpected {packageId} dependency versions: {string.Join(", ", versions)}");
}
