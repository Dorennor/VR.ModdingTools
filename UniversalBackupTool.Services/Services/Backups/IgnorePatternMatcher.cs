using System.Text;
using System.Text.RegularExpressions;

using UniversalBackupTool.Interface.Interfaces.Backups;

namespace UniversalBackupTool.Service.Services.Backups
{
    public sealed class IgnorePatternMatcher : IIgnorePatternMatcher
    {
        public IIgnoreFilter Create(IEnumerable<string> patterns)
        {
            ArgumentNullException.ThrowIfNull(patterns);

            var rules = new List<Regex>();

            foreach (var pattern in patterns)
            {
                var regex = TryBuildRegex(pattern);

                if (regex is not null)
                    rules.Add(regex);
            }

            return new IgnoreFilter(rules);
        }

        private static Regex? TryBuildRegex(string? rawPattern)
        {
            if (string.IsNullOrWhiteSpace(rawPattern))
                return null;

            var pattern = rawPattern.Trim().Replace('\\', '/');

            if (pattern.StartsWith('#'))
                return null;

            var directoryOnly = pattern.EndsWith('/');
            pattern = pattern.TrimEnd('/');

            if (pattern.Length == 0)
                return null;

            // A slash inside the pattern anchors it to the component root, otherwise it matches at any depth.
            var anchored = pattern.Contains('/');
            pattern = pattern.TrimStart('/');

            if (pattern.Length == 0)
                return null;

            var prefix = anchored ? "^" : "(?:^|/)";
            var suffix = directoryOnly ? "/" : "(?:/|$)";

            return new Regex(prefix + GlobToRegex(pattern) + suffix, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string GlobToRegex(string glob)
        {
            var builder = new StringBuilder();

            for (var index = 0; index < glob.Length; index++)
            {
                var character = glob[index];

                switch (character)
                {
                    case '*':
                        if (index + 1 < glob.Length && glob[index + 1] == '*')
                        {
                            index++;

                            if (index + 1 < glob.Length && glob[index + 1] == '/')
                            {
                                index++;
                                builder.Append("(?:.*/)?");
                            }
                            else
                            {
                                builder.Append(".*");
                            }
                        }
                        else
                        {
                            builder.Append("[^/]*");
                        }

                        break;

                    case '?':
                        builder.Append("[^/]");
                        break;

                    default:
                        builder.Append(Regex.Escape(character.ToString()));
                        break;
                }
            }

            return builder.ToString();
        }

        private sealed class IgnoreFilter : IIgnoreFilter
        {
            private readonly IReadOnlyList<Regex> _rules;

            public IgnoreFilter(IReadOnlyList<Regex> rules)
            {
                _rules = rules;
            }

            public bool IsIgnored(string relativePath)
            {
                if (_rules.Count == 0 || string.IsNullOrEmpty(relativePath))
                    return false;

                var normalized = relativePath.Replace('\\', '/').TrimStart('/');

                return _rules.Any(rule => rule.IsMatch(normalized));
            }
        }
    }
}