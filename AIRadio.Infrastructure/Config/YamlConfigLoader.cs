using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AIRadio.Infrastructure;

/// <summary>
/// YAML を DTO 型へデシリアライズする汎用ローダ（snake_case = UnderscoredNamingConvention）。
/// 各設定は「DTO へ Deserialize → map/validate して domain 型へ」の 1 経路に集約する
/// （ファイルごとのローダ量産を避ける。CLAUDE.md §3-5）。
/// </summary>
public static class YamlConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>YAML 文字列を DTO へデシリアライズ。</summary>
    public static T Deserialize<T>(string yaml) => Deserializer.Deserialize<T>(yaml);

    /// <summary>ファイルパスから読み込んで DTO へデシリアライズ。</summary>
    public static T LoadFile<T>(string path) => Deserialize<T>(File.ReadAllText(path));
}
