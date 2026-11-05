using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace RDM.Shared.Common;

public static class RemoteIdFormat
{
    /// <summary>Bỏ khoảng trắng, dấu gạch. Trả về null nếu không phải 9-12 chữ số.</summary>
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var compact = new string(input.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        return compact.Length >= 9 && compact.Length <= 12 && compact.All(c => c >= '0' && c <= '9') ? compact : null;
    }

    /// <summary>Hiển thị theo nhóm 3 chữ số: 123 456 789.</summary>
    public static string Display(string remoteId)
    {
        if (string.IsNullOrEmpty(remoteId)) return string.Empty;
        return Regex.Replace(remoteId, ".{3}(?!$)", "$0 ");
    }
}

public static class PasswordPolicy
{
    public const int MinLength = 10;

    /// <summary>Trả về lý do không đạt, hoặc null nếu hợp lệ. Ưu tiên độ dài thay vì luật phức tạp.</summary>
    public static string Validate(string password, string username = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return $"The password must be at least {MinLength} characters.";
        if (username != null && string.Equals(password, username, StringComparison.OrdinalIgnoreCase))
            return "The password cannot be the same as the username.";
        if (password.Distinct().Count() < 4)
            return "The password is too simple.";
        return null;
    }
}

/// <summary>Che giá trị nhạy cảm trước khi ghi Log hoặc Audit, phòng trường hợp lập trình viên lỡ truyền vào.</summary>
public static class LogRedactor
{
    private static readonly Regex Secret = new Regex(
        @"(?i)\b(password|passwd|pwd|mật khẩu|token|secret|private[_ ]?key|session[_ ]?key|api[_ ]?key|key|credential)\s*[:=]\s*(""[^""]*""|'[^']*'|\S+)",
        RegexOptions.Compiled);

    public static string Redact(string text) =>
        string.IsNullOrEmpty(text) ? text : Secret.Replace(text, m => m.Groups[1].Value + "=***");
}
