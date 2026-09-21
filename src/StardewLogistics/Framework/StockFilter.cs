using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StardewLogistics.Framework
{
    /// <summary>Decides which network stock entries the terminal shows.</summary>
    /// <remarks>
    /// Combines three independent restrictions, all of which must pass: the free-text search box, the selected
    /// item category, and the selected source mod. The search box understands a small query language so that
    /// filters which would otherwise need their own widget stay typeable:
    ///
    /// <code>
    ///   blueberry          name contains "blueberry"
    ///   "iron bar"         quoted phrase, so the space isn't a term separator
    ///   #wine              context tag contains "wine"
    ///   {@}fish            category name contains "fish"
    ///   ~ridgeside         source mod name contains "ridgeside"
    ///   >500 &lt;=2000       quantity comparisons
    ///   !stone             excludes entries matching "stone"
    /// </code>
    ///
    /// Terms are ANDed, so "#wine >100" means wine of which there are more than a hundred.
    /// </remarks>
    internal class StockFilter
    {
        /*********
        ** Fields
        *********/
        private readonly List<Term> Terms = new();


        /*********
        ** Accessors
        *********/
        /// <summary>The raw search text this filter was parsed from.</summary>
        public string SearchText { get; private set; } = "";

        /// <summary>The category to restrict to, or <c>null</c> for any category.</summary>
        public int? Category { get; set; }

        /// <summary>The display name of the category restriction, for the UI.</summary>
        public string CategoryLabel { get; set; }

        /// <summary>The source mod to restrict to, or <c>null</c> for any mod.</summary>
        public string Mod { get; set; }

        /// <summary>Whether this filter would exclude nothing.</summary>
        public bool IsEmpty => this.Terms.Count == 0 && this.Category == null && this.Mod == null;


        /*********
        ** Public methods
        *********/
        /// <summary>Replaces the typed search terms, keeping the dropdown selections.</summary>
        public void SetSearch(string text)
        {
            this.SearchText = text ?? "";
            this.Terms.Clear();

            foreach (string token in Tokenise(this.SearchText))
                this.Terms.Add(Term.Parse(token));
        }

        /// <summary>Clears every restriction.</summary>
        public void Clear()
        {
            this.Terms.Clear();
            this.SearchText = "";
            this.Category = null;
            this.CategoryLabel = null;
            this.Mod = null;
        }

        /// <summary>Whether an entry passes every restriction.</summary>
        public bool Matches(IFilterableEntry entry)
        {
            if (entry?.Sample == null)
                return false;

            if (this.Category != null && entry.Category != this.Category.Value)
                return false;

            if (this.Mod != null && !string.Equals(entry.SourceMod, this.Mod, StringComparison.OrdinalIgnoreCase))
                return false;

            foreach (Term term in this.Terms)
            {
                if (!term.Matches(entry))
                    return false;
            }

            return true;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Splits search text into terms, treating a quoted run as a single term.</summary>
        private static IEnumerable<string> Tokenise(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                yield break;

            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;
                if (i >= text.Length)
                    break;

                int start = i;
                bool quoted = false;

                while (i < text.Length && (quoted || !char.IsWhiteSpace(text[i])))
                {
                    if (text[i] == '"')
                        quoted = !quoted;
                    i++;
                }

                string token = text.Substring(start, i - start).Replace("\"", "");
                if (token.Length > 0)
                    yield return token;
            }
        }


        /*********
        ** Nested types
        *********/
        /// <summary>One parsed restriction from the search box.</summary>
        private class Term
        {
            private enum Kind { Name, Tag, Category, Mod, Quantity }

            /// <summary>The operators recognised for quantity terms, longest first so ">=" isn't read as ">".</summary>
            private static readonly string[] Operators = { ">=", "<=", "==", ">", "<", "=" };

            private Kind Type;
            private string Text;
            private bool Negated;
            private string Comparison;
            private long Threshold;

            /// <summary>Parses a single whitespace-delimited token.</summary>
            public static Term Parse(string token)
            {
                Term term = new();

                if (token.StartsWith("!") && token.Length > 1)
                {
                    term.Negated = true;
                    token = token.Substring(1);
                }

                foreach (string op in Operators)
                {
                    if (!token.StartsWith(op) || token.Length <= op.Length)
                        continue;

                    if (long.TryParse(token.Substring(op.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                    {
                        term.Type = Kind.Quantity;
                        term.Comparison = op;
                        term.Threshold = value;
                        return term;
                    }
                }

                if (token.Length > 1)
                {
                    switch (token[0])
                    {
                        case '#':
                            term.Type = Kind.Tag;
                            term.Text = token.Substring(1);
                            return term;

                        case '@':
                            term.Type = Kind.Category;
                            term.Text = token.Substring(1);
                            return term;

                        case '~':
                            term.Type = Kind.Mod;
                            term.Text = token.Substring(1);
                            return term;
                    }
                }

                term.Type = Kind.Name;
                term.Text = token;
                return term;
            }

            /// <summary>Whether an entry satisfies this term.</summary>
            public bool Matches(IFilterableEntry entry)
            {
                bool result = this.Evaluate(entry);
                return this.Negated ? !result : result;
            }

            /// <summary>Evaluates the term before negation is applied.</summary>
            private bool Evaluate(IFilterableEntry entry)
            {
                switch (this.Type)
                {
                    case Kind.Quantity:
                        return this.Comparison switch
                        {
                            ">" => entry.Count > this.Threshold,
                            ">=" => entry.Count >= this.Threshold,
                            "<" => entry.Count < this.Threshold,
                            "<=" => entry.Count <= this.Threshold,
                            _ => entry.Count == this.Threshold
                        };

                    case Kind.Tag:
                        try
                        {
                            return entry.Sample.GetContextTags().Any(tag => tag.Contains(this.Text, StringComparison.OrdinalIgnoreCase));
                        }
                        catch
                        {
                            // A malformed item shouldn't break the whole search.
                            return false;
                        }

                    case Kind.Category:
                    {
                        string name = entry.Sample.getCategoryName();
                        return !string.IsNullOrEmpty(name) && name.Contains(this.Text, StringComparison.OrdinalIgnoreCase);
                    }

                    case Kind.Mod:
                        return entry.SourceMod.Contains(this.Text, StringComparison.OrdinalIgnoreCase);

                    default:
                        return entry.DisplayName.Contains(this.Text, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }
}
