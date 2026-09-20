using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

/// <summary>授权错误码（结构保留，便于理解主程序流程）</summary>
public enum LicenseError
{
    None,
    Missing,
    ParseError,
    BadSignature,
    MachineMismatch,
    Expired,
    VersionTooLow,
}

public sealed class LicensePayload
{
    [JsonInclude][JsonPropertyName("licenseId")] public string LicenseId = "";
    [JsonInclude][JsonPropertyName("machineId")] public string MachineId = "";
    [JsonInclude][JsonPropertyName("issuedAt")] public string IssuedAt = "";
    [JsonInclude][JsonPropertyName("expiresAt")] public string ExpiresAt = "";
    [JsonInclude][JsonPropertyName("features")] public string[] Features = Array.Empty<string>();
    [JsonInclude][JsonPropertyName("maxVersion")] public string MaxVersion = "";
    [JsonInclude][JsonPropertyName("signature")] public string Signature = "";
}

/// <summary>
/// 【开源占位实现】授权管理器。
///
/// 官方发布版通过本模块完成：机器指纹采集、ECDSA P-256 + SHA256 签名验证、
/// Crockford Base32 申请码编解码、防回退时间戳校验等。
/// 出于安全与商业考虑，授权算法相关代码未开源，此处仅保留公开接口的占位实现，
/// 使仓库可整体编译、可阅读主程序全部业务逻辑。
///
/// 完整可用的软件请从官方渠道下载（需激活码授权）：
///   官网 https://leitingquant.bond   QQ群 3245668977
/// </summary>
public static class LicenseManager
{
    public const string ContactEmail = "LeiTingQuant@126.com";

    public static bool IsValid { get; private set; } = false;
    public static string? LicenseId { get; private set; }
    public static string? ExpiresAtText { get; private set; }
    public static IReadOnlyList<string> Features { get; private set; } = Array.Empty<string>();
    public static LicenseError LastError { get; private set; } = LicenseError.Missing;

    public static bool HasFeature(string feature) => IsValid && Features.Contains(feature);

    /// <summary>开源占位：不支持本地验证，一律返回 Missing。</summary>
    public static LicenseError Validate()
        => Validate(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "license.json"));

    public static LicenseError Validate(string path)
    {
        LastError = LicenseError.Missing;
        return LastError;
    }

    public static string ErrorText(LicenseError e) => e switch
    {
        LicenseError.None => "授权有效",
        LicenseError.Missing => "开源学习版不包含授权模块——请到官网下载官方发布版并用激活码激活",
        LicenseError.ParseError => "授权文件损坏",
        LicenseError.BadSignature => "签名无效（文件被篡改）",
        LicenseError.MachineMismatch => "授权与当前机器不匹配",
        LicenseError.Expired => "授权已过期",
        LicenseError.VersionTooLow => "授权版本过低，请更新软件",
        _ => "未知授权错误",
    };

    /// <summary>开源占位：官方版的申请码由机器指纹生成，此处仅返回提示。</summary>
    public static string GetRequestCode()
        => "OPEN-SOURCE-BUILD（开源版不含授权模块，请下载官方发布版获取申请码）";
}
