using System;
using System.Globalization;

namespace StardewLogistics.Framework
{
    /// <summary>Evaluates the small integer expressions the quantity box accepts, such as <c>10*2</c> or <c>(3+4)*6</c>.</summary>
    /// <remarks>
    /// A recursive-descent parser over <c>+ - * / ( )</c> and whole numbers. Division truncates, since the result is
    /// a number of crafts. Anything malformed fails rather than guessing, so the caller can keep the previous value
    /// instead of silently crafting something unintended.
    /// </remarks>
    internal static class MathExpression
    {
        /// <summary>Evaluates an expression.</summary>
        /// <param name="text">The text to evaluate.</param>
        /// <param name="result">The value, if it parsed.</param>
        /// <returns>Whether the whole string parsed as a valid expression.</returns>
        public static bool TryEvaluate(string text, out int result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            try
            {
                int position = 0;
                long value = ParseExpression(text, ref position);

                SkipSpace(text, ref position);
                if (position != text.Length)
                    return false; // trailing junk, e.g. "10*2x"

                result = (int)Math.Clamp(value, int.MinValue, int.MaxValue);
                return true;
            }
            catch
            {
                return false;
            }
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Parses addition and subtraction, the loosest-binding operators.</summary>
        private static long ParseExpression(string text, ref int position)
        {
            long value = ParseTerm(text, ref position);

            while (true)
            {
                SkipSpace(text, ref position);
                if (position >= text.Length)
                    return value;

                char op = text[position];
                if (op != '+' && op != '-')
                    return value;

                position++;
                long rhs = ParseTerm(text, ref position);
                value = op == '+' ? value + rhs : value - rhs;
            }
        }

        /// <summary>Parses multiplication and division.</summary>
        private static long ParseTerm(string text, ref int position)
        {
            long value = ParseFactor(text, ref position);

            while (true)
            {
                SkipSpace(text, ref position);
                if (position >= text.Length)
                    return value;

                char op = text[position];
                if (op != '*' && op != '/' && op != 'x' && op != 'X')
                    return value;

                position++;
                long rhs = ParseFactor(text, ref position);

                if (op == '/')
                {
                    if (rhs == 0)
                        throw new DivideByZeroException();
                    value /= rhs;
                }
                else
                    value *= rhs;
            }
        }

        /// <summary>Parses a number, a parenthesised expression, or a negated one.</summary>
        private static long ParseFactor(string text, ref int position)
        {
            SkipSpace(text, ref position);
            if (position >= text.Length)
                throw new FormatException("unexpected end of expression");

            char c = text[position];

            if (c == '-')
            {
                position++;
                return -ParseFactor(text, ref position);
            }

            if (c == '+')
            {
                position++;
                return ParseFactor(text, ref position);
            }

            if (c == '(')
            {
                position++;
                long inner = ParseExpression(text, ref position);

                SkipSpace(text, ref position);
                if (position >= text.Length || text[position] != ')')
                    throw new FormatException("unclosed bracket");

                position++;
                return inner;
            }

            int start = position;
            while (position < text.Length && char.IsDigit(text[position]))
                position++;

            if (position == start)
                throw new FormatException("expected a number");

            // Cap the literal itself so a long run of digits can't overflow before clamping.
            string digits = text.Substring(start, position - start);
            if (digits.Length > 9)
                return int.MaxValue;

            return long.Parse(digits, CultureInfo.InvariantCulture);
        }

        /// <summary>Advances past any whitespace.</summary>
        private static void SkipSpace(string text, ref int position)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
                position++;
        }
    }
}
