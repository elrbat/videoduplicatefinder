// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GPLv3 as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
//     You should have received a copy of the GNU General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VDF.GUI.ViewModels {
	/// <summary>
	/// The results search box understands a few "field operator value" terms next to plain
	/// words. Terms are separated by spaces; wrap a value in double quotes to keep spaces in it.
	///
	///   name:text     file name (without folders) contains text
	///   name=text     file name is exactly text (with or without the extension)
	///   name!=text    no file of the group is named exactly text
	///   name!:text    no file of the group has a name containing text
	///   path:text     full path contains text (same for path=, path!=, path!:)
	///   size>500MB    a file of the group is bigger than 500 MB (also >=, &lt;, &lt;=, =)
	///   total&lt;10GB     all files of the group together are smaller than 10 GB
	///
	/// Sizes accept B, KB, MB, GB, TB (1 KB = 1024 B); a number without a unit means MB.
	/// "*" and "?" work as wildcards inside name and path values.
	/// Plain words (no field) must appear in the path, like before.
	///
	/// How terms combine: every positive term (words, name:, path:, name=, size) has to be
	/// satisfied by the SAME file, and a group is shown when at least one of its files
	/// satisfies all of them. Negative terms (!= and !:) exclude a group as soon as any of its
	/// files matches the positive form. total applies to the whole group.
	/// </summary>
	internal sealed class ResultsSearchQuery {
		enum Field { Path, Name, Size, Total }
		enum Op { Contains, NotContains, Exact, NotExact, Greater, GreaterOrEqual, Less, LessOrEqual }

		sealed class Term {
			public Field Field;
			public Op Op;
			public string Text = string.Empty;
			public long Bytes;
		}

		static readonly Regex FieldTermRegex = new(
			@"^(path|name|size|total)(!=|!:|>=|<=|=|:|>|<)(.*)$",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

		static readonly Regex SizeRegex = new(
			@"^\s*(\d+(?:[.,]\d+)?)\s*([a-z]*)\s*$",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

		/// <summary>Terms one file must satisfy all at once.</summary>
		readonly List<Term> fileTerms = new();
		/// <summary>Terms (in their positive form) no file of a shown group may satisfy.</summary>
		readonly List<Term> excludeTerms = new();
		/// <summary>Terms about the summed size of a group.</summary>
		readonly List<Term> totalTerms = new();

		/// <summary>
		/// Returns null when the text uses none of the extra syntax (no field term, no quotes);
		/// the caller then keeps the classic path search for it.
		/// </summary>
		internal static ResultsSearchQuery? Parse(string? text) {
			if (string.IsNullOrWhiteSpace(text)) return null;

			bool special = text.Contains('"');
			var query = new ResultsSearchQuery();
			foreach (string token in SplitTokens(text)) {
				if (TryParseFieldTerm(token, out Term? term)) {
					special = true;
					if (term == null) continue; // value still empty while typing: ignore for now
					if (term.Field == Field.Total)
						query.totalTerms.Add(term);
					else if (term.Op is Op.NotContains or Op.NotExact)
						query.excludeTerms.Add(term);
					else
						query.fileTerms.Add(term);
				}
				else {
					query.fileTerms.Add(new Term { Field = Field.Path, Op = Op.Contains, Text = token });
				}
			}
			return special ? query : null;
		}

		/// <summary>Splits on whitespace; text inside double quotes stays together (the quotes are dropped).</summary>
		internal static List<string> SplitTokens(string text) {
			var tokens = new List<string>();
			var current = new StringBuilder();
			bool inQuotes = false;
			foreach (char c in text) {
				if (c == '"') { inQuotes = !inQuotes; continue; }
				if (!inQuotes && char.IsWhiteSpace(c)) {
					if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
					continue;
				}
				current.Append(c);
			}
			if (current.Length > 0) tokens.Add(current.ToString());
			return tokens;
		}

		/// <summary>
		/// False: the token is not a field term (it is treated as a plain word instead).
		/// True with a null term: a field term whose value is still empty.
		/// </summary>
		static bool TryParseFieldTerm(string token, out Term? term) {
			term = null;
			var m = FieldTermRegex.Match(token);
			if (!m.Success) return false;

			string field = m.Groups[1].Value.ToLowerInvariant();
			string op = m.Groups[2].Value;
			string value = m.Groups[3].Value.Trim();
			bool isSize = field == "size" || field == "total";

			if (value.Length == 0) return true;

			Op? parsedOp = ParseOp(op, isSize);
			if (parsedOp == null) return false;

			var t = new Term { Op = parsedOp.Value };
			if (isSize) {
				if (!TryParseSize(value, out long bytes)) return false;
				t.Field = field == "total" ? Field.Total : Field.Size;
				t.Bytes = bytes;
			}
			else {
				t.Field = field == "name" ? Field.Name : Field.Path;
				t.Text = value;
			}
			term = t;
			return true;
		}

		static Op? ParseOp(string op, bool isSize) {
			if (isSize) {
				switch (op) {
					case "=": return Op.Exact;
					case ">": return Op.Greater;
					case ">=": return Op.GreaterOrEqual;
					case "<": return Op.Less;
					case "<=": return Op.LessOrEqual;
					default: return null;
				}
			}
			switch (op) {
				case ":": return Op.Contains;
				case "!:": return Op.NotContains;
				case "=": return Op.Exact;
				case "!=": return Op.NotExact;
				default: return null;
			}
		}

		/// <summary>"500", "1.5GB", "1,5 gb", "512kb": a number plus optional B/KB/MB/GB/TB. No unit means MB.</summary>
		internal static bool TryParseSize(string text, out long bytes) {
			bytes = 0;
			var m = SizeRegex.Match(text);
			if (!m.Success) return false;
			if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
				return false;

			double factor;
			switch (m.Groups[2].Value.ToLowerInvariant()) {
				case "b": factor = 1; break;
				case "k": case "kb": case "kib": factor = 1024d; break;
				case "": case "m": case "mb": case "mib": factor = 1024d * 1024d; break;
				case "g": case "gb": case "gib": factor = 1024d * 1024d * 1024d; break;
				case "t": case "tb": case "tib": factor = 1024d * 1024d * 1024d * 1024d; break;
				default: return false;
			}
			bytes = (long)Math.Round(number * factor);
			return true;
		}

		/// <summary>The ids of all groups that satisfy the query.</summary>
		internal HashSet<Guid> MatchingGroups<T>(IEnumerable<T> items, Func<T, Guid> groupOf, Func<T, string> pathOf, Func<T, long> sizeOf) {
			var result = new HashSet<Guid>();
			foreach (var group in items.GroupBy(groupOf)) {
				long total = 0;
				bool fileMatched = fileTerms.Count == 0;
				bool excluded = false;

				foreach (T item in group) {
					string path = pathOf(item) ?? string.Empty;
					long size = sizeOf(item);
					total += size;

					SplitFileName(path, out string fileName, out string nameNoExt);

					if (!fileMatched && AllMatch(fileTerms, path, fileName, nameNoExt, size))
						fileMatched = true;
					if (!excluded && AnyMatch(excludeTerms, path, fileName, nameNoExt, size))
						excluded = true;
				}

				if (!fileMatched || excluded) continue;

				bool totalOk = true;
				foreach (var t in totalTerms) {
					if (!CompareSize(total, t)) { totalOk = false; break; }
				}
				if (totalOk) result.Add(group.Key);
			}
			return result;
		}

		/// <summary>Works the same on every OS: both \ and / count as folder separators.</summary>
		internal static void SplitFileName(string path, out string fileName, out string nameNoExt) {
			int slash = path.LastIndexOfAny(new[] { '\\', '/' });
			fileName = slash >= 0 ? path.Substring(slash + 1) : path;
			int dot = fileName.LastIndexOf('.');
			nameNoExt = dot > 0 ? fileName.Substring(0, dot) : fileName;
		}

		static bool AllMatch(List<Term> terms, string path, string fileName, string nameNoExt, long size) {
			foreach (var t in terms)
				if (!Matches(t, path, fileName, nameNoExt, size)) return false;
			return true;
		}

		static bool AnyMatch(List<Term> terms, string path, string fileName, string nameNoExt, long size) {
			foreach (var t in terms)
				if (Matches(t, path, fileName, nameNoExt, size)) return true;
			return false;
		}

		/// <summary>Evaluates the positive form of a term against one file.</summary>
		static bool Matches(Term t, string path, string fileName, string nameNoExt, long size) {
			bool exact = t.Op is Op.Exact or Op.NotExact;
			switch (t.Field) {
				case Field.Path:
					return TextMatches(path, t.Text, exact);
				case Field.Name:
					return TextMatches(fileName, t.Text, exact) || (exact && TextMatches(nameNoExt, t.Text, true));
				default:
					return CompareSize(size, t);
			}
		}

		static bool CompareSize(long value, Term t) {
			switch (t.Op) {
				case Op.Greater: return value > t.Bytes;
				case Op.GreaterOrEqual: return value >= t.Bytes;
				case Op.Less: return value < t.Bytes;
				case Op.LessOrEqual: return value <= t.Bytes;
				default: return value == t.Bytes;
			}
		}

		/// <summary>
		/// Case-insensitive. Without wildcards: equality (exact) or substring. With * or ? the
		/// value is a wildcard pattern; unless exact it may match anywhere in the text.
		/// </summary>
		static bool TextMatches(string value, string pattern, bool exact) {
			if (pattern.IndexOfAny(new[] { '*', '?' }) >= 0) {
				string p = pattern.Replace("\\", "\\\\"); // backslashes are literal in paths, not escapes
				if (!exact) {
					if (!p.StartsWith('*')) p = "*" + p;
					if (!p.EndsWith('*')) p += "*";
				}
				return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, value);
			}
			return exact
				? value.Equals(pattern, StringComparison.OrdinalIgnoreCase)
				: value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
		}
	}
}
