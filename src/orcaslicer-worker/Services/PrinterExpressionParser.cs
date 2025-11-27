using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Farm.Web.Shared;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// Parser for OrcaSlicer printer condition expressions (compatible_printers_condition).
/// 
/// Supports expressions like:
/// - "printer_notes=~/.*PRINTER_VENDOR_PRUSA3D.*/ and printer_notes=~/.*PRINTER_MODEL_MK3.*/ and nozzle_diameter[0]==0.4"
/// - "printer_notes=~/.*PRINTER_MODEL_VORON.*/"
/// 
/// Syntax:
/// - Regex matching: property=~/pattern/
/// - Equality: property==value or property[index]==value
/// - Logical operators: and, or
/// - Properties: printer_notes (from Settings), nozzle_diameter
/// </summary>
public static class PrinterExpressionParser
{
    /// <summary>
    /// Evaluates a compatible_printers_condition expression against machine metadata.
    /// Returns null if condition cannot be parsed (treated as empty condition).
    /// </summary>
    public static List<string>? EvaluateCondition(string condition, List<MachineProfileDto> availableMachines)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return null; // No condition, handled by caller
        }

        try
        {
            var matchingMachines = new List<string>();

            foreach (var machine in availableMachines)
            {
                if (EvaluateExpressionForMachine(condition, machine))
                {
                    matchingMachines.Add(machine.Name ?? "");
                }
            }

            return matchingMachines.Count > 0 ? matchingMachines : null;
        }
        catch
        {
            // If parsing fails, return null (condition will be skipped)
            return null;
        }
    }

    private static bool EvaluateExpressionForMachine(string expression, MachineProfileDto machine)
    {
        // Split by top-level 'and' and 'or' operators
        // Handle precedence: 'and' binds tighter than 'or'
        
        var tokens = TokenizeExpression(expression);
        return EvaluateTokens(tokens, machine);
    }

    private static bool EvaluateTokens(List<Token> tokens, MachineProfileDto machine)
    {
        // First pass: evaluate all comparison operators
        var evaluatedTokens = new List<Token>();
        
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            
            if (token.Type == TokenType.Comparison)
            {
                var result = EvaluateComparison(token.Value ?? "", machine);
                evaluatedTokens.Add(new Token { Type = TokenType.Boolean, Value = result.ToString().ToLowerInvariant() });
            }
            else
            {
                evaluatedTokens.Add(token);
            }
        }

        // Second pass: process logical operators (AND has higher precedence than OR)
        // Process all ANDs first
        var andProcessed = new List<Token>();
        for (int i = 0; i < evaluatedTokens.Count; i++)
        {
            if (i < evaluatedTokens.Count - 2 && 
                evaluatedTokens[i + 1].Type == TokenType.Operator && 
                evaluatedTokens[i + 1].Value == "and")
            {
                var left = evaluatedTokens[i].Value == "true";
                var right = evaluatedTokens[i + 2].Value == "true";
                var result = left && right;
                andProcessed.Add(new Token { Type = TokenType.Boolean, Value = result.ToString().ToLowerInvariant() });
                i += 2; // Skip operator and right operand
            }
            else if (evaluatedTokens[i].Type != TokenType.Operator)
            {
                andProcessed.Add(evaluatedTokens[i]);
            }
        }

        // Third pass: process ORs
        bool finalResult = andProcessed.Count > 0 && andProcessed[0].Value == "true";
        for (int i = 1; i < andProcessed.Count; i += 2)
        {
            if (i + 1 < andProcessed.Count)
            {
                var right = andProcessed[i + 1].Value == "true";
                finalResult = finalResult || right;
            }
        }

        return finalResult;
    }

    private static List<Token> TokenizeExpression(string expression)
    {
        var tokens = new List<Token>();
        var parts = expression.Split(new[] { " and ", " or " }, StringSplitOptions.None);
        
        // Track operators
        int operatorCount = 0;
        foreach (var part in parts)
        {
            tokens.Add(new Token { Type = TokenType.Comparison, Value = part.Trim() });
            
            // Add operator if not last part
            operatorCount++;
            if (operatorCount < parts.Length)
            {
                // Figure out which operator was used
                int findIndex = 0;
                for (int i = 0; i < operatorCount - 1; i++)
                {
                    int andIndex = expression.IndexOf(" and ", findIndex, StringComparison.OrdinalIgnoreCase);
                    int orIndex = expression.IndexOf(" or ", findIndex, StringComparison.OrdinalIgnoreCase);
                    
                    findIndex = andIndex >= 0 && (orIndex < 0 || andIndex < orIndex) ? andIndex + 5 : orIndex + 4;
                }
                
                var op = expression.Contains(" and ", StringComparison.OrdinalIgnoreCase) ? "and" : "or";
                tokens.Add(new Token { Type = TokenType.Operator, Value = op });
            }
        }

        return tokens;
    }

    private static bool EvaluateComparison(string comparison, MachineProfileDto machine)
    {
        comparison = comparison.Trim();

        // Handle regex matching: property=~/pattern/
        var regexMatch = Regex.Match(comparison, @"(\w+)\s*=~\s*/(.+)/", RegexOptions.IgnoreCase);
        if (regexMatch.Success)
        {
            var property = regexMatch.Groups[1].Value.ToLowerInvariant();
            var pattern = regexMatch.Groups[2].Value;
            
            return EvaluateRegexMatch(property, pattern, machine);
        }

        // Handle equality: property==value or property[index]==value
        var eqMatch = Regex.Match(comparison, @"(\w+)(?:\[(\d+)\])?\s*==\s*(.+)", RegexOptions.IgnoreCase);
        if (eqMatch.Success)
        {
            var property = eqMatch.Groups[1].Value.ToLowerInvariant();
            var indexStr = eqMatch.Groups[2].Value;
            var value = eqMatch.Groups[3].Value.Trim();

            return EvaluateEquality(property, indexStr, value, machine);
        }

        return false; // Unknown comparison format
    }

    private static bool EvaluateRegexMatch(string property, string pattern, MachineProfileDto machine)
    {
        var value = GetPropertyValue(property, machine);
        if (value == null)
            return false;

        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool EvaluateEquality(string property, string indexStr, string expectedValue, MachineProfileDto machine)
    {
        var value = GetPropertyValue(property, indexStr, machine);
        if (value == null)
            return false;

        // Normalize comparison (trim quotes, handle numeric comparison)
        expectedValue = expectedValue.Trim('"', '\'');
        
        if (double.TryParse(value, out var numValue) && double.TryParse(expectedValue, out var expectedNum))
        {
            return Math.Abs(numValue - expectedNum) < 0.0001; // Float comparison with tolerance
        }

        return value.Equals(expectedValue, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetPropertyValue(string property, MachineProfileDto machine)
    {
        return property.ToLowerInvariant() switch
        {
            "printer_notes" => ExtractPrinterNotes(machine),
            "name" => machine.Name,
            _ => null
        };
    }

    private static string? ExtractPrinterNotes(MachineProfileDto machine)
    {
        // printer_notes are stored in Settings dictionary
        if (machine.Settings != null && machine.Settings.TryGetValue("printer_notes", out var notes))
        {
            return notes?.ToString();
        }

        // Fallback: try to find from raw JSON
        if (machine.Settings != null && machine.Settings.TryGetValue("printer_model", out var model))
        {
            return model?.ToString();
        }

        return null;
    }

    private static string? GetPropertyValue(string property, string? indexStr, MachineProfileDto machine)
    {
        // Properties that support indexing
        if (property.Equals("nozzle_diameter", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(indexStr) || !int.TryParse(indexStr, out int index))
            {
                return null;
            }

            // First try to use the dedicated NozzleDiameter property
            if (index == 0 && machine.NozzleDiameter.HasValue)
            {
                return machine.NozzleDiameter.Value.ToString("F1");
            }

            // Try Settings dictionary
            if (machine.Settings != null && machine.Settings.TryGetValue("nozzle_diameter", out var nozzle))
            {
                if (index == 0 && nozzle != null)
                {
                    return nozzle.ToString();
                }
            }

            // Try to extract from name as fallback
            var nozzleDiameter = ExtractNozzleDiameterFromName(machine.Name);
            if (index == 0 && nozzleDiameter != null)
            {
                return nozzleDiameter;
            }

            return null;
        }

        return GetPropertyValue(property, machine);
    }

    private static string? ExtractNozzleDiameterFromName(string? machineName)
    {
        if (string.IsNullOrEmpty(machineName))
            return null;

        // Try to extract nozzle diameter from name
        // Common patterns: "X 0.4 nozzle", "X (0.4)", "X 0.6mm", etc.
        var matches = Regex.Matches(machineName, @"(\d+\.?\d*)\s*mm?", RegexOptions.IgnoreCase);
        if (matches.Count > 0)
        {
            // Last number is likely the nozzle diameter (first is usually part of model name)
            var lastMatch = matches[matches.Count - 1];
            return lastMatch.Groups[1].Value;
        }

        return null;
    }

    private class Token
    {
        public TokenType Type { get; set; }
        public string? Value { get; set; }
    }

    private enum TokenType
    {
        Comparison,
        Operator,
        Boolean
    }
}
