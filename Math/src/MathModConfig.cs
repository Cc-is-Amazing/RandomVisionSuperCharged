using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace MathMod;

/// <summary>
/// 抽牌预测只需要记住一组很小的整数选项，
/// 直接落到 Mod 目录旁的 JSON 文件里，便于玩家手改与备份。
/// </summary>
public sealed class MathModConfig
{
    private const string ConfigFileName = "config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static MathModConfig Instance { get; private set; } = CreateDefault();

    public static event Action? Updated;

    [JsonPropertyName("draw_probability_counts")]
    public List<int> DrawProbabilityCounts { get; init; } = new();

    public static IReadOnlyList<int> SelectedDrawProbabilityCounts => Instance.DrawProbabilityCounts;

    public static void Load()
    {
        string configPath = GetConfigPath();

        try
        {
            if (!File.Exists(configPath))
            {
                Instance = CreateDefault();
                Save();
                return;
            }

            string json = File.ReadAllText(configPath);
            MathModConfig? parsed = JsonSerializer.Deserialize<MathModConfig>(json, JsonOptions);
            Instance = Normalize(parsed) ?? CreateDefault();
        }
        catch (Exception exception)
        {
            Instance = CreateDefault();
            MainFile.Logger.Error($"Failed to load Math config: {exception}");
        }

        Updated?.Invoke();
    }

    public static void SetDrawProbabilityEnabled(int drawCount, bool isEnabled)
    {
        if (drawCount < 1 || drawCount > 10)
        {
            return;
        }

        HashSet<int> nextCounts = Instance.DrawProbabilityCounts.ToHashSet();
        if (isEnabled)
        {
            nextCounts.Add(drawCount);
        }
        else
        {
            nextCounts.Remove(drawCount);
        }

        Instance = new MathModConfig
        {
            DrawProbabilityCounts = nextCounts.OrderBy(static count => count).ToList()
        };
        Save();
        Updated?.Invoke();
    }

    private static MathModConfig? Normalize(MathModConfig? config)
    {
        if (config == null)
        {
            return null;
        }

        return new MathModConfig
        {
            // 配置允许为空，表示玩家暂时不想显示任何抽牌概率行；
            // 只有首次创建默认配置时，才自动勾上 5 抽。
            DrawProbabilityCounts = config.DrawProbabilityCounts
                .Where(static count => count is >= 1 and <= 10)
                .Distinct()
                .OrderBy(static count => count)
                .ToList()
        };
    }

    private static MathModConfig CreateDefault()
    {
        return new MathModConfig
        {
            DrawProbabilityCounts = new List<int> { 5 }
        };
    }

    private static string GetConfigPath()
    {
        string? executableDirectory = Path.GetDirectoryName(OS.GetExecutablePath());
        if (string.IsNullOrWhiteSpace(executableDirectory))
        {
            return ConfigFileName;
        }

        return Path.Combine(executableDirectory, "mods", MainFile.ModId, ConfigFileName);
    }

    private static void Save()
    {
        try
        {
            string configPath = GetConfigPath();
            string? directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(configPath, JsonSerializer.Serialize(Instance, JsonOptions));
        }
        catch (Exception exception)
        {
            MainFile.Logger.Error($"Failed to save Math config: {exception}");
        }
    }
}
