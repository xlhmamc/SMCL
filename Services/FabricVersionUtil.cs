using System;
using System.Globalization;
using System.Linq;

namespace SMCL.Services
{
    /// <summary>
    /// Fabric 版本号与版本谓词（version predicate）工具。
    ///
    /// 支持 <c>fabric.mod.json</c> 中常见的约束语法：
    /// <list type="bullet">
    ///   <item><c>1.20.1</c>、<c>1.20.x</c>、<c>*</c></item>
    ///   <item><c>&gt;=0.16.10</c>、<c>&gt;0.16.10</c>、<c>&lt;=...</c>、<c>&lt;...</c>、<c>=...</c></item>
    ///   <item><c>~0.16.10</c>（同一 minor 内）、<c>^0.16.10</c>（同一 major 内）</item>
    ///   <item>空格分隔表示 AND，<c>||</c> 表示 OR</item>
    /// </list>
    /// Fabric Loader 的版本号均为数字段（如 0.15.11 / 0.16.10），这里只实现
    /// 比较所需的最小 SemVer 语义，避免引入额外依赖。
    /// </summary>
    public static class FabricVersionUtil
    {
        private static readonly string[] Operators = { ">=", "<=", ">", "<", "=", "~", "^" };

        /// <summary>
        /// 判断 <paramref name="version"/> 是否满足 <paramref name="constraint"/>。
        /// 约束为空时视为无限制。
        /// </summary>
        public static bool Satisfies(string? version, string? constraint)
        {
            if (string.IsNullOrWhiteSpace(version)) return false;
            if (string.IsNullOrWhiteSpace(constraint)) return true;

            foreach (string orClause in constraint.Split(new[] { "||" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string normalized = orClause.Replace(',', ' ');
                string[] predicates = normalized.Split(
                    new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries);

                if (predicates.Length == 0) continue;

                bool allMatched = true;
                foreach (string predicate in predicates)
                {
                    if (!EvaluatePredicate(version, predicate))
                    {
                        allMatched = false;
                        break;
                    }
                }

                if (allMatched) return true;
            }

            return false;
        }

        /// <summary>
        /// 比较两个版本号：返回 &lt;0、0、&gt;0。
        /// </summary>
        public static int Compare(string? left, string? right)
        {
            TryParse(left, out int[] l, out string? lPre, out _);
            TryParse(right, out int[] r, out string? rPre, out _);

            for (int i = 0; i < 3; i++)
            {
                int diff = l[i].CompareTo(r[i]);
                if (diff != 0) return diff;
            }

            // 预发布版本低于同核心版本号（SemVer）
            bool leftIsPre = !string.IsNullOrEmpty(lPre);
            bool rightIsPre = !string.IsNullOrEmpty(rPre);

            if (!leftIsPre && !rightIsPre) return 0;
            if (!leftIsPre) return 1;
            if (!rightIsPre) return -1;

            return string.Compare(lPre, rPre, StringComparison.OrdinalIgnoreCase);
        }

        private static bool EvaluatePredicate(string version, string predicate)
        {
            predicate = predicate.Trim();
            if (predicate.Length == 0) return true;
            if (predicate == "*" || predicate.Equals("x", StringComparison.OrdinalIgnoreCase)) return true;

            string op = "=";
            foreach (string candidate in Operators)
            {
                if (predicate.StartsWith(candidate, StringComparison.Ordinal))
                {
                    op = candidate;
                    predicate = predicate[candidate.Length..].Trim();
                    break;
                }
            }

            if (!TryParse(predicate, out int[] target, out _, out int specified))
                return false;

            if (!TryParse(version, out int[] value, out _, out _))
                return false;

            // 通配符版本（0.16.x、1.x）按 Fabric 语义转换为前缀范围；
            // 普通的 1.20 这类不完整版本则按 1.20.0 精确比较。
            bool hasExplicitWildcard = IsWildcard(predicate);

            if (op == "=")
            {
                if (hasExplicitWildcard)
                {
                    for (int i = 0; i < specified && i < 3; i++)
                    {
                        if (value[i] != target[i]) return false;
                    }
                    return true;
                }

                return Compare(version, predicate) == 0;
            }

            int cmp = Compare(version, predicate);

            switch (op)
            {
                case ">=":
                    return cmp >= 0;
                case ">":
                    return cmp > 0;
                case "<=":
                    return cmp <= 0;
                case "<":
                    return cmp < 0;
                case "~":
                    // Fabric: SAME_TO_NEXT_MINOR —— >=target 且 major/minor 完全相同
                    return cmp >= 0 &&
                           value[0] == target[0] &&
                           value[1] == target[1];
                case "^":
                    // Fabric: SAME_TO_NEXT_MAJOR —— >=target 且 major 完全相同
                    return cmp >= 0 &&
                           value[0] == target[0];
                default:
                    return false;
            }
        }

        private static bool IsWildcard(string versionText)
        {
            return versionText.Split('.').Any(p =>
                p == "*" || p.Equals("x", StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryParse(string? text, out int[] core, out string? preRelease, out int specifiedCount)
        {
            core = new[] { 0, 0, 0 };
            preRelease = null;
            specifiedCount = 0;

            if (string.IsNullOrWhiteSpace(text)) return false;

            string value = text.Trim();

            int buildIndex = value.IndexOf('+');
            if (buildIndex >= 0) value = value[..buildIndex];

            // Fabric 在约束末尾加 "-" 表示允许预发布版本
            if (value.EndsWith("-", StringComparison.Ordinal)) value = value[..^1];
            if (value.Length == 0) return false;

            int preIndex = value.IndexOf('-');
            if (preIndex >= 0)
            {
                preRelease = value[(preIndex + 1)..];
                value = value[..preIndex];
            }

            string[] parts = value.Split('.');
            for (int i = 0; i < parts.Length && i < 3; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0) return false;
                if (part == "*" || part.Equals("x", StringComparison.OrdinalIgnoreCase))
                    break;

                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out core[i]))
                    return false;

                specifiedCount = i + 1;
            }

            return specifiedCount > 0;
        }
    }
}
