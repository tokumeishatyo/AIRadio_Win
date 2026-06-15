using AIRadio.Core;

namespace AIRadio.Infrastructure;

/// <summary>
/// LLM 設定。本体（モデル等、<c>config/llm.yaml</c>）と機密（API キー、<c>config/llm.local.yaml</c>）の 2 ファイル構成。
/// キー欠落（local ファイルなし / api_key 空 / サンプルのプレースホルダのまま）は fail-fast（<see cref="LlmException.KeyMissing"/>）。
/// </summary>
public sealed record LlmConfig(string Provider, string Model, string Endpoint, double Temperature, string ApiKey)
{
    private const string DefaultEndpoint = "https://generativelanguage.googleapis.com/";

    public static LlmConfig Load(string mainYaml, string? localYaml)
    {
        var main = YamlConfigLoader.Deserialize<MainDto>(mainYaml);
        var model = main?.Llm?.Model;
        if (string.IsNullOrEmpty(model))
        {
            throw ConfigException.MissingField("llm.model");
        }

        if (localYaml is null)
        {
            throw LlmException.KeyMissing();
        }
        var local = YamlConfigLoader.Deserialize<LocalDto>(localYaml);
        var apiKey = (local?.Llm?.ApiKey ?? "").Trim();
        if (apiKey.Length == 0 || apiKey.Contains("PASTE", StringComparison.Ordinal))
        {
            throw LlmException.KeyMissing();
        }

        return new LlmConfig(
            Provider: main!.Llm!.Provider ?? "gemini",
            Model: model,
            Endpoint: main.Llm.Endpoint ?? DefaultEndpoint,
            Temperature: main.Llm.Temperature ?? 0.9,
            ApiKey: apiKey);
    }

    public static LlmConfig LoadFiles(string mainPath, string localPath)
    {
        var mainYaml = File.ReadAllText(mainPath);
        var localYaml = File.Exists(localPath) ? File.ReadAllText(localPath) : null;
        return Load(mainYaml, localYaml);
    }

    public sealed class MainDto
    {
        public LlmMain? Llm { get; set; }

        public sealed class LlmMain
        {
            public string? Provider { get; set; }
            public string? Model { get; set; }
            public string? Endpoint { get; set; }
            public double? Temperature { get; set; }
        }
    }

    public sealed class LocalDto
    {
        public LlmLocal? Llm { get; set; }

        public sealed class LlmLocal
        {
            public string? ApiKey { get; set; }
        }
    }
}
