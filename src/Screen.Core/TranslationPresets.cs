namespace Screen.Core;

/// <summary>
/// 主流 OpenAI 兼容 AI 翻译服务商预设（均使用当前在役官方模型编码），帮助进阶用户仅填 API Key 即可快速启用。
/// </summary>
public sealed record AiTranslationPreset(
    string Id,
    string DisplayName,
    string BaseAddress,
    string DefaultModel);

public static class TranslationPresets
{
    public static AiTranslationPreset DefaultOpenAi { get; } =
        new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-5.4-mini");

    public static IReadOnlyList<AiTranslationPreset> All { get; } =
    [
        new("custom", "自定义（手动填写地址与模型）", string.Empty, string.Empty),
        new("zhipu", "智谱 GLM（官方免费模型）", "https://open.bigmodel.cn/api/paas/v4", "glm-4.7-flash"),
        new("deepseek", "DeepSeek（国内直连）", "https://api.deepseek.com/v1", "deepseek-flash"),
        new("dashscope", "阿里云百炼（通义千问）", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-flash"),
        new("siliconflow", "硅基流动（含免费模型）", "https://api.siliconflow.cn/v1", "Qwen/Qwen2.5-7B-Instruct"),
        DefaultOpenAi
    ];

    /// <summary>
    /// 根据已保存的 BaseAddress 匹配对应预设索引；未命中时返回 0（自定义）。
    /// </summary>
    public static int FindPresetIndex(string? baseAddress)
    {
        var normalized = (baseAddress ?? string.Empty).Trim().TrimEnd('/');
        if (normalized.Length == 0)
        {
            return 0;
        }
        for (var index = 1; index < All.Count; index++)
        {
            if (string.Equals(All[index].BaseAddress, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return 0;
    }
}
