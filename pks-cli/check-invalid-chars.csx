#!/usr/bin/env dotnet-script

using System;
using System.IO;
using System.Linq;

var invalidChars = Path.GetInvalidFileNameChars();
Console.WriteLine($"Total invalid chars: {invalidChars.Length}");
Console.WriteLine($"Invalid chars: [{string.Join(", ", invalidChars.Select(c => $"'{c}' ({(int)c})"))}]");

// Check specific chars from the test
var testChars = new[] { '/', '\\', ':', '*', '?', '<', '>', '|', '"' };
foreach (var c in testChars)
{
    var isInvalid = invalidChars.Contains(c);
    Console.WriteLine($"'{c}' is {(isInvalid ? "INVALID" : "VALID")} according to Path.GetInvalidFileNameChars()");
}