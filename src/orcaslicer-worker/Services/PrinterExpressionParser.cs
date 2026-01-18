using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Farm.Infrastructure;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// Parser for OrcaSlicer printer condition expressions (compatible_printers_condition).
/// 
/// Evaluates arbitrary boolean expressions with proper operator precedence:
/// - Regex matching: property=~/pattern/
/// - Equality: property==value or property[index]==value  
/// - Logical operators: and, or (AND has higher precedence than OR)
/// - Properties: printer_notes (from Settings), nozzle_diameter
///
/// Examples:
/// - printer_notes=~/.*PRINTER_VENDOR_PRUSA3D.*/ and nozzle_diameter[0]==0.4
/// - printer_notes=~/.*PRINTER_MODEL_COREONE.*/ and nozzle_diameter[0]==0.8 and printer_notes=~/.*HF_NOZZLE.*/
/// </summary>
public static class PrinterExpressionParser
{
    /// <summary>
    /// Evaluates a compatible_printers_condition expression against available machines.
    /// Returns list of matching machine names, or null if expression cannot be evaluated.
    /// </summary>
    public static List<string>? EvaluateCondition(string condition, List<MachineProfileDto> availableMachines)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return null;
        }

        try
        {
            List<string> matchingMachines = new List<string>();

            foreach (MachineProfileDto machine in availableMachines)
            {
                if (EvaluateExpression(condition, machine))
                {
                    matchingMachines.Add(machine.Name ?? "");
                }
            }

            return matchingMachines.Count > 0 ? matchingMachines : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool EvaluateExpression(string expression, MachineProfileDto machine)
    {
        try
        {
            ExpressionParser parser = new ExpressionParser(expression, machine);
            return parser.Parse();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Recursive descent parser for boolean expressions with proper operator precedence.
    /// Precedence: comparison (highest) > AND > OR (lowest)
    /// </summary>
    private class ExpressionParser(string expression, MachineProfileDto machine)
    {
        private readonly string _expression = expression;
        private readonly MachineProfileDto _machine = machine;
        private int _position = 0;

        public bool Parse()
        {
            SkipWhitespace();
            bool result = ParseOr();
            SkipWhitespace();
            return _position < _expression.Length ? throw new FormatException($"Unexpected characters at position {_position}") : result;
        }

        private bool ParseOr()
        {
            bool left = ParseAnd();

            while (PeekKeyword("or"))
            {
                ConsumeKeyword("or");
                SkipWhitespace();
                bool right = ParseAnd();
                left = left || right;
            }

            return left;
        }

        private bool ParseAnd()
        {
            bool left = ParseComparison();

            while (PeekKeyword("and"))
            {
                ConsumeKeyword("and");
                SkipWhitespace();
                bool right = ParseComparison();
                left = left && right;
            }

            return left;
        }

        private bool ParseComparison()
        {
            SkipWhitespace();

            // Parse property with optional index: property or property[index]
            (string? property, int? index) = ParseProperty();

            SkipWhitespace();

            // Check for regex match: =~
            if (Peek("=~"))
            {
                Consume('=');
                Consume('~');
                SkipWhitespace();
                string pattern = ParseRegexPattern();
                return EvaluateRegexMatch(property, pattern);
            }

            // Check for equality: ==
            if (Peek("=="))
            {
                Consume('=');
                Consume('=');
                SkipWhitespace();
                string value = ParseValue();
                return EvaluateEquality(property, index, value);
            }

            throw new FormatException($"Invalid comparison at position {_position}");
        }

        private (string property, int? index) ParseProperty()
        {
            string property = ReadIdentifier();
            int? index = null;

            SkipWhitespace();

            if (Peek('['))
            {
                Consume('[');
                SkipWhitespace();
                index = int.Parse(ReadNumber());
                SkipWhitespace();
                Consume(']');
            }

            return (property, index);
        }

        private string ParseRegexPattern()
        {
            Consume('/');
            StringBuilder pattern = new StringBuilder();
            while (!Peek("/") && _position < _expression.Length)
            {
                _ = pattern.Append(_expression[_position++]);
            }
            Consume('/');
            return pattern.ToString();
        }

        private string ParseValue()
        {
            SkipWhitespace();

            // Handle quoted strings
            if (Peek('"'))
            {
                Consume('"');
                StringBuilder value = new StringBuilder();
                while (!Peek('"') && _position < _expression.Length)
                {
                    _ = value.Append(_expression[_position++]);
                }
                Consume('"');
                return value.ToString();
            }

            // Handle unquoted numbers or identifiers
            char? ch = Peek();
            return ch.HasValue && (char.IsDigit(ch.Value) || (ch == '-' && _position + 1 < _expression.Length && char.IsDigit(_expression[_position + 1])))
                ? ReadNumber()
                : ReadIdentifier();
        }

        private string ReadIdentifier()
        {
            StringBuilder id = new StringBuilder();
            while (_position < _expression.Length && (char.IsLetterOrDigit(_expression[_position]) || _expression[_position] == '_' || _expression[_position] == '.'))
            {
                _ = id.Append(_expression[_position++]);
            }
            return id.ToString();
        }

        private string ReadNumber()
        {
            StringBuilder num = new StringBuilder();
            if (Peek('-'))
            {
                _ = num.Append(_expression[_position++]);
            }

            while (_position < _expression.Length && (char.IsDigit(_expression[_position]) || _expression[_position] == '.'))
            {
                _ = num.Append(_expression[_position++]);
            }
            return num.ToString();
        }

        private void SkipWhitespace()
        {
            while (_position < _expression.Length && char.IsWhiteSpace(_expression[_position]))
            {
                _position++;
            }
        }

        private bool Peek(string str)
        {
            return _expression.Substring(_position).StartsWith(str, StringComparison.Ordinal);
        }

        private bool Peek(char ch)
        {
            return _position < _expression.Length && _expression[_position] == ch;
        }

        private char? Peek()
        {
            return _position < _expression.Length ? _expression[_position] : null;
        }

        private bool PeekKeyword(string keyword)
        {
            if (!Peek(keyword))
            {
                return false;
            }

            // Ensure it's a whole word (not part of another identifier)
            int endPos = _position + keyword.Length;
            if (endPos < _expression.Length)
            {
                char nextChar = _expression[endPos];
                return !char.IsLetterOrDigit(nextChar) && nextChar != '_';
            }

            return true;
        }

        private void Consume(char ch)
        {
            if (!Peek(ch))
            {
                throw new FormatException($"Expected '{ch}' at position {_position}");
            }

            _position++;
        }

        private void ConsumeKeyword(string keyword)
        {
            if (!Peek(keyword))
            {
                throw new FormatException($"Expected '{keyword}' at position {_position}");
            }

            _position += keyword.Length;
        }

        private bool EvaluateRegexMatch(string property, string pattern)
        {
            string? value = GetPropertyValue(property);
            if (value == null)
            {
                return false;
            }

            try
            {
                return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private bool EvaluateEquality(string property, int? index, string expectedValue)
        {
            string? value = GetPropertyValue(property, index);
            if (value == null)
            {
                return false;
            }

            // Numeric comparison with tolerance
            if (double.TryParse(value, out double numValue) && double.TryParse(expectedValue, out double expectedNum))
            {
                return Math.Abs(numValue - expectedNum) < 0.0001;
            }

            // String comparison
            return value.Equals(expectedValue, StringComparison.OrdinalIgnoreCase);
        }

        private string? GetPropertyValue(string property)
        {
            return property.ToLowerInvariant() switch
            {
                "printer_notes" => ExtractPrinterNotes(_machine),
                "name" => _machine.Name,
                _ => null
            };
        }

        private string? GetPropertyValue(string property, int? index)
        {
            // Properties with indexing support
            if (property.Equals("nozzle_diameter", StringComparison.OrdinalIgnoreCase))
            {
                if (!index.HasValue || index < 0)
                {
                    return null;
                }

                // Try dedicated property first
                if (index == 0 && _machine.NozzleDiameter.HasValue)
                {
                    return _machine.NozzleDiameter.Value.ToString("G");
                }

                // Try settings
                if (_machine.Settings != null && _machine.Settings.TryGetValue("nozzle_diameter", out object? nozzle))
                {
                    if (index == 0 && nozzle != null)
                    {
                        return nozzle.ToString();
                    }
                }

                // Try extracting from name as fallback
                if (index == 0)
                {
                    return ExtractNozzleDiameterFromName(_machine.Name);
                }
            }

            return GetPropertyValue(property);
        }

        private static string? ExtractPrinterNotes(MachineProfileDto machine)
        {
            return machine.Settings != null && machine.Settings.TryGetValue("printer_notes", out object? notes) ? (notes?.ToString()) : null;
        }

        private static string? ExtractNozzleDiameterFromName(string? machineName)
        {
            if (string.IsNullOrEmpty(machineName))
            {
                return null;
            }

            // Look for pattern: space + number + optional decimal + space/end/nozzle
            // e.g., "Prusa MK4S 0.25 nozzle" → "0.25"
            Match match = Regex.Match(machineName, @"\s(\d+\.?\d*)\s*(nozzle|mm)?(\s|$)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
