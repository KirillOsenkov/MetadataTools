﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BinaryCompatChecker
{
    public class IncludeExcludePattern
    {
        public string InclusionPattern { get; }
        public string ExclusionPattern { get; }

        private Regex m_inclusionRegex;
        private Regex m_exclusionRegex;

        private static RegexOptions regexOptions = Checker.IsWindows ? RegexOptions.IgnoreCase : RegexOptions.None;

        public Regex InclusionRegex => m_inclusionRegex ??= new Regex(InclusionPattern, regexOptions);
        public Regex ExclusionRegex => m_exclusionRegex ??= new Regex(ExclusionPattern, regexOptions);

        public IncludeExcludePattern(string inclusionPattern, string exclusionPattern)
        {
            InclusionPattern = inclusionPattern;
            ExclusionPattern = exclusionPattern;
        }

        public bool Includes(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            path = path.Replace('\\', '/');

            if (path[0] == '/')
            {
                path = path.Substring(1);
            }

            return InclusionRegex.IsMatch(path);
        }

        public bool Excludes(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            path = path.Replace('\\', '/');

            if (path[0] == '/')
            {
                path = path.Substring(1);
            }

            bool isMatch = ExclusionRegex.IsMatch(path);
            return isMatch;
        }

        public static IncludeExcludePattern ParseFromFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var lines = File.ReadAllLines(filePath);
            return ParseFromLines(lines);
        }

        public static IncludeExcludePattern ParseFromLines(IEnumerable<string> lines)
        {
            var includes = new List<string>();
            var excludes = new List<string>();

            foreach (var line in lines)
            {
                var parsed = Parse(line, out bool isNegative);
                if (parsed != null)
                {
                    if (isNegative)
                    {
                        excludes.Add(parsed);
                    }
                    else
                    {
                        includes.Add(parsed);
                    }
                }
            }

            return new IncludeExcludePattern(Combine(includes), Combine(excludes));
        }

        private static string Parse(string line, out bool isNegative)
        {
            isNegative = false;

            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            line = line.Trim();
            if (line.StartsWith("#"))
            {
                return null;
            }

            if (line.StartsWith("!"))
            {
                if (line.Length == 1)
                {
                    return null;
                }
                else
                {
                    line = line.Substring(1);
                    isNegative = true;
                }
            }

            return PrepareRegexPattern(line);
        }

        public static string Combine(IEnumerable<string> expressions)
        {
            if (expressions == null || !expressions.Any())
            {
                return "$^";
            }
            else if (expressions.Count() == 1)
            {
                return expressions.First();
            }
            else
            {
                return "^((" + string.Join(")|(", expressions) + "))";
            }
        }

        public static string PrepareRegexPattern(string line)
        {
            line = line.Replace("\\", "/");

            bool prefixMatch = false;
            if (line[0] == '/')
            {
                line = line.Substring(1);
                prefixMatch = true;
            }
            else if (line.StartsWith("**/"))
            {
                line = line.Substring(3);
            }

            bool matchFileOrDirectory = line[line.Length - 1] != '/';

            line = Regex.Replace(line, @"[\-\/\{\}\(\)\+\.\\\^\$\|]", "\\$0");

            line = line.Replace("?", "\\?");
            line = line.Replace("**", "(.+)");
            line = line.Replace("*", "(([^\\/]+)|$)");

            if (!prefixMatch)
            {
                line = "((.+)\\/)?" + line;
            }

            if (matchFileOrDirectory)
            {
                line += "(/|$)";
            }

            return line;
        }
    }
}
