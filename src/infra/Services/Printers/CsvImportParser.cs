using System;
using System.Collections.Generic;
using System.Text;

namespace Farm.Infrastructure.Services.Printers;

public static class CsvImportParser
{
    public static string[] SplitCsvLine(string? line)
    {
        if (line is null)
        {
            return Array.Empty<string>();
        }

        List<string> values = new List<string>();
        StringBuilder current = new StringBuilder();
        bool inQuotes = false;

        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    _ = current.Append('"');
                    i += 2;
                }
                else
                {
                    inQuotes = !inQuotes;
                    i++;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                _ = current.Clear();
                i++;
            }
            else
            {
                _ = current.Append(c);
                i++;
            }
        }

        values.Add(current.ToString());
        return values.ToArray();
    }
}
