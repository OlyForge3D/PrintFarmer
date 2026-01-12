using System.Text.RegularExpressions;

string prusakLine = "; perimeters = 2";
string orcaLine = "; wall_loops = 2";

var prusmatch = Regex.Match(prusakLine, @"(?:wall_loops|perimeters)\s*[:=]\s*(\d+)", RegexOptions.IgnoreCase);
var orcamatch = Regex.Match(orcaLine, @"(?:wall_loops|perimeters)\s*[:=]\s*(\d+)", RegexOptions.IgnoreCase);

Console.WriteLine($"Prusa matches: {prusmatch.Success}, value: {(prusmatch.Success ? prusmatch.Groups[1].Value : "none")}");
Console.WriteLine($"Orca matches: {orcamatch.Success}, value: {(orcamatch.Success ? orcamatch.Groups[1].Value : "none")}");
