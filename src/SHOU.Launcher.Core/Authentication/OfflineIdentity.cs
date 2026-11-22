using System.Security.Cryptography;
using System.Text;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Authentication;

public static class OfflineIdentity
{
    public static string Create(string name, OfflineUuidMode mode = OfflineUuidMode.Standard, string? customUuid = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 16 || name.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("玩家 ID 须为 1–16 位字母、数字或下划线；不推荐中文名，部分模组或服务器不兼容。");
        if (mode == OfflineUuidMode.Custom)
        {
            if (!(Guid.TryParseExact(customUuid, "N", out var id) || Guid.TryParseExact(customUuid, "D", out id)) || id == Guid.Empty)
                throw new ArgumentException("请输入有效的 UUID（32 位十六进制，可带连字符）。");
            return id.ToString("N");
        }
        if (mode == OfflineUuidMode.Legacy)
        {
            // Portable v1.5 identity algorithm. Preserve UTF-16 characters and unsigned overflow exactly.
            ulong hash = 5381;
            foreach (var character in name) hash = unchecked((hash << 5) ^ hash ^ character);
            var value = name.Length.ToString("x16") + (hash ^ 0xa98f501bc684032fUL).ToString("x16");
            return value[..12] + "3" + value[13..16] + "9" + value[17..];
        }
        if (mode != OfflineUuidMode.Standard) throw new ArgumentOutOfRangeException(nameof(mode));
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return Convert.ToHexStringLower(bytes);
    }
}
