using System.Text.Json.Serialization;

namespace BMSMonitor.Services;

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(GitHubAsset))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class AppJsonContext : JsonSerializerContext { }
