using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IsotopeProbe.Domain;
using YamlDotNet.RepresentationModel;

namespace IsotopeProbe.Profiles;

public sealed record ProfileDefinition(string Id, string Version, string Name, string Description, bool Enabled,
    string Root, string[] Files, string[] Exclusions, string? SourceVersion, string Restrictions = "http-get-v1");
public sealed record ProfileConfiguration(string StorageDirectory, ProfileDefinition[] Profiles);
public sealed record TemplateEntry(string Id, string Path, string Hash);
public sealed record TemplateManifest(string ProfileId, string Version, string Name, string? SourceVersion,
    string DefinitionHash, TemplateEntry[] Templates, DateTimeOffset CreatedAt);
public sealed record PreparedProfile(TemplateManifest Manifest, string Hash)
{
    public void Capture(ScanExecution execution)
    {
        execution.TemplateProfile = Manifest.Name; execution.ProfileId = Manifest.ProfileId;
        execution.ProfileVersion = Manifest.Version; execution.SnapshotHash = Hash;
        execution.TemplateCount = Manifest.Templates.Length; execution.TemplateSourceVersion = Manifest.SourceVersion;
    }
}

// Operator-owned storage must be shared by preparation, CLI and Web and retained indefinitely.
public sealed class ProfileCatalog
{
    private readonly ProfileConfiguration config;
    private readonly Action<string[]> validateEngine;
    public IReadOnlyList<ProfileDefinition> Definitions => config.Profiles;
    public string Storage { get; }
    public static ProfileCatalog FromEnvironment() => new(JsonSerializer.Deserialize<ProfileConfiguration>(
        File.ReadAllText(Environment.GetEnvironmentVariable("ISOTOPEPROBE_PROFILES") ?? "profiles.json"))
        ?? throw new ArgumentException("Invalid profile configuration."));
    public ProfileCatalog(ProfileConfiguration configuration) : this(configuration, Nuclei.NucleiRunner.ValidateTemplates) { }
    internal ProfileCatalog(ProfileConfiguration configuration, Action<string[]> validateEngine)
    {
        this.validateEngine = validateEngine;
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.StorageDirectory) || configuration.Profiles is null)
            throw new ArgumentException("Profile configuration requires durable storage and profile definitions.");
        config = configuration;
        if (config.Profiles.Any(p => p is null || p.Files is null || p.Exclusions is null))
            throw new ArgumentException("Profile definitions require files and exclusions arrays.");
        Storage = Expand(config.StorageDirectory);
        if (Storage.IndexOfAny([',', '\n', '\r']) >= 0) throw new ArgumentException("Snapshot storage path cannot contain commas or newlines.");
        if (config.Profiles.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != config.Profiles.Length)
            throw new ArgumentException("Duplicate profile IDs.");
        foreach (var p in config.Profiles)
        {
            if (!SafeName(p.Id) || !SafeName(p.Version) || string.IsNullOrWhiteSpace(p.Name) ||
                string.IsNullOrWhiteSpace(p.Description) || string.IsNullOrWhiteSpace(p.Root) ||
                p.Files.Length == 0 || p.Restrictions != "http-get-v1")
                throw new ArgumentException("Invalid profile definition; require identity, version, description, files and http-get-v1 restrictions.");
            if (Expand(p.Root).IndexOfAny([',', '\n', '\r']) >= 0) throw new ArgumentException("Template root cannot contain commas or newlines.");
            foreach (var path in p.Files.Concat(p.Exclusions)) Relative(path);
        }
    }
    private static bool SafeName(string? s) => s is not null && Regex.IsMatch(s, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,99}$");
    public static string Expand(string path) => Path.GetFullPath(path.StartsWith("~/", StringComparison.Ordinal)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : path);
    private static void Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') ||
            path.Split('/').Any(x => x is "" or "." or "..") || path.Contains(',') || path.Contains('\n'))
            throw new ArgumentException("Template paths must be relative files inside the trusted root.");
    }
    private static string Inside(string root, string relative)
    {
        Relative(relative);
        var full = Path.Combine(root, relative);
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Symbolic links are unsupported in template and snapshot storage paths.");
        return full;
    }
    private ProfileDefinition Definition(string id) => config.Profiles.SingleOrDefault(x => x.Id == id && x.Enabled)
        ?? throw new ArgumentException("Choose an enabled profile.");
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string DefinitionHash(ProfileDefinition p) => Hash(JsonSerializer.SerializeToUtf8Bytes(p with { Enabled = true }));
    public static string ManifestHash(TemplateManifest m) => Hash(JsonSerializer.SerializeToUtf8Bytes(m with { CreatedAt = default }));
    private string Binding(ProfileDefinition p) => Path.Combine(Storage, "versions", Hash(JsonSerializer.SerializeToUtf8Bytes(new[] { p.Id, p.Version })) + ".json");
    public PreparedProfile GetPrepared(string id)
    {
        var p = Definition(id);
        try
        {
            var prepared = JsonSerializer.Deserialize<PreparedProfile>(File.ReadAllText(Binding(p)))!;
            if (ManifestHash(prepared.Manifest) != prepared.Hash || prepared.Manifest.ProfileId != p.Id || prepared.Manifest.Version != p.Version || prepared.Manifest.DefinitionHash != DefinitionHash(p))
                throw new ArgumentException("Profile definition changed; publish a new version and prepare it.");
            Verify(prepared.Hash, p.Id, p.Version);
            return prepared;
        }
        catch (Exception e) when (e is IOException or JsonException or NullReferenceException) { throw new ArgumentException("Profile snapshot unavailable. Ask the operator to prepare or restore it."); }
    }
    public PreparedProfile Prepare(string id)
    {
        var p = Definition(id);
        var root = Expand(p.Root);
        var entries = new List<TemplateEntry>();
        var contents = new Dictionary<string, byte[]>();
        foreach (var relative in p.Files.Except(p.Exclusions, StringComparer.Ordinal).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(Inside(root, relative));
            string templateId;
            try { templateId = ValidateTemplate(Encoding.UTF8.GetString(bytes)); }
            catch (Exception e) when (e is InvalidCastException or KeyNotFoundException)
            { throw new ArgumentException($"Unsupported template structure in {relative}; require standalone HTTP GET templates.", e); }
            var hash = Hash(bytes);
            var previous = entries.SingleOrDefault(x => x.Id == templateId);
            if (previous is not null)
            {
                if (previous.Hash != hash) throw new ArgumentException($"Conflicting template ID: {templateId}.");
                continue;
            }
            entries.Add(new(templateId, relative, hash)); contents.Add(relative, bytes);
        }
        if (entries.Count == 0) throw new ArgumentException("Profile resolves to zero templates.");
        var manifest = new TemplateManifest(p.Id, p.Version, p.Name, p.SourceVersion, DefinitionHash(p), entries.ToArray(), DateTimeOffset.UtcNow);
        var prepared = new PreparedProfile(manifest, ManifestHash(manifest));
        if (File.Exists(Binding(p)))
        {
            var existing = GetPrepared(id);
            if (existing.Hash != prepared.Hash) throw new ArgumentException("Profile content changed under the same ID/version. Publish a new version.");
            validateEngine(Verify(existing.Hash, p.Id, p.Version));
            return existing;
        }
        Directory.CreateDirectory(Path.Combine(Storage, "versions"));
        var staging = Path.Combine(Storage, ".preparing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var entry in entries)
            {
                var path = Path.Combine(staging, entry.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, contents[entry.Path]);
            }
            validateEngine(entries.Select(x => Path.Combine(staging, x.Path)).ToArray());
            File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest));
            try { Directory.Move(staging, Path.Combine(Storage, prepared.Hash)); }
            catch (IOException) when (Directory.Exists(Path.Combine(Storage, prepared.Hash))) { }
            Verify(prepared.Hash, p.Id, p.Version);
            // Atomic no-overwrite publication arbitrates concurrent preparations, including differing content.
            var bindingTemp = Path.Combine(Storage, ".binding-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(bindingTemp, JsonSerializer.Serialize(prepared));
                try { File.Move(bindingTemp, Binding(p), false); }
                catch (IOException) when (File.Exists(Binding(p))) { }
            }
            finally { File.Delete(bindingTemp); }
            var existing = GetPrepared(id);
            if (existing.Hash != prepared.Hash) throw new ArgumentException("Profile content changed under the same ID/version. Publish a new version.");
            return existing;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    public string[] Verify(string hash, string? profileId, string? version)
    {
        try
        {
            if (!Regex.IsMatch(hash, "^[a-f0-9]{64}$")) throw new ArgumentException("Invalid snapshot hash.");
            var root = Inside(Storage, hash);
            var manifest = JsonSerializer.Deserialize<TemplateManifest>(File.ReadAllText(Inside(root, "manifest.json")))!;
            if (ManifestHash(manifest) != hash || manifest.ProfileId != profileId || manifest.Version != version || manifest.Templates.Length == 0)
                throw new ArgumentException("Snapshot manifest mismatch.");
            return manifest.Templates.Select(x =>
            {
                var path = Inside(root, x.Path);
                if (Hash(File.ReadAllBytes(path)) != x.Hash) throw new ArgumentException("Snapshot content altered.");
                return path;
            }).ToArray();
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException)
        { throw new ArgumentException("Template snapshot missing or altered; restore the recorded snapshot. No fallback was used.", e); }
    }
    internal static string ValidateTemplate(string text)
    {
        var yaml = new YamlStream();
        try { yaml.Load(new StringReader(text)); }
        catch (YamlDotNet.Core.YamlException e) { throw new ArgumentException("Invalid template YAML.", e); }
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new ArgumentException("Require a single YAML template.");
        static string Scalar(YamlNode n) => (n as YamlScalarNode)?.Value ?? throw new ArgumentException("Expected scalar.");
        static void Keys(YamlMappingNode map, params string[] allowed)
        {
            if (map.Children.Keys.Any(k => !allowed.Contains(Scalar(k))))
                throw new ArgumentException("Unsupported template feature/dependency; only reviewed standalone HTTP GET templates are supported.");
        }
        Keys(root, "id", "info", "http");
        if (!root.Children.TryGetValue(new YamlScalarNode("info"), out var infoNode) || infoNode is not YamlMappingNode info ||
            new[] { "name", "author", "severity" }.Any(k => !info.Children.ContainsKey(new YamlScalarNode(k))))
            throw new ArgumentException("Template info requires name, author and severity.");
        if (!root.Children.ContainsKey(new YamlScalarNode("id")) || !root.Children.ContainsKey(new YamlScalarNode("http")))
            throw new ArgumentException("Template requires id and HTTP requests.");
        var id = Scalar(root.Children[new YamlScalarNode("id")]);
        if (!SafeName(id)) throw new ArgumentException("Invalid template ID.");
        if (root.Children[new YamlScalarNode("http")] is not YamlSequenceNode requests || requests.Children.Count == 0)
            throw new ArgumentException("HTTP requests required.");
        foreach (var request in requests.Children.Cast<YamlMappingNode>())
        {
            Keys(request, "method", "path", "payloads", "matchers-condition", "matchers", "extractors", "stop-at-first-match");
            if (Scalar(request.Children[new YamlScalarNode("method")]) != "GET") throw new ArgumentException("Only GET is supported.");
            foreach (var path in ((YamlSequenceNode)request.Children[new YamlScalarNode("path")]).Children)
                if (!Scalar(path).StartsWith("{{BaseURL}}/", StringComparison.Ordinal) && !Scalar(path).StartsWith("{{BaseURL}}{{paths}}", StringComparison.Ordinal))
                    throw new ArgumentException("Requests must use the supplied BaseURL.");
            if (!request.Children.TryGetValue(new YamlScalarNode("matchers"), out var matcherNode) || matcherNode is not YamlSequenceNode matchers || matchers.Children.Count == 0)
                throw new ArgumentException("At least one matcher is required.");
            foreach (var matcher in matchers.Children.Cast<YamlMappingNode>())
            {
                Keys(matcher, "type", "name", "part", "condition", "negative", "internal", "words", "regex", "status", "dsl", "encoding", "case-insensitive", "match-all");
                if (!matcher.Children.TryGetValue(new YamlScalarNode("type"), out var type) || Scalar(type) is not ("word" or "regex" or "status" or "dsl"))
                    throw new ArgumentException("Unsupported matcher type.");
                if (Scalar(type) == "dsl" && matcher.Children.TryGetValue(new YamlScalarNode("dsl"), out var expressions))
                    foreach (var expression in ((YamlSequenceNode)expressions).Children)
                        if (Regex.Matches(Scalar(expression), @"([a-zA-Z_][a-zA-Z0-9_]*)\s*\(").Any(m => m.Groups[1].Value is not ("contains" or "tolower")))
                            throw new ArgumentException("Unsupported DSL helper; only reviewed response-only contains/tolower expressions are supported.");
            }
            if (request.Children.TryGetValue(new YamlScalarNode("extractors"), out var extractorNode))
                foreach (var extractor in ((YamlSequenceNode)extractorNode).Children.Cast<YamlMappingNode>())
                {
                    Keys(extractor, "type", "name", "part", "group", "regex");
                    if (Scalar(extractor.Children[new YamlScalarNode("type")]) != "regex") throw new ArgumentException("Only regex extractors supported.");
                }
            if (request.Children.TryGetValue(new YamlScalarNode("payloads"), out var payloads))
                foreach (var value in ((YamlMappingNode)payloads).Children.Values)
                    if (value is not YamlSequenceNode seq || seq.Children.Any(x => x is not YamlScalarNode))
                        throw new ArgumentException("External payload dependencies unsupported; use inline payloads.");
        }
        if (Regex.IsMatch(text, @"(?i)(interactsh|read_file\s*\(|\{\{[^}]*\()"))
            throw new ArgumentException("External dependencies or dynamic request functions unsupported.");
        return id;
    }
}
